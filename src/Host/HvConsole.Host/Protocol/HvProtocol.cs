// ============================================================================
// HvProtocol.cs - C# Wire Protocol Structures & Hyper-V Socket GUIDs
// Project: VMStream - Host Console Client (HvConsole.Host)
// Exact binary mirror of src/Shared/HvProtocol.h (#pragma pack(push, 1))
// ============================================================================
using System;
using System.Runtime.InteropServices;

namespace HvConsole.Host.Protocol;

public static class HvProtocolConstants
{
    // Audio Stream Service GUID: {8B4E9A12-5C3D-4F18-9A2E-7D6C4B8E1001}
    public static readonly Guid AudioServiceGuid = new("8b4e9a12-5c3d-4f18-9a2e-7d6c4b8e1001");

    // Multi-Touch HID Service GUID: {8B4E9A12-5C3D-4F18-9A2E-7D6C4B8E1002}
    public static readonly Guid TouchServiceGuid = new("8b4e9a12-5c3d-4f18-9a2e-7d6c4b8e1002");

    // Standard Hyper-V Parent Partition GUID: {A42E7CDA-D03F-480C-9CC2-A4DE20ABB878}
    public static readonly Guid HvGuidParent = new("a42e7cda-d03f-480c-9cc2-a4de20abb878");

    public const int TcpFallbackPortAudio = 42179;
    public const int TcpFallbackPortTouch = 42180;

    public const uint PacketMagic = 0x53564D56u; // 'VMVS'
    public const byte ProtocolVersion = 1;

    public const int AudioSampleRate44100 = 44100;
    public const int AudioSampleRate48000 = 48000;
    public const int AudioSampleRate = AudioSampleRate48000;
    public const int AudioChannelsStereo = 2;
    public const int AudioBitsPerSample16 = 16;
    public const int AudioBlockAlignStereo16 = 4;
    public const int AudioPeriodMs = 10;
    public const int AudioFramesPerPeriod44100 = 441;
    public const int AudioPeriodBytesStereo16_44100 = 1764; // 441 * 4 bytes
    public const int AudioFramesPerPeriod = 480;
    public const int AudioPeriodBytesStereo16 = 1920; // 480 * 4 bytes

    public const int MaxTouchContacts = 10;
    public const ushort TouchCoordinateMax = 32767;
    public const byte TouchReportIdInput = 0x01;

    public static int GetPeriodFramesForRate(int sampleRate) =>
        sampleRate == AudioSampleRate44100 ? AudioFramesPerPeriod44100 : AudioFramesPerPeriod;

    public static int GetPeriodBytesStereo16ForRate(int sampleRate) =>
        GetPeriodFramesForRate(sampleRate) * AudioBlockAlignStereo16;

    public static HvAudioRateCode EncodeAudioRateCode(int sampleRate) => sampleRate switch
    {
        AudioSampleRate44100 => HvAudioRateCode.Hz44100,
        AudioSampleRate48000 => HvAudioRateCode.Hz48000,
        _ => HvAudioRateCode.Default
    };

    public static int DecodeAudioRateCode(byte code) =>
        (code & 0x0F) == (byte)HvAudioRateCode.Hz44100
            ? AudioSampleRate44100
            : AudioSampleRate48000;

    public static byte PackAudioRateConfig(int renderOrPreferredRate, int captureRate) =>
        (byte)(((byte)EncodeAudioRateCode(renderOrPreferredRate) & 0x0F) |
               (((byte)EncodeAudioRateCode(captureRate) & 0x0F) << 4));

    public static int DecodeRenderRateFromConfig(byte rateConfig) =>
        DecodeAudioRateCode((byte)(rateConfig & 0x0F));

    public static int DecodeCaptureRateFromConfig(byte rateConfig) =>
        DecodeAudioRateCode((byte)((rateConfig >> 4) & 0x0F));
}

public enum HvAudioRateCode : byte
{
    Default = 0, // Auto / Legacy Default (48000 Hz)
    Hz44100 = 1, // 44100 Hz
    Hz48000 = 2  // 48000 Hz
}

public enum HvPacketType : byte
{
    Ping            = 0x01,
    Pong            = 0x02,
    AudioRenderPcm  = 0x10, // Guest -> Host: Speaker PCM audio frame
    AudioCapturePcm = 0x11, // Host -> Guest: Microphone PCM audio frame
    AudioControl    = 0x12, // Bidirectional: Guest KSSTATE_RUN / Host Mic Gate / Sample Rate
    TouchFrame      = 0x20, // Host -> Guest: 64-byte HID Multi-Touch Input Report
    TouchReset      = 0x21  // Host -> Guest: Release all contacts immediately
}

[Flags]
public enum HvAudioFlags : ushort
{
    None          = 0x0000,
    Silence       = 0x0001,
    MicActive     = 0x0002,
    Discontinuity = 0x0004
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HvPacketHeader
{
    public uint Magic;          // HvProtocolConstants.PacketMagic (0x53564D56)
    public byte Version;        // HvProtocolConstants.ProtocolVersion (1)
    public byte Type;           // HvPacketType
    public ushort PayloadLength;
    public uint Sequence;

    public const int Size = 12;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HvAudioFrameHeader
{
    public uint SampleRate;     // 44100 or 48000
    public ushort Channels;     // 2 (Stereo) or 1 (Mono)
    public ushort BitsPerSample;// 16
    public ushort FrameSamples; // 441 or 480 for 10ms
    public ushort Flags;        // HvAudioFlags

    public const int Size = 12;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HvAudioControlPayload
{
    public byte GuestCaptureActive; // 1 when Guest app has Capture Pin in KSSTATE_RUN
    public byte GuestRenderActive;  // 1 when Guest app has Render Pin in KSSTATE_RUN
    public byte HostMicEnabled;     // 1 when Host user explicitly enabled Mic
    public byte SampleRateConfig;   // Low nibble: Render/HostPref rate code; High nibble: Capture rate code
    public uint SequenceGeneration;

    public const int Size = 8;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HvPingPayload
{
    public ulong HostTimestampQpc;
    public ulong GuestTimestampQpc;

    public const int Size = 16;
}

/// <summary>
/// 6-byte single finger contact matching g_HvTouchReportDescriptor.
/// Bit 0 of Flags = TipSwitch (1 = touching, 0 = lifted).
/// Bit 1 of Flags = Confidence (always 1 for valid touch contact).
/// Bits 2..7 of Flags = Padding (0).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HvTouchContact
{
    public byte Flags;      // Bit 0: TipSwitch, Bit 1: Confidence, Bits 2..7: Padding (0)
    public byte ContactId;  // 0..127
    public ushort X;        // 0..32767
    public ushort Y;        // 0..32767

    public const int Size = 6;

    public bool TipSwitch
    {
        readonly get => (Flags & 0x01) != 0;
        set
        {
            if (value) Flags |= 0x01;
            else Flags = (byte)(Flags & ~0x01);
        }
    }

    public bool Confidence
    {
        readonly get => (Flags & 0x02) != 0;
        set
        {
            if (value) Flags |= 0x02;
            else Flags = (byte)(Flags & ~0x02);
        }
    }

    public static HvTouchContact Create(byte contactId, ushort x, ushort y, bool tipSwitch, bool confidence = true)
    {
        byte flags = 0;
        if (tipSwitch) flags |= 0x01;
        if (confidence) flags |= 0x02;

        return new HvTouchContact
        {
            Flags = flags,
            ContactId = (byte)(contactId & 0x7F),
            X = Math.Min(x, HvProtocolConstants.TouchCoordinateMax),
            Y = Math.Min(y, HvProtocolConstants.TouchCoordinateMax)
        };
    }
}

/// <summary>
/// Complete 64-byte HID Multi-Touch Input Report (Report ID 0x01).
/// Total size: 1 (ReportId) + 10 * 6 (Contacts) + 1 (ContactCount) + 2 (ScanTime) = 64 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct HvTouchInputReport
{
    public byte ReportId; // Always 0x01
    public fixed byte ContactsRaw[HvProtocolConstants.MaxTouchContacts * HvTouchContact.Size]; // 60 bytes
    public byte ContactCount; // 0..10
    public ushort ScanTime;   // 100us units (0..65535)

    public const int Size = 64;

    public void SetContact(int slotIndex, in HvTouchContact contact)
    {
        if ((uint)slotIndex >= HvProtocolConstants.MaxTouchContacts)
            throw new ArgumentOutOfRangeException(nameof(slotIndex));

        fixed (byte* basePtr = ContactsRaw)
        {
            var slotPtr = (HvTouchContact*)(basePtr + (slotIndex * HvTouchContact.Size));
            *slotPtr = contact;
        }
    }

    public readonly HvTouchContact GetContact(int slotIndex)
    {
        if ((uint)slotIndex >= HvProtocolConstants.MaxTouchContacts)
            throw new ArgumentOutOfRangeException(nameof(slotIndex));

        fixed (byte* basePtr = ContactsRaw)
        {
            var slotPtr = (HvTouchContact*)(basePtr + (slotIndex * HvTouchContact.Size));
            return *slotPtr;
        }
    }
}
