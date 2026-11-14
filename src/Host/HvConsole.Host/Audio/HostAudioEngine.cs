// ============================================================================
// HostAudioEngine.cs - Low-Latency WASAPI Speaker Render & Privacy-Locked Mic
// Project: VMStream - Host Console Client (HvConsole.Host)
//
// Strict Privacy Architecture:
//   1. Default Microphone state on startup and VM connect is OFF (DISCONNECTED).
//   2. When OFF (or when GateMicOnGuestKsState=true and no Guest app has opened
//      the microphone pin in KSSTATE_RUN), the Host holds ZERO open IAudioClient
//      handles - ensuring the Windows 11 system tray mic icon stays completely OFF.
//   3. Toggling OFF immediately stops and releases IAudioCaptureClient,
//      IAudioClient, and IMMDevice via Marshal.FinalReleaseComObject.
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HvConsole.Host.Protocol;
using HvConsole.Host.Transport;

namespace HvConsole.Host.Audio;

public sealed class HostAudioEngine : IDisposable
{
    private const uint CLSCTX_ALL = 23;
    private const long ReftimesPerMillisecond = 10_000; // 100-ns units per ms

    private readonly IHvTransport _audioTransport;
    private readonly AdaptiveJitterBuffer _jitterBuffer = new();
    private readonly object _micStateLock = new();

    private Thread? _renderThread;
    private Thread? _captureThread;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _captureCts;

    private float _speakerVolume = 1.0f;
    private volatile bool _speakerMuted;

    // Strict Privacy Default = FALSE (OFF / Disconnected)
    private volatile bool _isMicUserEnabled;
    private volatile bool _isGuestCaptureActive;
    private volatile bool _isGuestRenderActive;
    private volatile bool _gateMicOnGuestKsState = true;
    private volatile bool _isMicHardwareOpen;
    private bool _disposed;

    public AdaptiveJitterBuffer JitterBuffer => _jitterBuffer;

    public float SpeakerVolume
    {
        get => Volatile.Read(ref _speakerVolume);
        set => Volatile.Write(ref _speakerVolume, Math.Clamp(value, 0.0f, 1.0f));
    }

    public bool SpeakerMuted
    {
        get => _speakerMuted;
        set => _speakerMuted = value;
    }

    /// <summary>
    /// User-controlled Microphone Privacy Switch (Ctrl+Alt+Shift+M or HUD button).
    /// Always defaults to FALSE on startup and on VM connect.
    /// </summary>
    public bool IsMicUserEnabled => _isMicUserEnabled;

    /// <summary>
    /// True when an application inside the Guest VM has opened "Hyper-V Console Microphone"
    /// in KSSTATE_RUN (reported via inverted-call IOCTL -> HvPacketType.AudioControl).
    /// </summary>
    public bool IsGuestCaptureActive => _isGuestCaptureActive;

    /// <summary>
    /// True when an application inside the Guest VM is playing audio (KSSTATE_RUN).
    /// </summary>
    public bool IsGuestRenderActive => _isGuestRenderActive;

    /// <summary>
    /// True ONLY when this Host process actually holds an open WASAPI IAudioClient capture handle.
    /// </summary>
    public bool IsMicHardwareOpen => _isMicHardwareOpen;

    /// <summary>
    /// When true (default), even after the user enables the Mic switch, the Host only opens
    /// physical WASAPI IAudioClient capture during the seconds a Guest app is in KSSTATE_RUN.
    /// </summary>
    public bool GateMicOnGuestKsState
    {
        get => _gateMicOnGuestKsState;
        set
        {
            _gateMicOnGuestKsState = value;
            ReevaluateMicrophoneHardwareGate();
        }
    }

    public event Action? StateChanged;

    public HostAudioEngine(IHvTransport audioTransport)
    {
        _audioTransport = audioTransport;
        _audioTransport.PacketReceived += OnAudioTransportPacketReceived;
        _audioTransport.ConnectionStateChanged += OnAudioTransportConnectionChanged;
    }

    public void Start()
    {
        if (_disposed || _renderThread != null) return;

        // Enforce strict privacy default on start: Mic OFF and hardware released
        _isMicUserEnabled = false;
        StopAndReleaseMicrophoneHardware();

        _renderCts = new CancellationTokenSource();
        _renderThread = new Thread(() => WasapiRenderLoop(_renderCts.Token))
        {
            Name = "VMStream-WASAPI-Render",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _renderThread.SetApartmentState(ApartmentState.MTA);
        _renderThread.Start();
    }

    public void Stop()
    {
        _isMicUserEnabled = false;
        StopAndReleaseMicrophoneHardware();

        if (_renderCts != null)
        {
            _renderCts.Cancel();
            if (_renderThread is { IsAlive: true })
            {
                _renderThread.Join(1500);
            }
            _renderCts.Dispose();
            _renderCts = null;
            _renderThread = null;
        }
    }

    /// <summary>
    /// Toggles the Host Microphone Forwarding switch ON or OFF.
    /// Returns the new state of IsMicUserEnabled.
    /// </summary>
    public bool ToggleMicrophonePrivacy()
    {
        SetMicrophonePrivacy(!_isMicUserEnabled);
        return _isMicUserEnabled;
    }

    public void SetMicrophonePrivacy(bool enabled)
    {
        lock (_micStateLock)
        {
            if (_isMicUserEnabled == enabled) return;
            _isMicUserEnabled = enabled;
        }

        // Notify Guest Bridge of updated Host Mic state
        var ctrl = new HvAudioControlPayload
        {
            GuestCaptureActive = _isGuestCaptureActive ? (byte)1 : (byte)0,
            GuestRenderActive = _isGuestRenderActive ? (byte)1 : (byte)0,
            HostMicEnabled = enabled ? (byte)1 : (byte)0,
            Reserved = 0,
            SequenceGeneration = 0
        };
        _audioTransport.SendStruct(HvPacketType.AudioControl, in ctrl);

        ReevaluateMicrophoneHardwareGate();
        StateChanged?.Invoke();
    }

    private void ReevaluateMicrophoneHardwareGate()
    {
        lock (_micStateLock)
        {
            bool shouldOpenHardware = _isMicUserEnabled &&
                                      _audioTransport.IsConnected &&
                                      (!_gateMicOnGuestKsState || _isGuestCaptureActive);

            if (shouldOpenHardware)
            {
                StartMicrophoneHardwareIfNeeded();
            }
            else
            {
                StopAndReleaseMicrophoneHardware();
            }
        }
        StateChanged?.Invoke();
    }

    private void StartMicrophoneHardwareIfNeeded()
    {
        if (_captureThread is { IsAlive: true })
        {
            return;
        }

        _captureCts = new CancellationTokenSource();
        CancellationToken token = _captureCts.Token;

        _captureThread = new Thread(() => WasapiCaptureLoop(token))
        {
            Name = "VMStream-WASAPI-MicCapture",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _captureThread.SetApartmentState(ApartmentState.MTA);
        _captureThread.Start();
    }

    /// <summary>
    /// Immediately stops the capture thread and releases all COM references to
    /// IAudioCaptureClient, IAudioClient, and IMMDevice so Windows extinguishes
    /// the system tray microphone privacy indicator.
    /// </summary>
    private void StopAndReleaseMicrophoneHardware()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _captureCts, null);
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
        }

        Thread? thread = Interlocked.Exchange(ref _captureThread, null);
        if (thread is { IsAlive: true } && Thread.CurrentThread != thread)
        {
            thread.Join(1000);
        }

        cts?.Dispose();
        _isMicHardwareOpen = false;
    }

    private unsafe void OnAudioTransportPacketReceived(HvReceivedPacket packet)
    {
        var type = (HvPacketType)packet.Header.Type;

        if (type == HvPacketType.AudioRenderPcm &&
            packet.Payload.Length > HvAudioFrameHeader.Size)
        {
            ReadOnlySpan<byte> payloadSpan = packet.Payload.AsSpan();
            ref readonly var frameHdr = ref Unsafe.As<byte, HvAudioFrameHeader>(ref MemoryMarshal.GetReference(payloadSpan));

            if (((HvAudioFlags)frameHdr.Flags & HvAudioFlags.Silence) == 0)
            {
                ReadOnlySpan<byte> pcmBytes = payloadSpan.Slice(HvAudioFrameHeader.Size);
                _jitterBuffer.PushInterleavedStereo16(pcmBytes);
            }
        }
        else if (type == HvPacketType.AudioControl &&
                 packet.Payload.Length >= HvAudioControlPayload.Size)
        {
            fixed (byte* p = packet.Payload)
            {
                var ctrl = *(HvAudioControlPayload*)p;
                _isGuestCaptureActive = ctrl.GuestCaptureActive != 0;
                _isGuestRenderActive = ctrl.GuestRenderActive != 0;
            }
            ReevaluateMicrophoneHardwareGate();
        }
    }

    private void OnAudioTransportConnectionChanged(bool connected)
    {
        if (!connected)
        {
            _jitterBuffer.Reset();
            _isGuestCaptureActive = false;
            _isGuestRenderActive = false;
            // Strict requirement: Default state on VM connect/disconnect is MUTED / DISCONNECTED
            _isMicUserEnabled = false;
            StopAndReleaseMicrophoneHardware();
        }
        else
        {
            // Enforce strict privacy default = OFF on every new VM connection
            _isMicUserEnabled = false;
            StopAndReleaseMicrophoneHardware();
        }

        StateChanged?.Invoke();
    }

    // ========================================================================
    // WASAPI Speaker Render Thread (eRender, eConsole, 48kHz 16-bit Stereo)
    // ========================================================================
    private unsafe void WasapiRenderLoop(CancellationToken token)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioRenderClient? renderClient = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out device);
            if (hr < 0 || device == null) return;

            Guid iidAudioClient = WasapiGuids.IID_IAudioClient;
            hr = device.Activate(ref iidAudioClient, CLSCTX_ALL, IntPtr.Zero, out object audioClientObj);
            if (hr < 0 || audioClientObj == null) return;
            audioClient = (IAudioClient)audioClientObj;

            WAVEFORMATEX waveFmt = WAVEFORMATEX.CreatePcm(
                HvProtocolConstants.AudioSampleRate,
                HvProtocolConstants.AudioChannelsStereo,
                HvProtocolConstants.AudioBitsPerSample16
            );

            // 25 ms shared-mode buffer with automatic high-quality Windows SRC conversion
            long bufferDurationHns = 25 * ReftimesPerMillisecond;
            var flags = AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;

            hr = audioClient.Initialize(
                AudioClientShareMode.Shared,
                flags,
                bufferDurationHns,
                0,
                ref waveFmt,
                IntPtr.Zero
            );
            if (hr < 0) return;

            hr = audioClient.GetBufferSize(out uint bufferFrameCount);
            if (hr < 0) return;

            Guid iidRenderClient = WasapiGuids.IID_IAudioRenderClient;
            hr = audioClient.GetService(ref iidRenderClient, out object renderClientObj);
            if (hr < 0 || renderClientObj == null) return;
            renderClient = (IAudioRenderClient)renderClientObj;

            audioClient.Start();

            while (!token.IsCancellationRequested)
            {
                if (audioClient.GetCurrentPadding(out uint numFramesPadding) >= 0)
                {
                    uint numFramesAvailable = bufferFrameCount - numFramesPadding;
                    if (numFramesAvailable >= 96) // >= 2 ms space available
                    {
                        if (renderClient.GetBuffer(numFramesAvailable, out IntPtr dataPtr) >= 0 && dataPtr != IntPtr.Zero)
                        {
                            int byteCount = (int)numFramesAvailable * HvProtocolConstants.AudioBlockAlignStereo16;
                            var dstSpan = new Span<byte>((void*)dataPtr, byteCount);
                            _jitterBuffer.PullInterleavedStereo16(dstSpan, SpeakerVolume, _speakerMuted);
                            renderClient.ReleaseBuffer(numFramesAvailable, AudioClientBufferFlags.None);
                        }
                    }
                }

                Thread.Sleep(4);
            }

            audioClient.Stop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HostAudioEngine] WasapiRenderLoop exception: {ex.Message}");
        }
        finally
        {
            SafeReleaseComObject(ref renderClient);
            SafeReleaseComObject(ref audioClient);
            SafeReleaseComObject(ref device);
            SafeReleaseComObject(ref enumerator);
        }
    }

    // ========================================================================
    // WASAPI Microphone Capture Thread (eCapture, eCommunications)
    // Strictly opened ONLY when IsMicUserEnabled == true and gated on KSSTATE_RUN
    // ========================================================================
    private unsafe void WasapiCaptureLoop(CancellationToken token)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out device);
            if (hr < 0 || device == null)
            {
                hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out device);
                if (hr < 0 || device == null) return;
            }

            Guid iidAudioClient = WasapiGuids.IID_IAudioClient;
            hr = device.Activate(ref iidAudioClient, CLSCTX_ALL, IntPtr.Zero, out object audioClientObj);
            if (hr < 0 || audioClientObj == null) return;
            audioClient = (IAudioClient)audioClientObj;

            WAVEFORMATEX waveFmt = WAVEFORMATEX.CreatePcm(
                HvProtocolConstants.AudioSampleRate,
                HvProtocolConstants.AudioChannelsStereo,
                HvProtocolConstants.AudioBitsPerSample16
            );

            // 20 ms shared-mode capture buffer with Windows SRC conversion to 48kHz 16-bit Stereo
            long bufferDurationHns = 20 * ReftimesPerMillisecond;
            var flags = AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;

            hr = audioClient.Initialize(
                AudioClientShareMode.Shared,
                flags,
                bufferDurationHns,
                0,
                ref waveFmt,
                IntPtr.Zero
            );
            if (hr < 0) return;

            Guid iidCaptureClient = WasapiGuids.IID_IAudioCaptureClient;
            hr = audioClient.GetService(ref iidCaptureClient, out object captureClientObj);
            if (hr < 0 || captureClientObj == null) return;
            captureClient = (IAudioCaptureClient)captureClientObj;

            hr = audioClient.Start();
            if (hr < 0) return;

            _isMicHardwareOpen = true;
            StateChanged?.Invoke();

            byte[] accumBuffer = new byte[HvProtocolConstants.AudioPeriodBytesStereo16];
            int accumOffset = 0;

            Span<byte> frameHdrBytes = stackalloc byte[HvAudioFrameHeader.Size];
            ref var frameHdr = ref Unsafe.As<byte, HvAudioFrameHeader>(ref frameHdrBytes[0]);
            frameHdr.SampleRate = HvProtocolConstants.AudioSampleRate;
            frameHdr.Channels = HvProtocolConstants.AudioChannelsStereo;
            frameHdr.BitsPerSample = HvProtocolConstants.AudioBitsPerSample16;
            frameHdr.FrameSamples = HvProtocolConstants.AudioFramesPerPeriod;
            frameHdr.Flags = (ushort)HvAudioFlags.MicActive;

            while (!token.IsCancellationRequested && _isMicUserEnabled && _audioTransport.IsConnected)
            {
                while (captureClient.GetNextPacketSize(out uint packetFrames) >= 0 && packetFrames > 0)
                {
                    if (captureClient.GetBuffer(
                            out IntPtr dataPtr,
                            out uint numFramesToRead,
                            out AudioClientBufferFlags bufFlags,
                            out _,
                            out _) >= 0)
                    {
                        int bytesAvailable = (int)numFramesToRead * HvProtocolConstants.AudioBlockAlignStereo16;
                        bool isSilent = (bufFlags & AudioClientBufferFlags.Silent) != 0 || dataPtr == IntPtr.Zero;

                        int srcOffset = 0;
                        while (srcOffset < bytesAvailable)
                        {
                            int copyLen = Math.Min(
                                bytesAvailable - srcOffset,
                                HvProtocolConstants.AudioPeriodBytesStereo16 - accumOffset
                            );

                            if (isSilent)
                            {
                                Array.Clear(accumBuffer, accumOffset, copyLen);
                            }
                            else
                            {
                                Marshal.Copy(IntPtr.Add(dataPtr, srcOffset), accumBuffer, accumOffset, copyLen);
                            }

                            accumOffset += copyLen;
                            srcOffset += copyLen;

                            if (accumOffset == HvProtocolConstants.AudioPeriodBytesStereo16)
                            {
                                _audioTransport.SendHeaderAndBytes(
                                    HvPacketType.AudioCapturePcm,
                                    frameHdrBytes,
                                    accumBuffer
                                );
                                accumOffset = 0;
                            }
                        }

                        captureClient.ReleaseBuffer(numFramesToRead);
                    }
                    else
                    {
                        break;
                    }
                }

                Thread.Sleep(5);
            }

            audioClient.Stop();
            audioClient.Reset();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HostAudioEngine] WasapiCaptureLoop exception: {ex.Message}");
        }
        finally
        {
            // Deterministically release every WASAPI COM interface so Windows 11
            // immediately extinguishes the microphone privacy icon in the system tray!
            SafeReleaseComObject(ref captureClient);
            SafeReleaseComObject(ref audioClient);
            SafeReleaseComObject(ref device);
            SafeReleaseComObject(ref enumerator);

            _isMicHardwareOpen = false;
            StateChanged?.Invoke();
        }
    }

    private static void SafeReleaseComObject<T>(ref T? comObj) where T : class
    {
        T? target = Interlocked.Exchange(ref comObj, null);
        if (target != null && Marshal.IsComObject(target))
        {
            try
            {
                Marshal.FinalReleaseComObject(target);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audioTransport.PacketReceived -= OnAudioTransportPacketReceived;
        _audioTransport.ConnectionStateChanged -= OnAudioTransportConnectionChanged;
        Stop();
    }
}
