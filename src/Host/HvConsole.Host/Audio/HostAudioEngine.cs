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
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HvConsole.Host.Protocol;
using HvConsole.Host.Transport;

namespace HvConsole.Host.Audio;

public enum AudioSampleRatePreference
{
    Auto = 0,
    Hz44100 = HvProtocolConstants.AudioSampleRate44100,
    Hz48000 = HvProtocolConstants.AudioSampleRate48000
}

public static class AudioRateSettings
{
    private static readonly string SettingsDirPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VMStream"
    );

    private static readonly string RateFilePath = Path.Combine(SettingsDirPath, "audio_rate.txt");

    public static AudioSampleRatePreference LoadSavedPreference()
    {
        try
        {
            if (File.Exists(RateFilePath))
            {
                string raw = File.ReadAllText(RateFilePath).Trim();
                if (TryParse(raw, out AudioSampleRatePreference parsed))
                {
                    return parsed;
                }
            }
        }
        catch { }

        return AudioSampleRatePreference.Auto;
    }

    public static void SavePreference(AudioSampleRatePreference preference)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirPath);
            string text = preference switch
            {
                AudioSampleRatePreference.Hz44100 => "44100",
                AudioSampleRatePreference.Hz48000 => "48000",
                _ => "auto"
            };
            File.WriteAllText(RateFilePath, text);
        }
        catch { }
    }

    public static bool TryParse(string? raw, out AudioSampleRatePreference preference)
    {
        preference = AudioSampleRatePreference.Auto;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string normalized = raw.Trim().ToLowerInvariant();
        if (normalized is "44100" or "44.1" or "44.1k" or "44.1khz" or "44k")
        {
            preference = AudioSampleRatePreference.Hz44100;
            return true;
        }
        if (normalized is "48000" or "48" or "48k" or "48khz")
        {
            preference = AudioSampleRatePreference.Hz48000;
            return true;
        }
        if (normalized is "auto" or "default" or "0")
        {
            preference = AudioSampleRatePreference.Auto;
            return true;
        }

        return false;
    }

    public static string FormatDisplay(AudioSampleRatePreference preference) => preference switch
    {
        AudioSampleRatePreference.Hz44100 => "44,100 Hz (44.1 kHz)",
        AudioSampleRatePreference.Hz48000 => "48,000 Hz (48 kHz)",
        _ => "Auto (Match Host Device)"
    };
}

public sealed class HostAudioEngine : IDisposable
{
    private const uint CLSCTX_ALL = 23;
    private const long ReftimesPerMillisecond = 10_000; // 100-ns units per ms
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    private const int AUDCLNT_E_BUFFER_TOO_LARGE = unchecked((int)0x88890006);

    private readonly IHvTransport _audioTransport;
    private readonly AdaptiveJitterBuffer _jitterBuffer = new();
    private readonly object _micStateLock = new();
    private readonly object _renderRestartLock = new();
    private readonly bool _speakersEnabled;
    private readonly bool _microphoneAllowed;

    private Thread? _renderThread;
    private Thread? _captureThread;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _renderRestartCts;
    private CancellationTokenSource? _captureCts;

    private float _speakerVolume = 1.0f;
    private volatile bool _speakerMuted;
    private volatile int _sampleRatePreference;
    private volatile int _activeRenderSampleRate = HvProtocolConstants.AudioSampleRate;
    private volatile int _activeGuestRenderSampleRate = HvProtocolConstants.AudioSampleRate;
    private volatile int _activeGuestCaptureSampleRate = HvProtocolConstants.AudioSampleRate;
    private volatile bool _renderUsesFloat32;
    private volatile bool _renderUsesEventCallback;

    // Strict Privacy Default = FALSE (OFF / Disconnected)
    private volatile bool _isMicUserEnabled;
    private volatile bool _isGuestCaptureActive;
    private volatile bool _isGuestRenderActive;
    private volatile bool _gateMicOnGuestKsState = true;
    private volatile bool _isMicHardwareOpen;
    private bool _started;
    private bool _disposed;

    public AdaptiveJitterBuffer JitterBuffer => _jitterBuffer;
    public bool SpeakersEnabled => _speakersEnabled;
    public bool MicrophoneAllowed => _microphoneAllowed;

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

    public AudioSampleRatePreference SampleRatePreference => (AudioSampleRatePreference)_sampleRatePreference;
    public int ActiveRenderSampleRate => _activeRenderSampleRate;
    public int ActiveGuestRenderSampleRate => _activeGuestRenderSampleRate;
    public int ActiveGuestCaptureSampleRate => _activeGuestCaptureSampleRate;

    public int TargetCaptureSampleRate =>
        _activeGuestCaptureSampleRate == HvProtocolConstants.AudioSampleRate44100
            ? HvProtocolConstants.AudioSampleRate44100
            : HvProtocolConstants.AudioSampleRate48000;

    public bool RenderUsesFloat32 => _renderUsesFloat32;
    public bool RenderUsesEventCallback => _renderUsesEventCallback;

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

    public HostAudioEngine(
        IHvTransport audioTransport,
        AudioSampleRatePreference? initialSampleRate = null,
        bool speakersEnabled = true,
        bool microphoneAllowed = true)
    {
        _audioTransport = audioTransport;
        _sampleRatePreference = (int)(initialSampleRate ?? AudioRateSettings.LoadSavedPreference());
        _speakersEnabled = speakersEnabled;
        _microphoneAllowed = microphoneAllowed;
        _audioTransport.PacketReceived += OnAudioTransportPacketReceived;
        _audioTransport.ConnectionStateChanged += OnAudioTransportConnectionChanged;
    }

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;

        // Enforce strict privacy default on start: Mic OFF and hardware released
        _isMicUserEnabled = false;
        StopAndReleaseMicrophoneHardware();

        if (!_speakersEnabled)
        {
            return;
        }

        _renderCts = new CancellationTokenSource();
        CancellationToken stopToken = _renderCts.Token;
        _renderThread = new Thread(() => WasapiRenderLoop(stopToken))
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
        _started = false;
        _isMicUserEnabled = false;
        StopAndReleaseMicrophoneHardware();

        if (_renderCts != null)
        {
            _renderCts.Cancel();
            RestartRenderSession();
            if (_renderThread is { IsAlive: true })
            {
                _renderThread.Join(1500);
            }
            _renderCts.Dispose();
            _renderCts = null;
            _renderThread = null;
        }

        lock (_renderRestartLock)
        {
            _renderRestartCts?.Dispose();
            _renderRestartCts = null;
        }
    }

    public void SetSampleRatePreference(AudioSampleRatePreference preference, bool persist = true)
    {
        if (!Enum.IsDefined(preference))
        {
            preference = AudioSampleRatePreference.Auto;
        }

        if (persist)
        {
            AudioRateSettings.SavePreference(preference);
        }

        int previous = Interlocked.Exchange(ref _sampleRatePreference, (int)preference);
        if (previous == (int)preference)
        {
            return;
        }

        if (_audioTransport.IsConnected)
        {
            SendControlStatePacket();
        }

        RestartRenderSession();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Toggles the Host Microphone Forwarding switch ON or OFF.
    /// Returns the new state of IsMicUserEnabled.
    /// </summary>
    public bool ToggleMicrophonePrivacy()
    {
        if (!_microphoneAllowed)
        {
            SetMicrophonePrivacy(false);
            return false;
        }

        SetMicrophonePrivacy(!_isMicUserEnabled);
        return _isMicUserEnabled;
    }

    public void SetMicrophonePrivacy(bool enabled)
    {
        if (!_microphoneAllowed)
        {
            enabled = false;
        }

        lock (_micStateLock)
        {
            if (_isMicUserEnabled == enabled) return;
            _isMicUserEnabled = enabled;
        }

        SendControlStatePacket();
        ReevaluateMicrophoneHardwareGate();
        StateChanged?.Invoke();
    }

    private void SendControlStatePacket()
    {
        if (!_audioTransport.IsConnected) return;

        var ctrl = new HvAudioControlPayload
        {
            GuestCaptureActive = _isGuestCaptureActive ? (byte)1 : (byte)0,
            GuestRenderActive = _isGuestRenderActive ? (byte)1 : (byte)0,
            HostMicEnabled = (_microphoneAllowed && _isMicUserEnabled) ? (byte)1 : (byte)0,
            SampleRateConfig = HvProtocolConstants.PackAudioRateConfig(_sampleRatePreference, 0),
            SequenceGeneration = 0
        };
        _audioTransport.SendStruct(HvPacketType.AudioControl, in ctrl);
    }

    private void RestartRenderSession()
    {
        lock (_renderRestartLock)
        {
            try { _renderRestartCts?.Cancel(); } catch { }
        }
    }

    private void ReevaluateMicrophoneHardwareGate()
    {
        lock (_micStateLock)
        {
            bool shouldOpenHardware = _microphoneAllowed &&
                                      _isMicUserEnabled &&
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

            int rate = frameHdr.SampleRate >= 8000 && frameHdr.SampleRate <= 192000
                ? (int)frameHdr.SampleRate
                : HvProtocolConstants.AudioSampleRate;

            bool rateChanged = _activeGuestRenderSampleRate != rate;
            if (rateChanged)
            {
                _activeGuestRenderSampleRate = rate;
            }

            if (_speakersEnabled &&
                ((HvAudioFlags)frameHdr.Flags & HvAudioFlags.Silence) == 0 &&
                frameHdr.Channels == HvProtocolConstants.AudioChannelsStereo &&
                frameHdr.BitsPerSample == HvProtocolConstants.AudioBitsPerSample16)
            {
                ReadOnlySpan<byte> pcmBytes = payloadSpan.Slice(HvAudioFrameHeader.Size);
                _jitterBuffer.PushInterleavedStereo16(pcmBytes, rate);
            }

            if (rateChanged)
            {
                StateChanged?.Invoke();
            }
        }
        else if (type == HvPacketType.AudioControl &&
                 packet.Payload.Length >= HvAudioControlPayload.Size)
        {
            int guestRenderRate;
            int guestCaptureRate;
            bool guestRenderActive;
            fixed (byte* p = packet.Payload)
            {
                var ctrl = *(HvAudioControlPayload*)p;
                _isGuestCaptureActive = ctrl.GuestCaptureActive != 0;
                guestRenderActive = ctrl.GuestRenderActive != 0;
                _isGuestRenderActive = guestRenderActive;
                guestRenderRate = HvProtocolConstants.DecodeRenderRateFromConfig(ctrl.SampleRateConfig);
                guestCaptureRate = HvProtocolConstants.DecodeCaptureRateFromConfig(ctrl.SampleRateConfig);
            }

            _activeGuestRenderSampleRate = guestRenderRate;
            _activeGuestCaptureSampleRate = guestCaptureRate;
            _jitterBuffer.NotifyGuestRenderState(guestRenderActive);
            ReevaluateMicrophoneHardwareGate();
        }
    }

    private void OnAudioTransportConnectionChanged(bool connected)
    {
        if (!connected)
        {
            _jitterBuffer.Reset();
            _jitterBuffer.NotifyGuestRenderState(false);
            _isGuestCaptureActive = false;
            _isGuestRenderActive = false;
            // Strict requirement: Default state on VM connect/disconnect is MUTED / DISCONNECTED
            _isMicUserEnabled = false;
            StopAndReleaseMicrophoneHardware();
        }
        else
        {
            // Enforce strict privacy default = OFF on every new VM connection and advertise Host sample rate preference
            _isMicUserEnabled = false;
            StopAndReleaseMicrophoneHardware();
            SendControlStatePacket();
        }

        StateChanged?.Invoke();
    }

    // ========================================================================
    // WASAPI Speaker Render Thread (eRender, eConsole, Event-Driven Float32)
    // ========================================================================
    private void WasapiRenderLoop(CancellationToken stopToken)
    {
        IntPtr avrtHandle = IntPtr.Zero;
        try
        {
            uint taskIndex = 0;
            try
            {
                avrtHandle = AvrtNative.AvSetMmThreadCharacteristicsW("Pro Audio", out taskIndex);
            }
            catch { }

            while (!stopToken.IsCancellationRequested)
            {
                CancellationTokenSource restartCts;
                lock (_renderRestartLock)
                {
                    _renderRestartCts?.Dispose();
                    _renderRestartCts = new CancellationTokenSource();
                    restartCts = _renderRestartCts;
                }

                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken, restartCts.Token);
                RunRenderSession(sessionCts.Token);

                if (!stopToken.IsCancellationRequested && !restartCts.IsCancellationRequested)
                {
                    stopToken.WaitHandle.WaitOne(500);
                }
            }
        }
        finally
        {
            if (avrtHandle != IntPtr.Zero)
            {
                try { AvrtNative.AvRevertMmThreadCharacteristics(avrtHandle); } catch { }
            }
        }
    }

    private unsafe void RunRenderSession(CancellationToken sessionToken)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioRenderClient? renderClient = null;
        AutoResetEvent? audioEvent = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out device);
            if (hr < 0 || device == null) return;

            int mixSampleRate = GetDeviceMixSampleRate(device);
            AudioSampleRatePreference pref = (AudioSampleRatePreference)_sampleRatePreference;
            int targetSampleRate = pref switch
            {
                AudioSampleRatePreference.Hz44100 => HvProtocolConstants.AudioSampleRate44100,
                AudioSampleRatePreference.Hz48000 => HvProtocolConstants.AudioSampleRate48000,
                _ => mixSampleRate
            };

            if (!TryCreateInitializedRenderClient(
                    device,
                    targetSampleRate,
                    out audioClient,
                    out bool usingFloat32,
                    out bool usingEventCallback,
                    out audioEvent) ||
                audioClient == null)
            {
                return;
            }

            hr = audioClient.GetBufferSize(out uint bufferFrameCount);
            if (hr < 0 || bufferFrameCount == 0) return;

            Guid iidRenderClient = WasapiGuids.IID_IAudioRenderClient;
            hr = audioClient.GetService(ref iidRenderClient, out object renderClientObj);
            if (hr < 0 || renderClientObj == null) return;
            renderClient = (IAudioRenderClient)renderClientObj;

            // Pre-fill entire shared WASAPI buffer with silence before Start() to avoid initial pop
            if (renderClient.GetBuffer(bufferFrameCount, out _) >= 0)
            {
                renderClient.ReleaseBuffer(bufferFrameCount, AudioClientBufferFlags.Silent);
            }

            _jitterBuffer.SetOutputSampleRate(targetSampleRate);
            _activeRenderSampleRate = targetSampleRate;
            _renderUsesFloat32 = usingFloat32;
            _renderUsesEventCallback = usingEventCallback;
            StateChanged?.Invoke();

            hr = audioClient.Start();
            if (hr < 0) return;

            WaitHandle[]? waitHandles = (usingEventCallback && audioEvent != null)
                ? new WaitHandle[] { audioEvent, sessionToken.WaitHandle }
                : null;
            uint minWriteFrames = usingEventCallback ? 1u : 64u;

            while (!sessionToken.IsCancellationRequested)
            {
                if (waitHandles != null)
                {
                    WaitHandle.WaitAny(waitHandles, 25);
                }
                else
                {
                    sessionToken.WaitHandle.WaitOne(4);
                }

                if (sessionToken.IsCancellationRequested)
                {
                    break;
                }

                int padHr = audioClient.GetCurrentPadding(out uint numFramesPadding);
                if (padHr == AUDCLNT_E_DEVICE_INVALIDATED)
                {
                    break;
                }
                if (padHr < 0)
                {
                    continue;
                }

                uint numFramesAvailable = bufferFrameCount > numFramesPadding
                    ? bufferFrameCount - numFramesPadding
                    : 0;
                if (numFramesAvailable < minWriteFrames)
                {
                    continue;
                }

                int bufHr = renderClient.GetBuffer(numFramesAvailable, out IntPtr dataPtr);
                if (bufHr == AUDCLNT_E_DEVICE_INVALIDATED)
                {
                    break;
                }
                if (bufHr == AUDCLNT_E_BUFFER_TOO_LARGE || bufHr < 0 || dataPtr == IntPtr.Zero)
                {
                    continue;
                }

                if (usingFloat32)
                {
                    var dstFloat = new Span<float>((void*)dataPtr, (int)numFramesAvailable * 2);
                    _jitterBuffer.PullInterleavedFloat32(dstFloat, SpeakerVolume, _speakerMuted);
                }
                else
                {
                    int byteCount = (int)numFramesAvailable * HvProtocolConstants.AudioBlockAlignStereo16;
                    var dstBytes = new Span<byte>((void*)dataPtr, byteCount);
                    _jitterBuffer.PullInterleavedStereo16(dstBytes, SpeakerVolume, _speakerMuted);
                }

                int relHr = renderClient.ReleaseBuffer(numFramesAvailable, AudioClientBufferFlags.None);
                if (relHr == AUDCLNT_E_DEVICE_INVALIDATED)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HostAudioEngine] WasapiRenderLoop exception: {ex.Message}");
        }
        finally
        {
            if (audioClient != null)
            {
                try { audioClient.Stop(); } catch { }
                try { audioClient.Reset(); } catch { }
            }
            SafeReleaseComObject(ref renderClient);
            SafeReleaseComObject(ref audioClient);
            audioEvent?.Dispose();
            SafeReleaseComObject(ref device);
            SafeReleaseComObject(ref enumerator);
        }
    }

    private static IAudioClient? ActivateAudioClient(IMMDevice device)
    {
        Guid iidAudioClient = WasapiGuids.IID_IAudioClient;
        int hr = device.Activate(ref iidAudioClient, CLSCTX_ALL, IntPtr.Zero, out object audioClientObj);
        if (hr < 0 || audioClientObj == null)
        {
            return null;
        }
        return (IAudioClient)audioClientObj;
    }

    private static int GetDeviceMixSampleRate(IMMDevice device)
    {
        IAudioClient? probe = ActivateAudioClient(device);
        if (probe == null) return HvProtocolConstants.AudioSampleRate;

        try
        {
            if (probe.GetMixFormat(out IntPtr mixPtr) >= 0 && mixPtr != IntPtr.Zero)
            {
                try
                {
                    WAVEFORMATEX mixFmt = Marshal.PtrToStructure<WAVEFORMATEX>(mixPtr);
                    if (mixFmt.nSamplesPerSec >= 8000 && mixFmt.nSamplesPerSec <= 192000)
                    {
                        return (int)mixFmt.nSamplesPerSec;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(mixPtr);
                }
            }
        }
        finally
        {
            SafeReleaseComObject(ref probe);
        }

        return HvProtocolConstants.AudioSampleRate;
    }

    private static bool IsSharedFormatSupported(IAudioClient client, IntPtr fmtPtr)
    {
        IntPtr closestMatch = IntPtr.Zero;
        int hr = client.IsFormatSupported(AudioClientShareMode.Shared, fmtPtr, out closestMatch);
        if (closestMatch != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(closestMatch);
        }
        return hr == 0;
    }

    private static bool TryInitRenderCandidate(
        IMMDevice device,
        IntPtr fmtPtr,
        bool useAutoConvert,
        bool useEvent,
        ref IAudioClient? client,
        out AutoResetEvent? wakeEvent)
    {
        wakeEvent = null;
        if (client == null)
        {
            client = ActivateAudioClient(device);
            if (client == null) return false;
        }

        long defaultPeriodHns = 10 * ReftimesPerMillisecond;
        if (client.GetDevicePeriod(out long devDefaultPeriod, out _) >= 0 && devDefaultPeriod > 0)
        {
            defaultPeriodHns = devDefaultPeriod;
        }

        long bufferDurationHns = Math.Max(40 * ReftimesPerMillisecond, defaultPeriodHns * 3);

        var flags = AudioClientStreamFlags.None;
        if (useEvent)
        {
            flags |= AudioClientStreamFlags.EventCallback;
        }
        if (useAutoConvert)
        {
            flags |= AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
        }

        int hr = client.Initialize(
            AudioClientShareMode.Shared,
            flags,
            bufferDurationHns,
            0,
            fmtPtr,
            IntPtr.Zero
        );

        if (hr >= 0 && useEvent)
        {
            var evt = new AutoResetEvent(false);
            hr = client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle());
            if (hr >= 0)
            {
                wakeEvent = evt;
                return true;
            }
            evt.Dispose();
        }
        else if (hr >= 0)
        {
            return true;
        }

        SafeReleaseComObject(ref client);
        return false;
    }

    private static unsafe bool TryCreateInitializedRenderClient(
        IMMDevice device,
        int targetSampleRate,
        out IAudioClient? audioClient,
        out bool usingFloat32,
        out bool usingEventCallback,
        out AutoResetEvent? audioEvent)
    {
        audioClient = null;
        usingFloat32 = false;
        usingEventCallback = false;
        audioEvent = null;

        WAVEFORMATEXTENSIBLE floatFmt = WAVEFORMATEXTENSIBLE.CreateIeeeFloatStereo((uint)targetSampleRate);
        WAVEFORMATEX pcm16Fmt = WAVEFORMATEX.CreatePcm(
            (uint)targetSampleRate,
            HvProtocolConstants.AudioChannelsStereo,
            HvProtocolConstants.AudioBitsPerSample16
        );

        IntPtr pFloat = (IntPtr)(&floatFmt);
        IntPtr pPcm16 = (IntPtr)(&pcm16Fmt);

        IAudioClient? candidate = ActivateAudioClient(device);
        if (candidate == null) return false;

        bool floatNative = IsSharedFormatSupported(candidate, pFloat);
        bool pcm16Native = !floatNative && IsSharedFormatSupported(candidate, pPcm16);

        if (floatNative)
        {
            if (TryInitRenderCandidate(device, pFloat, useAutoConvert: false, useEvent: true, ref candidate, out audioEvent))
            {
                audioClient = candidate;
                usingFloat32 = true;
                usingEventCallback = true;
                return true;
            }
            if (TryInitRenderCandidate(device, pFloat, useAutoConvert: false, useEvent: false, ref candidate, out audioEvent))
            {
                audioClient = candidate;
                usingFloat32 = true;
                usingEventCallback = false;
                return true;
            }
        }

        if (pcm16Native)
        {
            if (TryInitRenderCandidate(device, pPcm16, useAutoConvert: false, useEvent: true, ref candidate, out audioEvent))
            {
                audioClient = candidate;
                usingFloat32 = false;
                usingEventCallback = true;
                return true;
            }
            if (TryInitRenderCandidate(device, pPcm16, useAutoConvert: false, useEvent: false, ref candidate, out audioEvent))
            {
                audioClient = candidate;
                usingFloat32 = false;
                usingEventCallback = false;
                return true;
            }
        }

        if (TryInitRenderCandidate(device, pFloat, useAutoConvert: true, useEvent: true, ref candidate, out audioEvent))
        {
            audioClient = candidate;
            usingFloat32 = true;
            usingEventCallback = true;
            return true;
        }
        if (TryInitRenderCandidate(device, pFloat, useAutoConvert: true, useEvent: false, ref candidate, out audioEvent))
        {
            audioClient = candidate;
            usingFloat32 = true;
            usingEventCallback = false;
            return true;
        }

        if (TryInitRenderCandidate(device, pPcm16, useAutoConvert: true, useEvent: true, ref candidate, out audioEvent))
        {
            audioClient = candidate;
            usingFloat32 = false;
            usingEventCallback = true;
            return true;
        }
        if (TryInitRenderCandidate(device, pPcm16, useAutoConvert: true, useEvent: false, ref candidate, out audioEvent))
        {
            audioClient = candidate;
            usingFloat32 = false;
            usingEventCallback = false;
            return true;
        }

        SafeReleaseComObject(ref candidate);
        return false;
    }

    // ========================================================================
    // WASAPI Microphone Capture Thread (eCapture, eCommunications)
    // Strictly opened ONLY when IsMicUserEnabled == true and gated on KSSTATE_RUN
    // ========================================================================
    private void WasapiCaptureLoop(CancellationToken token)
    {
        IntPtr avrtHandle = IntPtr.Zero;
        try
        {
            uint taskIndex = 0;
            try
            {
                avrtHandle = AvrtNative.AvSetMmThreadCharacteristicsW("Pro Audio", out taskIndex);
            }
            catch { }

            while (!token.IsCancellationRequested && _isMicUserEnabled && _audioTransport.IsConnected)
            {
                RunCaptureSession(token);
                if (!token.IsCancellationRequested && _isMicUserEnabled && _audioTransport.IsConnected)
                {
                    token.WaitHandle.WaitOne(500);
                }
            }
        }
        finally
        {
            if (avrtHandle != IntPtr.Zero)
            {
                try { AvrtNative.AvRevertMmThreadCharacteristics(avrtHandle); } catch { }
            }
        }
    }

    private unsafe void RunCaptureSession(CancellationToken token)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        AutoResetEvent? captureEvent = null;
        IntPtr mixFmtPtr = IntPtr.Zero;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out device);
            if (hr < 0 || device == null)
            {
                hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out device);
                if (hr < 0 || device == null) return;
            }

            audioClient = ActivateAudioClient(device);
            if (audioClient == null) return;

            int srcSampleRate = HvProtocolConstants.AudioSampleRate;
            ushort srcChannels = HvProtocolConstants.AudioChannelsStereo;
            ushort srcBitsPerSample = 16;
            bool srcIsFloat = false;
            int srcBlockAlign = 4;
            bool nativeSupported = false;

            if (audioClient.GetMixFormat(out mixFmtPtr) >= 0 && mixFmtPtr != IntPtr.Zero)
            {
                nativeSupported = TryParseCaptureMixFormat(
                    mixFmtPtr,
                    out srcSampleRate,
                    out srcChannels,
                    out srcBitsPerSample,
                    out srcIsFloat,
                    out srcBlockAlign
                );
            }

            bool initialized = false;
            bool usingEventCallback = false;

            if (nativeSupported && mixFmtPtr != IntPtr.Zero)
            {
                if (TryInitCaptureNative(audioClient, mixFmtPtr, useEvent: true, out captureEvent))
                {
                    initialized = true;
                    usingEventCallback = true;
                }
                else
                {
                    SafeReleaseComObject(ref audioClient);
                    audioClient = ActivateAudioClient(device);
                    if (audioClient != null &&
                        TryInitCaptureNative(audioClient, mixFmtPtr, useEvent: false, out captureEvent))
                    {
                        initialized = true;
                        usingEventCallback = false;
                    }
                }
            }

            if (mixFmtPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(mixFmtPtr);
                mixFmtPtr = IntPtr.Zero;
            }

            if (!initialized)
            {
                SafeReleaseComObject(ref audioClient);
                audioClient = ActivateAudioClient(device);
                if (audioClient == null) return;

                int fallbackRate = TargetCaptureSampleRate;
                WAVEFORMATEX fallbackFmt = WAVEFORMATEX.CreatePcm(
                    (uint)fallbackRate,
                    HvProtocolConstants.AudioChannelsStereo,
                    HvProtocolConstants.AudioBitsPerSample16
                );

                IntPtr pFallback = (IntPtr)(&fallbackFmt);
                var fallbackFlags = AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
                if (audioClient.Initialize(
                        AudioClientShareMode.Shared,
                        fallbackFlags,
                        25 * ReftimesPerMillisecond,
                        0,
                        pFallback,
                        IntPtr.Zero) < 0)
                {
                    return;
                }

                srcSampleRate = fallbackRate;
                srcChannels = HvProtocolConstants.AudioChannelsStereo;
                srcBitsPerSample = HvProtocolConstants.AudioBitsPerSample16;
                srcIsFloat = false;
                srcBlockAlign = HvProtocolConstants.AudioBlockAlignStereo16;
                usingEventCallback = false;
            }

            if (audioClient == null) return;

            Guid iidCaptureClient = WasapiGuids.IID_IAudioCaptureClient;
            hr = audioClient.GetService(ref iidCaptureClient, out object captureClientObj);
            if (hr < 0 || captureClientObj == null) return;
            captureClient = (IAudioCaptureClient)captureClientObj;

            hr = audioClient.Start();
            if (hr < 0) return;

            _isMicHardwareOpen = true;
            StateChanged?.Invoke();

            int activeTargetRate = TargetCaptureSampleRate;
            int activeTargetPeriodBytes = HvProtocolConstants.GetPeriodBytesStereo16ForRate(activeTargetRate);
            byte[] accumBuffer = new byte[HvProtocolConstants.AudioPeriodBytesStereo16];
            int accumOffset = 0;
            double resamplePhase = 0.0;
            float prevL = 0.0f;
            float prevR = 0.0f;

            var frameHdr = new HvAudioFrameHeader
            {
                SampleRate = (uint)activeTargetRate,
                Channels = HvProtocolConstants.AudioChannelsStereo,
                BitsPerSample = HvProtocolConstants.AudioBitsPerSample16,
                FrameSamples = (ushort)HvProtocolConstants.GetPeriodFramesForRate(activeTargetRate),
                Flags = (ushort)HvAudioFlags.MicActive
            };
            byte[] frameHdrBytes = new byte[HvAudioFrameHeader.Size];
            Unsafe.WriteUnaligned(ref frameHdrBytes[0], frameHdr);

            WaitHandle[]? waitHandles = (usingEventCallback && captureEvent != null)
                ? new WaitHandle[] { captureEvent, token.WaitHandle }
                : null;

            bool deviceInvalidated = false;
            while (!token.IsCancellationRequested && _isMicUserEnabled && _audioTransport.IsConnected && !deviceInvalidated)
            {
                int currentTargetRate = TargetCaptureSampleRate;
                if (currentTargetRate != activeTargetRate)
                {
                    activeTargetRate = currentTargetRate;
                    activeTargetPeriodBytes = HvProtocolConstants.GetPeriodBytesStereo16ForRate(activeTargetRate);
                    accumOffset = 0;
                    resamplePhase = 0.0;
                    prevL = 0.0f;
                    prevR = 0.0f;
                    frameHdr.SampleRate = (uint)activeTargetRate;
                    frameHdr.FrameSamples = (ushort)HvProtocolConstants.GetPeriodFramesForRate(activeTargetRate);
                    Unsafe.WriteUnaligned(ref frameHdrBytes[0], frameHdr);
                }

                while (true)
                {
                    int pktHr = captureClient.GetNextPacketSize(out uint packetFrames);
                    if (pktHr == AUDCLNT_E_DEVICE_INVALIDATED)
                    {
                        deviceInvalidated = true;
                        break;
                    }
                    if (pktHr < 0 || packetFrames == 0)
                    {
                        break;
                    }

                    int bufHr = captureClient.GetBuffer(
                        out IntPtr dataPtr,
                        out uint numFramesToRead,
                        out AudioClientBufferFlags bufFlags,
                        out _,
                        out _);

                    if (bufHr == AUDCLNT_E_DEVICE_INVALIDATED)
                    {
                        deviceInvalidated = true;
                        break;
                    }
                    if (bufHr < 0)
                    {
                        break;
                    }

                    bool isSilent = (bufFlags & AudioClientBufferFlags.Silent) != 0 || dataPtr == IntPtr.Zero;
                    if (numFramesToRead > 0)
                    {
                        EmitResampledCapturePacket(
                            dataPtr,
                            (int)numFramesToRead,
                            isSilent,
                            srcSampleRate,
                            srcChannels,
                            srcBitsPerSample,
                            srcIsFloat,
                            srcBlockAlign,
                            activeTargetRate,
                            activeTargetPeriodBytes,
                            accumBuffer,
                            ref accumOffset,
                            ref resamplePhase,
                            ref prevL,
                            ref prevR,
                            frameHdrBytes
                        );
                    }

                    int relHr = captureClient.ReleaseBuffer(numFramesToRead);
                    if (relHr == AUDCLNT_E_DEVICE_INVALIDATED)
                    {
                        deviceInvalidated = true;
                        break;
                    }
                }

                if (deviceInvalidated || token.IsCancellationRequested)
                {
                    break;
                }

                if (waitHandles != null)
                {
                    WaitHandle.WaitAny(waitHandles, 25);
                }
                else
                {
                    token.WaitHandle.WaitOne(5);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HostAudioEngine] WasapiCaptureLoop exception: {ex.Message}");
        }
        finally
        {
            if (mixFmtPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(mixFmtPtr);
            }

            if (audioClient != null)
            {
                try { audioClient.Stop(); } catch { }
                try { audioClient.Reset(); } catch { }
            }

            // Deterministically release every WASAPI COM interface so Windows 11
            // immediately extinguishes the microphone privacy icon in the system tray!
            SafeReleaseComObject(ref captureClient);
            SafeReleaseComObject(ref audioClient);
            captureEvent?.Dispose();
            SafeReleaseComObject(ref device);
            SafeReleaseComObject(ref enumerator);

            _isMicHardwareOpen = false;
            StateChanged?.Invoke();
        }
    }

    private static bool TryParseCaptureMixFormat(
        IntPtr fmtPtr,
        out int sampleRate,
        out ushort channels,
        out ushort bitsPerSample,
        out bool isFloat,
        out int blockAlign)
    {
        WAVEFORMATEX baseFmt = Marshal.PtrToStructure<WAVEFORMATEX>(fmtPtr);
        sampleRate = (int)baseFmt.nSamplesPerSec;
        channels = baseFmt.nChannels;
        bitsPerSample = baseFmt.wBitsPerSample;
        blockAlign = baseFmt.nBlockAlign;
        isFloat = false;

        if (sampleRate < 8000 || sampleRate > 192000 || channels == 0 ||
            blockAlign < channels * Math.Max(1, bitsPerSample / 8))
        {
            return false;
        }

        if (baseFmt.wFormatTag == WAVEFORMATEX.WAVE_FORMAT_IEEE_FLOAT)
        {
            isFloat = true;
            return bitsPerSample == 32;
        }

        if (baseFmt.wFormatTag == WAVEFORMATEX.WAVE_FORMAT_PCM)
        {
            isFloat = false;
            return bitsPerSample is 16 or 24 or 32;
        }

        if (baseFmt.wFormatTag == WAVEFORMATEX.WAVE_FORMAT_EXTENSIBLE && baseFmt.cbSize >= 22)
        {
            WAVEFORMATEXTENSIBLE extFmt = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(fmtPtr);
            if (extFmt.SubFormat == WasapiGuids.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
            {
                isFloat = true;
                return bitsPerSample == 32;
            }
            if (extFmt.SubFormat == WasapiGuids.KSDATAFORMAT_SUBTYPE_PCM)
            {
                isFloat = false;
                return bitsPerSample is 16 or 24 or 32;
            }
        }

        return false;
    }

    private static bool TryInitCaptureNative(
        IAudioClient audioClient,
        IntPtr mixFmtPtr,
        bool useEvent,
        out AutoResetEvent? captureEvent)
    {
        captureEvent = null;
        var flags = useEvent ? AudioClientStreamFlags.EventCallback : AudioClientStreamFlags.None;

        int hr = audioClient.Initialize(
            AudioClientShareMode.Shared,
            flags,
            25 * ReftimesPerMillisecond,
            0,
            mixFmtPtr,
            IntPtr.Zero
        );
        if (hr < 0)
        {
            return false;
        }

        if (useEvent)
        {
            var evt = new AutoResetEvent(false);
            if (audioClient.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()) < 0)
            {
                evt.Dispose();
                return false;
            }
            captureEvent = evt;
        }

        return true;
    }

    private void EmitResampledCapturePacket(
        IntPtr dataPtr,
        int frameCount,
        bool isSilent,
        int srcRate,
        ushort srcChannels,
        ushort srcBitsPerSample,
        bool srcIsFloat,
        int srcBlockAlign,
        int targetRate,
        int targetPeriodBytes,
        byte[] accumBuffer,
        ref int accumOffset,
        ref double resamplePhase,
        ref float prevL,
        ref float prevR,
        ReadOnlySpan<byte> frameHdrBytes)
    {
        if (frameCount <= 0) return;

        // Bit-exact zero-resample path when mix format is already matching stereo 16-bit PCM
        if (srcRate == targetRate &&
            srcChannels == HvProtocolConstants.AudioChannelsStereo &&
            !srcIsFloat &&
            srcBitsPerSample == HvProtocolConstants.AudioBitsPerSample16)
        {
            int bytesAvailable = frameCount * HvProtocolConstants.AudioBlockAlignStereo16;
            int srcOffset = 0;

            while (srcOffset < bytesAvailable)
            {
                int copyLen = Math.Min(bytesAvailable - srcOffset, targetPeriodBytes - accumOffset);
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

                if (accumOffset == targetPeriodBytes)
                {
                    _audioTransport.SendHeaderAndBytes(
                        HvPacketType.AudioCapturePcm,
                        frameHdrBytes,
                        accumBuffer.AsSpan(0, targetPeriodBytes)
                    );
                    accumOffset = 0;
                }
            }

            ReadCaptureStereoFrame(dataPtr, frameCount - 1, isSilent, srcChannels, srcBitsPerSample, srcIsFloat, srcBlockAlign, out prevL, out prevR);
            resamplePhase = 0.0;
            return;
        }

        if (srcRate == targetRate)
        {
            for (int i = 0; i < frameCount; i++)
            {
                ReadCaptureStereoFrame(dataPtr, i, isSilent, srcChannels, srcBitsPerSample, srcIsFloat, srcBlockAlign, out float l, out float r);
                AppendStereo16AndFlushIfFull(l, r, targetPeriodBytes, accumBuffer, ref accumOffset, frameHdrBytes);
                prevL = l;
                prevR = r;
            }
            resamplePhase = 0.0;
            return;
        }

        double step = (double)srcRate / Math.Max(1, targetRate);
        double limit = frameCount - 1;

        while (resamplePhase < limit)
        {
            int i0;
            float frac;
            if (resamplePhase < 0.0)
            {
                i0 = -1;
                frac = (float)(resamplePhase + 1.0);
            }
            else
            {
                i0 = (int)resamplePhase;
                frac = (float)(resamplePhase - i0);
            }

            float l0, r0;
            if (i0 < 0)
            {
                l0 = prevL;
                r0 = prevR;
            }
            else
            {
                ReadCaptureStereoFrame(dataPtr, i0, isSilent, srcChannels, srcBitsPerSample, srcIsFloat, srcBlockAlign, out l0, out r0);
            }

            ReadCaptureStereoFrame(dataPtr, i0 + 1, isSilent, srcChannels, srcBitsPerSample, srcIsFloat, srcBlockAlign, out float l1, out float r1);

            float outL = l0 + (l1 - l0) * frac;
            float outR = r0 + (r1 - r0) * frac;
            AppendStereo16AndFlushIfFull(outL, outR, targetPeriodBytes, accumBuffer, ref accumOffset, frameHdrBytes);
            resamplePhase += step;
        }

        ReadCaptureStereoFrame(dataPtr, frameCount - 1, isSilent, srcChannels, srcBitsPerSample, srcIsFloat, srcBlockAlign, out prevL, out prevR);
        resamplePhase -= frameCount;
    }

    private void AppendStereo16AndFlushIfFull(
        float left,
        float right,
        int targetPeriodBytes,
        byte[] accumBuffer,
        ref int accumOffset,
        ReadOnlySpan<byte> frameHdrBytes)
    {
        short sL = (short)Math.Clamp((int)MathF.Round(Math.Clamp(left, -1.0f, 1.0f) * 32767.0f), short.MinValue, short.MaxValue);
        short sR = (short)Math.Clamp((int)MathF.Round(Math.Clamp(right, -1.0f, 1.0f) * 32767.0f), short.MinValue, short.MaxValue);

        accumBuffer[accumOffset] = (byte)(sL & 0xFF);
        accumBuffer[accumOffset + 1] = (byte)((sL >> 8) & 0xFF);
        accumBuffer[accumOffset + 2] = (byte)(sR & 0xFF);
        accumBuffer[accumOffset + 3] = (byte)((sR >> 8) & 0xFF);
        accumOffset += 4;

        if (accumOffset >= targetPeriodBytes)
        {
            _audioTransport.SendHeaderAndBytes(
                HvPacketType.AudioCapturePcm,
                frameHdrBytes,
                accumBuffer.AsSpan(0, targetPeriodBytes)
            );
            accumOffset = 0;
        }
    }

    private static unsafe void ReadCaptureStereoFrame(
        IntPtr dataPtr,
        int frameIndex,
        bool isSilent,
        ushort channels,
        ushort bitsPerSample,
        bool isFloat,
        int blockAlign,
        out float left,
        out float right)
    {
        if (isSilent || dataPtr == IntPtr.Zero || frameIndex < 0)
        {
            left = 0.0f;
            right = 0.0f;
            return;
        }

        byte* framePtr = (byte*)dataPtr + (frameIndex * blockAlign);
        left = ReadCaptureChannelSample(framePtr, 0, bitsPerSample, isFloat);
        right = channels > 1
            ? ReadCaptureChannelSample(framePtr, 1, bitsPerSample, isFloat)
            : left;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float ReadCaptureChannelSample(
        byte* framePtr,
        int channelIndex,
        ushort bitsPerSample,
        bool isFloat)
    {
        int bytesPerSample = bitsPerSample / 8;
        byte* samplePtr = framePtr + (channelIndex * bytesPerSample);

        if (isFloat && bitsPerSample == 32)
        {
            float val = *(float*)samplePtr;
            return float.IsFinite(val) ? Math.Clamp(val, -1.0f, 1.0f) : 0.0f;
        }
        if (!isFloat && bitsPerSample == 16)
        {
            return *(short*)samplePtr * (1.0f / 32768.0f);
        }
        if (!isFloat && bitsPerSample == 24)
        {
            int raw = samplePtr[0] | (samplePtr[1] << 8) | (samplePtr[2] << 16);
            if ((raw & 0x00800000) != 0)
            {
                raw |= unchecked((int)0xFF000000);
            }
            return raw * (1.0f / 8388608.0f);
        }
        if (!isFloat && bitsPerSample == 32)
        {
            return *(int*)samplePtr * (1.0f / 2147483648.0f);
        }
        return 0.0f;
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
