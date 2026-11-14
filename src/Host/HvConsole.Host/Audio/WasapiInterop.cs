// ============================================================================
// WasapiInterop.cs - Native Windows Core Audio (WASAPI) COM Interop
// Provides direct access to IMMDeviceEnumerator, IAudioClient,
// IAudioRenderClient, and IAudioCaptureClient with deterministic COM release
// so the Host Microphone handle is 100% closed when muted/disarmed.
// ============================================================================
using System;
using System.Runtime.InteropServices;

namespace HvConsole.Host.Audio;

internal enum EDataFlow
{
    eRender  = 0,
    eCapture = 1,
    eAll     = 2
}

internal enum ERole
{
    eConsole        = 0,
    eMultimedia     = 1,
    eCommunications = 2
}

internal enum AudioClientShareMode
{
    Shared    = 0,
    Exclusive = 1
}

[Flags]
internal enum AudioClientStreamFlags : uint
{
    None               = 0x00000000,
    CrossProcess       = 0x00010000,
    Loopback           = 0x00020000,
    EventCallback      = 0x00040000,
    NoPersist          = 0x00080000,
    RateAdjust         = 0x00100000,
    AutoConvertPcm     = 0x80000000,
    SrcDefaultQuality  = 0x08000000
}

[Flags]
internal enum AudioClientBufferFlags : uint
{
    None             = 0,
    DataDiscontinuity = 0x1,
    Silent           = 0x2,
    TimestampError   = 0x4
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;

    public const ushort WAVE_FORMAT_PCM = 1;

    public static WAVEFORMATEX CreatePcm(uint sampleRate, ushort channels, ushort bitsPerSample)
    {
        ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));
        return new WAVEFORMATEX
        {
            wFormatTag = WAVE_FORMAT_PCM,
            nChannels = channels,
            nSamplesPerSec = sampleRate,
            nAvgBytesPerSec = sampleRate * blockAlign,
            nBlockAlign = blockAlign,
            wBitsPerSample = bitsPerSample,
            cbSize = 0
        };
    }
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(
        ref Guid iid,
        uint dwClsCtx,
        IntPtr pActivationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface
    );
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(
        AudioClientShareMode ShareMode,
        AudioClientStreamFlags StreamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        ref WAVEFORMATEX pFormat,
        IntPtr AudioSessionGuid
    );

    [PreserveSig]
    int GetBufferSize(out uint pNumBufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long phnsLatency);

    [PreserveSig]
    int GetCurrentPadding(out uint pNumPaddingFrames);

    [PreserveSig]
    int IsFormatSupported(AudioClientShareMode ShareMode, ref WAVEFORMATEX pFormat, out IntPtr ppClosestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr ppDeviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig]
    int GetBuffer(uint NumFramesRequested, out IntPtr ppData);

    [PreserveSig]
    int ReleaseBuffer(uint NumFramesWritten, AudioClientBufferFlags dwFlags);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(
        out IntPtr ppData,
        out uint pNumFramesToRead,
        out AudioClientBufferFlags pdwFlags,
        out ulong pu64DevicePosition,
        out ulong pu64QPCPosition
    );

    [PreserveSig]
    int ReleaseBuffer(uint NumFramesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint pNumFramesInNextPacket);
}

internal static class WasapiGuids
{
    public static Guid IID_IAudioClient        = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static Guid IID_IAudioRenderClient  = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
}
