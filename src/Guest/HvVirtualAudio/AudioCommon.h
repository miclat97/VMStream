// ============================================================================
// AudioCommon.h - Shared PortCls/WaveRT Definitions & Ring Buffer Structures
// Project: VMStream - Guest Virtual Audio Driver (HvVirtualAudio.sys)
// ============================================================================
#pragma once

#include <ntddk.h>
#include <windef.h>
#define NOBITMAP
#include <mmreg.h>
#undef NOBITMAP
#include <ks.h>
#include <ksmedia.h>
#include <unknown.h>
#include <portcls.h>
#include <stdunk.h>
#include <drmk.h>

#include "../../Shared/HvProtocol.h"

#define HVAUDIO_POOL_TAG 'uAvH' // 'HvAu'
#define MAX_MINIPORTS    4

// Subdevice Endpoint Types
enum HV_AUDIO_DEVICE_TYPE {
    HvAudioDeviceType_Render = 0,   // "Hyper-V Console Speakers"
    HvAudioDeviceType_Capture = 1   // "Hyper-V Console Microphone"
};

// WaveRT Filter Pins
enum HV_WAVERT_PIN_IDS {
    KSPIN_WAVERT_HOST_OR_SINK = 0,
    KSPIN_WAVERT_BRIDGE       = 1
};

// Topology Filter Pins
enum HV_TOPO_PIN_IDS {
    KSPIN_TOPO_WAVEOUT_SOURCE = 0,
    KSPIN_TOPO_LINEOUT_DEST   = 1,
    KSPIN_TOPO_MIC_SOURCE     = 0,
    KSPIN_TOPO_WAVEIN_DEST    = 1
};

// Topology Nodes
enum HV_TOPO_NODE_IDS {
    KSNODE_TOPO_VOLUME = 0,
    KSNODE_TOPO_MUTE   = 1
};

// {7A1F9C80-41B2-4E38-91D4-8E2A5C6B7001}
DEFINE_GUID(IID_IHvAudioAdapterCommon,
    0x7a1f9c80, 0x41b2, 0x4e38, 0x91, 0xd4, 0x8e, 0x2a, 0x5c, 0x6b, 0x70, 0x01);

// ============================================================================
// Lock-Protected Non-Paged Kernel Circular Ring Buffer (64 KB)
// Used between WaveRT DPC DMA engine and IOCTL handlers from HvGuestBridge.exe
// ============================================================================
class CHvAudioRingBuffer {
public:
    void Initialize() {
        KeInitializeSpinLock(&m_Lock);
        m_ReadIndex = 0;
        m_WriteIndex = 0;
        m_AvailableBytes = 0;
        RtlZeroMemory(m_Data, sizeof(m_Data));
    }

    void Reset() {
        KIRQL oldIrql;
        KeAcquireSpinLock(&m_Lock, &oldIrql);
        m_ReadIndex = 0;
        m_WriteIndex = 0;
        m_AvailableBytes = 0;
        RtlZeroMemory(m_Data, sizeof(m_Data));
        KeReleaseSpinLock(&m_Lock, oldIrql);
    }

    // Writes PCM bytes into the circular buffer; overwrites oldest data on overflow
    ULONG Write(_In_reads_bytes_(Length) const UCHAR* Source, _In_ ULONG Length) {
        if (Source == NULL || Length == 0) {
            return 0;
        }

        KIRQL oldIrql;
        KeAcquireSpinLock(&m_Lock, &oldIrql);

        if (Length > HV_AUDIO_RING_BUFFER_SIZE) {
            Source += (Length - HV_AUDIO_RING_BUFFER_SIZE);
            Length = HV_AUDIO_RING_BUFFER_SIZE;
        }

        // If writing exceeds remaining space, advance ReadIndex to drop oldest samples
        ULONG freeSpace = HV_AUDIO_RING_BUFFER_SIZE - m_AvailableBytes;
        if (Length > freeSpace) {
            ULONG dropBytes = (Length - freeSpace);
            m_ReadIndex = (m_ReadIndex + dropBytes) % HV_AUDIO_RING_BUFFER_SIZE;
            m_AvailableBytes -= dropBytes;
        }

        ULONG firstChunk = min(Length, HV_AUDIO_RING_BUFFER_SIZE - m_WriteIndex);
        RtlCopyMemory(&m_Data[m_WriteIndex], Source, firstChunk);
        if (Length > firstChunk) {
            RtlCopyMemory(&m_Data[0], Source + firstChunk, Length - firstChunk);
        }

        m_WriteIndex = (m_WriteIndex + Length) % HV_AUDIO_RING_BUFFER_SIZE;
        m_AvailableBytes += Length;

        KeReleaseSpinLock(&m_Lock, oldIrql);
        return Length;
    }

    // Reads up to Length bytes from the circular buffer
    ULONG Read(_Out_writes_bytes_(Length) UCHAR* Destination, _In_ ULONG Length) {
        if (Destination == NULL || Length == 0) {
            return 0;
        }

        KIRQL oldIrql;
        KeAcquireSpinLock(&m_Lock, &oldIrql);

        ULONG toRead = min(Length, m_AvailableBytes);
        if (toRead > 0) {
            ULONG firstChunk = min(toRead, HV_AUDIO_RING_BUFFER_SIZE - m_ReadIndex);
            RtlCopyMemory(Destination, &m_Data[m_ReadIndex], firstChunk);
            if (toRead > firstChunk) {
                RtlCopyMemory(Destination + firstChunk, &m_Data[0], toRead - firstChunk);
            }

            m_ReadIndex = (m_ReadIndex + toRead) % HV_AUDIO_RING_BUFFER_SIZE;
            m_AvailableBytes -= toRead;
        }

        KeReleaseSpinLock(&m_Lock, oldIrql);
        return toRead;
    }

    ULONG GetAvailableBytes() {
        KIRQL oldIrql;
        KeAcquireSpinLock(&m_Lock, &oldIrql);
        ULONG avail = m_AvailableBytes;
        KeReleaseSpinLock(&m_Lock, oldIrql);
        return avail;
    }

private:
    KSPIN_LOCK m_Lock;
    ULONG      m_ReadIndex;
    ULONG      m_WriteIndex;
    ULONG      m_AvailableBytes;
    UCHAR      m_Data[HV_AUDIO_RING_BUFFER_SIZE];
};

// ============================================================================
// IHvAudioAdapterCommon - Adapter-level state shared across miniports & IOCTLs
// ============================================================================
DECLARE_INTERFACE_(IHvAudioAdapterCommon, IUnknown) {
    STDMETHOD_(NTSTATUS, Init)(THIS_ _In_ PDEVICE_OBJECT DeviceObject) PURE;
    STDMETHOD_(void, Cleanup)(THIS) PURE;

    STDMETHOD_(void, SetRenderState)(THIS_ _In_ BOOLEAN IsRunning) PURE;
    STDMETHOD_(void, SetCaptureState)(THIS_ _In_ BOOLEAN IsRunning) PURE;
    STDMETHOD_(BOOLEAN, IsRenderRunning)(THIS) PURE;
    STDMETHOD_(BOOLEAN, IsCaptureRunning)(THIS) PURE;

    STDMETHOD_(ULONG, PushRenderPcmFromWaveRT)(
        THIS_
        _In_reads_bytes_(Length) const UCHAR* Buffer,
        _In_ ULONG Length
    ) PURE;

    STDMETHOD_(ULONG, PullRenderPcmForBridge)(
        THIS_
        _Out_writes_bytes_(Length) UCHAR* Buffer,
        _In_ ULONG Length
    ) PURE;

    STDMETHOD_(ULONG, PushCapturePcmFromBridge)(
        THIS_
        _In_reads_bytes_(Length) const UCHAR* Buffer,
        _In_ ULONG Length
    ) PURE;

    STDMETHOD_(ULONG, PullCapturePcmForWaveRT)(
        THIS_
        _Out_writes_bytes_(Length) UCHAR* Buffer,
        _In_ ULONG Length
    ) PURE;

    STDMETHOD_(void, GetControlState)(
        THIS_
        _Out_ PHV_AUDIO_CONTROL_PAYLOAD OutState
    ) PURE;

    STDMETHOD_(LONG, GetVolumeLevel)(THIS_ _In_ HV_AUDIO_DEVICE_TYPE Type) PURE;
    STDMETHOD_(void, SetVolumeLevel)(THIS_ _In_ HV_AUDIO_DEVICE_TYPE Type, _In_ LONG LevelDb) PURE;
    STDMETHOD_(BOOL, GetMuteState)(THIS_ _In_ HV_AUDIO_DEVICE_TYPE Type) PURE;
    STDMETHOD_(void, SetMuteState)(THIS_ _In_ HV_AUDIO_DEVICE_TYPE Type, _In_ BOOL Muted) PURE;
};

typedef IHvAudioAdapterCommon *PHVAUDIOADAPTERCOMMON;

NTSTATUS CreateMiniportWaveRTHvAudio(
    _Out_ PUNKNOWN* Unknown,
    _In_  REFCLSID,
    _In_opt_ PUNKNOWN UnknownOuter,
    _In_  POOL_TYPE PoolType,
    _In_  PUNKNOWN UnknownAdapter,
    _In_  HV_AUDIO_DEVICE_TYPE DeviceType
);

NTSTATUS CreateMiniportTopologyHvAudio(
    _Out_ PUNKNOWN* Unknown,
    _In_  REFCLSID,
    _In_opt_ PUNKNOWN UnknownOuter,
    _In_  POOL_TYPE PoolType,
    _In_  PUNKNOWN UnknownAdapter,
    _In_  HV_AUDIO_DEVICE_TYPE DeviceType
);
