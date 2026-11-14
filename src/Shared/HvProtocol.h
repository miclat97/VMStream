// ============================================================================
// HvProtocol.h - Shared Wire Protocol, Service GUIDs, and IOCTL Definitions
// Project: VMStream - Zero-Artifact Hyper-V Console Client & Guest Bridge
// ============================================================================
#pragma once

#if defined(_KERNEL_MODE)
    #include <ntddk.h>
    #include <initguid.h>
    typedef UINT8   uint8_t;
    typedef UINT16  uint16_t;
    typedef UINT32  uint32_t;
    typedef UINT64  uint64_t;
    typedef INT16   int16_t;
    typedef INT32   int32_t;
#else
    #include <stdint.h>
    #if defined(_WIN32)
        #ifndef WIN32_LEAN_AND_MEAN
            #define WIN32_LEAN_AND_MEAN
        #endif
        #include <windows.h>
        #include <winioctl.h>
        #include <initguid.h>
    #endif
#endif

// ============================================================================
// 1. Hyper-V Socket (AF_HYPERV) Service & Endpoint GUIDs
// ============================================================================

// Audio Stream Service GUID: {8B4E9A12-5C3D-4F18-9A2E-7D6C4B8E1001}
// Registered under HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices
DEFINE_GUID(HV_STREAM_AUDIO_SERVICE_GUID,
    0x8b4e9a12, 0x5c3d, 0x4f18, 0x9a, 0x2e, 0x7d, 0x6c, 0x4b, 0x8e, 0x10, 0x01);

// Multi-Touch HID Service GUID: {8B4E9A12-5C3D-4F18-9A2E-7D6C4B8E1002}
// Registered under HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices
DEFINE_GUID(HV_STREAM_TOUCH_SERVICE_GUID,
    0x8b4e9a12, 0x5c3d, 0x4f18, 0x9a, 0x2e, 0x7d, 0x6c, 0x4b, 0x8e, 0x10, 0x02);

// Standard Hyper-V Parent Partition GUID: {A42E7CDA-D03F-480C-9CC2-A4DE20ABB878}
DEFINE_GUID(HV_STREAM_GUID_PARENT,
    0xa42e7cda, 0xd03f, 0x480c, 0x9c, 0xc2, 0xa4, 0xde, 0x20, 0xab, 0xb8, 0x78);

// Standard Hyper-V Loopback / Zero GUIDs
DEFINE_GUID(HV_STREAM_GUID_ZERO,
    0x00000000, 0x0000, 0x0000, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);

// Optional TCP Fallback Ports (only used if AF_HYPERV creation is explicitly bypassed)
#define HV_STREAM_TCP_PORT_AUDIO            42179
#define HV_STREAM_TCP_PORT_TOUCH            42180

// ============================================================================
// 2. Guest Driver Device Interface GUIDs, Device Names, and IOCTLs
// ============================================================================

// HvTouchHid Device Interface GUID: {4D8F2B91-3E7A-4C56-8F19-2A6D5E9C3001}
DEFINE_GUID(GUID_DEVINTERFACE_HVTOUCHHID,
    0x4d8f2b91, 0x3e7a, 0x4c56, 0x8f, 0x19, 0x2a, 0x6d, 0x5e, 0x9c, 0x30, 0x01);

// HvVirtualAudio Device Interface GUID: {4D8F2B91-3E7A-4C56-8F19-2A6D5E9C3002}
DEFINE_GUID(GUID_DEVINTERFACE_HVAUDIO,
    0x4d8f2b91, 0x3e7a, 0x4c56, 0x8f, 0x19, 0x2a, 0x6d, 0x5e, 0x9c, 0x30, 0x02);

#define HVTOUCH_NT_DEVICE_NAME              L"\\Device\\HvTouchHidControl"
#define HVTOUCH_DOS_DEVICE_NAME             L"\\DosDevices\\HvTouchHid"
#define HVTOUCH_WIN32_DEVICE_PATH           L"\\\\.\\HvTouchHid"

#define HVAUDIO_NT_DEVICE_NAME              L"\\Device\\HvVirtualAudioControl"
#define HVAUDIO_DOS_DEVICE_NAME             L"\\DosDevices\\HvVirtualAudio"
#define HVAUDIO_WIN32_DEVICE_PATH           L"\\\\.\\HvVirtualAudio"

#ifndef CTL_CODE
    #define FILE_DEVICE_UNKNOWN             0x00000022
    #define METHOD_BUFFERED                 0
    #define METHOD_IN_DIRECT                1
    #define METHOD_OUT_DIRECT               2
    #define METHOD_NEITHER                  3
    #define FILE_ANY_ACCESS                 0
    #define FILE_READ_DATA                  0x0001
    #define FILE_WRITE_DATA                 0x0002
    #define CTL_CODE(DeviceType, Function, Method, Access) \
        (((DeviceType) << 16) | ((Access) << 14) | ((Function) << 2) | (Method))
#endif

#define FILE_DEVICE_HVTOUCH                 0x00008000
#define FILE_DEVICE_HVAUDIO                 0x00008001

// Submits a 64-byte HV_TOUCH_INPUT_REPORT directly to VHF via VhfReadReportSubmit
#define IOCTL_HVTOUCH_SUBMIT_FRAME \
    CTL_CODE(FILE_DEVICE_HVTOUCH, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)

// Releases all active touch contacts immediately (anti-stuck-finger safeguard)
#define IOCTL_HVTOUCH_RESET_CONTACTS \
    CTL_CODE(FILE_DEVICE_HVTOUCH, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS)

// Reads rendered speaker PCM frames from the Guest WaveRT Render ring buffer
#define IOCTL_HVAUDIO_READ_RENDER_PCM \
    CTL_CODE(FILE_DEVICE_HVAUDIO, 0x810, METHOD_BUFFERED, FILE_ANY_ACCESS)

// Writes captured microphone PCM frames into the Guest WaveRT Capture ring buffer
#define IOCTL_HVAUDIO_WRITE_CAPTURE_PCM \
    CTL_CODE(FILE_DEVICE_HVAUDIO, 0x811, METHOD_BUFFERED, FILE_ANY_ACCESS)

// Queries current Guest audio pin states (Render KSSTATE_RUN / Capture KSSTATE_RUN)
#define IOCTL_HVAUDIO_GET_STATE \
    CTL_CODE(FILE_DEVICE_HVAUDIO, 0x812, METHOD_BUFFERED, FILE_ANY_ACCESS)

// Inverted-call IOCTL: pends in HvVirtualAudio.sys until Capture or Render KSSTATE changes
#define IOCTL_HVAUDIO_WAIT_STATE_CHANGE \
    CTL_CODE(FILE_DEVICE_HVAUDIO, 0x813, METHOD_BUFFERED, FILE_ANY_ACCESS)

// ============================================================================
// 3. Audio Constants (48 kHz, 16-bit/24-bit PCM, 10ms Period)
// ============================================================================

#define HV_AUDIO_SAMPLE_RATE                48000
#define HV_AUDIO_CHANNELS_STEREO            2
#define HV_AUDIO_CHANNELS_MONO              1
#define HV_AUDIO_BITS_PER_SAMPLE_16         16
#define HV_AUDIO_BITS_PER_SAMPLE_24         24
#define HV_AUDIO_BYTES_PER_SAMPLE_16        2
#define HV_AUDIO_BLOCK_ALIGN_STEREO_16      4
#define HV_AUDIO_PERIOD_MS                  10
#define HV_AUDIO_FRAMES_PER_PERIOD          480     // 48000 Hz * 10 ms / 1000 = 480 frames
#define HV_AUDIO_PERIOD_BYTES_STEREO_16     1920    // 480 frames * 2 channels * 2 bytes = 1920 bytes
#define HV_AUDIO_RING_BUFFER_SIZE           65536   // 64 KB circular buffer in kernel non-paged pool

// ============================================================================
// 4. Multi-Touch HID Constants
// ============================================================================

#define HV_MAX_TOUCH_CONTACTS               10
#define HV_TOUCH_COORDINATE_MAX             32767
#define HV_TOUCH_REPORT_ID_INPUT            0x01
#define HV_TOUCH_REPORT_ID_MAX_COUNT        0x02
#define HV_TOUCH_REPORT_ID_THQA_CERT        0x03

// ============================================================================
// 5. Packed Wire Protocol Structures
// ============================================================================

#define HV_PACKET_MAGIC                     0x53564D56u // 'VMVS' (VMStream Wire Magic)
#define HV_PROTOCOL_VERSION                 1u

enum HV_PACKET_TYPE : uint8_t {
    HvPacketType_Ping               = 0x01,
    HvPacketType_Pong               = 0x02,
    HvPacketType_AudioRenderPcm     = 0x10, // Guest -> Host: Speaker PCM audio frame
    HvPacketType_AudioCapturePcm    = 0x11, // Host -> Guest: Microphone PCM audio frame
    HvPacketType_AudioControl       = 0x12, // Bidirectional: KSSTATE_RUN / Mic Gate state
    HvPacketType_TouchFrame         = 0x20, // Host -> Guest: 64-byte HID Multi-Touch Input Report
    HvPacketType_TouchReset         = 0x21  // Host -> Guest: Zero all touch contacts immediately
};

enum HV_AUDIO_FLAGS : uint16_t {
    HvAudioFlag_None                = 0x0000,
    HvAudioFlag_Silence             = 0x0001,
    HvAudioFlag_MicActive           = 0x0002,
    HvAudioFlag_Discontinuity       = 0x0004
};

#pragma pack(push, 1)

// 12-byte common wire header preceding every packet on both Audio and Touch sockets
typedef struct _HV_PACKET_HEADER {
    uint32_t Magic;             // Always HV_PACKET_MAGIC (0x53564D56)
    uint8_t  Version;           // Always HV_PROTOCOL_VERSION (1)
    uint8_t  Type;              // HV_PACKET_TYPE
    uint16_t PayloadLength;     // Payload byte count following this header
    uint32_t Sequence;          // Monotonically increasing packet counter
} HV_PACKET_HEADER, *PHV_PACKET_HEADER;

static_assert(sizeof(HV_PACKET_HEADER) == 12, "HV_PACKET_HEADER must be 12 bytes");

// 12-byte audio frame header followed immediately by raw interleaved PCM bytes
typedef struct _HV_AUDIO_FRAME_HEADER {
    uint32_t SampleRate;        // 48000
    uint16_t Channels;          // 2 (Render/Capture Stereo) or 1 (Capture Mono)
    uint16_t BitsPerSample;     // 16 (or 24)
    uint16_t FrameSamples;      // Number of samples per channel (e.g., 480 for 10ms)
    uint16_t Flags;             // HV_AUDIO_FLAGS bitmask
} HV_AUDIO_FRAME_HEADER, *PHV_AUDIO_FRAME_HEADER;

static_assert(sizeof(HV_AUDIO_FRAME_HEADER) == 12, "HV_AUDIO_FRAME_HEADER must be 12 bytes");

// 8-byte control payload for IOCTL_HVAUDIO_GET_STATE / IOCTL_HVAUDIO_WAIT_STATE_CHANGE
// and HvPacketType_AudioControl wire messages
typedef struct _HV_AUDIO_CONTROL_PAYLOAD {
    uint8_t  GuestCaptureActive;    // 1 when Guest app has Capture Pin in KSSTATE_RUN, 0 otherwise
    uint8_t  GuestRenderActive;     // 1 when Guest app has Render Pin in KSSTATE_RUN, 0 otherwise
    uint8_t  HostMicEnabled;        // 1 when Host user explicitly enabled Mic forwarding
    uint8_t  Reserved;              // Alignment padding (0)
    uint32_t SequenceGeneration;    // Incremented inside HvVirtualAudio.sys on every state transition
} HV_AUDIO_CONTROL_PAYLOAD, *PHV_AUDIO_CONTROL_PAYLOAD;

static_assert(sizeof(HV_AUDIO_CONTROL_PAYLOAD) == 8, "HV_AUDIO_CONTROL_PAYLOAD must be 8 bytes");

// 16-byte Ping/Pong diagnostic payload for latency measurement
typedef struct _HV_PING_PAYLOAD {
    uint64_t HostTimestampQpc;
    uint64_t GuestTimestampQpc;
} HV_PING_PAYLOAD, *PHV_PING_PAYLOAD;

static_assert(sizeof(HV_PING_PAYLOAD) == 16, "HV_PING_PAYLOAD must be 16 bytes");

// Single finger contact entry (6 bytes) matching g_HvTouchReportDescriptor
typedef struct _HV_TOUCH_CONTACT {
    uint8_t  TipSwitch  : 1;    // 1 = finger touching screen, 0 = lifted
    uint8_t  Confidence : 1;    // 1 = valid touch contact (required by Windows 11 DirectManipulation)
    uint8_t  Padding    : 6;    // Always 0
    uint8_t  ContactId;         // Unique finger tracking ID (0..127)
    uint16_t X;                 // Normalized X coordinate (0..32767)
    uint16_t Y;                 // Normalized Y coordinate (0..32767)
} HV_TOUCH_CONTACT, *PHV_TOUCH_CONTACT;

static_assert(sizeof(HV_TOUCH_CONTACT) == 6, "HV_TOUCH_CONTACT must be 6 bytes");

// Complete 64-byte HID Multi-Touch Input Report (Report ID 0x01)
typedef struct _HV_TOUCH_INPUT_REPORT {
    uint8_t          ReportId;                          // Always 0x01 (HV_TOUCH_REPORT_ID_INPUT)
    HV_TOUCH_CONTACT Contacts[HV_MAX_TOUCH_CONTACTS];   // 10 * 6 = 60 bytes
    uint8_t          ContactCount;                      // Number of active contacts in this frame (0..10)
    uint16_t         ScanTime;                          // Timestamp in 100us units (0..65535, wrapping)
} HV_TOUCH_INPUT_REPORT, *PHV_TOUCH_INPUT_REPORT;

static_assert(sizeof(HV_TOUCH_INPUT_REPORT) == 64, "HV_TOUCH_INPUT_REPORT must be exactly 64 bytes");

// Feature Report 0x02: Contact Count Maximum (2 bytes)
typedef struct _HV_TOUCH_MAX_COUNT_FEATURE {
    uint8_t ReportId;   // Always 0x02 (HV_TOUCH_REPORT_ID_MAX_COUNT)
    uint8_t MaxCount;   // Always 10 (HV_MAX_TOUCH_CONTACTS)
} HV_TOUCH_MAX_COUNT_FEATURE, *PHV_TOUCH_MAX_COUNT_FEATURE;

static_assert(sizeof(HV_TOUCH_MAX_COUNT_FEATURE) == 2, "HV_TOUCH_MAX_COUNT_FEATURE must be 2 bytes");

// Feature Report 0x03: Windows 8/10/11 THQA Certification Blob (257 bytes)
typedef struct _HV_TOUCH_CERT_FEATURE {
    uint8_t ReportId;   // Always 0x03 (HV_TOUCH_REPORT_ID_THQA_CERT)
    uint8_t Blob[256];  // THQA signature blob
} HV_TOUCH_CERT_FEATURE, *PHV_TOUCH_CERT_FEATURE;

static_assert(sizeof(HV_TOUCH_CERT_FEATURE) == 257, "HV_TOUCH_CERT_FEATURE must be 257 bytes");

#pragma pack(pop)
