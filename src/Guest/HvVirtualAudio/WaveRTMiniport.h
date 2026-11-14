// ============================================================================
// WaveRTMiniport.h - PortCls WaveRT Miniport & Stream Declarations
// Project: VMStream - Guest Virtual Audio Driver (HvVirtualAudio.sys)
// ============================================================================
#pragma once

#include "AudioCommon.h"

class CMiniportWaveRTHvAudio;

// ============================================================================
// CMiniportWaveRTStreamHvAudio - WaveRT Stream with Notification Support
// ============================================================================
class CMiniportWaveRTStreamHvAudio :
    public IMiniportWaveRTStreamNotification,
    public CUnknown
{
public:
    DECLARE_STD_UNKNOWN();

    CMiniportWaveRTStreamHvAudio(_In_opt_ PUNKNOWN UnknownOuter);
    virtual ~CMiniportWaveRTStreamHvAudio();

    NTSTATUS Init(
        _In_ CMiniportWaveRTHvAudio* Miniport,
        _In_ PPORTWAVERTSTREAM       PortStream,
        _In_ ULONG                   PinId,
        _In_ BOOLEAN                 Capture,
        _In_ PKSDATAFORMAT           DataFormat
    );

    // IMiniportWaveRTStream methods
    STDMETHODIMP AllocateAudioBuffer(
        _In_  ULONG               RequestedSize,
        _Out_ PMDL*               AudioBufferMdl,
        _Out_ ULONG*              ActualSize,
        _Out_ ULONG*              OffsetFromFirstPage,
        _Out_ MEMORY_CACHING_TYPE* CacheType
    ) override;

    STDMETHODIMP_(VOID) FreeAudioBuffer(
        _In_opt_ PMDL  AudioBufferMdl,
        _In_     ULONG BufferSize
    ) override;

    STDMETHODIMP_(VOID) GetHWLatency(
        _Out_ KSRTAUDIO_HWLATENCY* HwLatency
    ) override;

    STDMETHODIMP GetPositionRegister(
        _Out_ KSRTAUDIO_HWREGISTER* Register
    ) override;

    STDMETHODIMP GetClockRegister(
        _Out_ KSRTAUDIO_HWREGISTER* Register
    ) override;

    STDMETHODIMP GetPosition(
        _Out_ PKSAUDIO_POSITION Position
    ) override;

    STDMETHODIMP SetFormat(
        _In_ PKSDATAFORMAT DataFormat
    ) override;

    STDMETHODIMP SetState(
        _In_ KSSTATE State
    ) override;

    // IMiniportWaveRTStreamNotification methods
    STDMETHODIMP AllocateBufferWithNotification(
        _In_  ULONG               NotificationCount,
        _In_  ULONG               RequestedSize,
        _Out_ PMDL*               AudioBufferMdl,
        _Out_ ULONG*              ActualSize,
        _Out_ ULONG*              OffsetFromFirstPage,
        _Out_ MEMORY_CACHING_TYPE* CacheType
    ) override;

    STDMETHODIMP_(VOID) FreeBufferWithNotification(
        _In_ PMDL  AudioBufferMdl,
        _In_ ULONG BufferSize
    ) override;

    STDMETHODIMP RegisterNotificationEvent(
        _In_ PKEVENT NotificationEvent
    ) override;

    STDMETHODIMP UnregisterNotificationEvent(
        _In_ PKEVENT NotificationEvent
    ) override;

    VOID ServiceWaveRTDmaTick();

private:
    static KDEFERRED_ROUTINE WaveRTTimerDpcRoutine;

    CMiniportWaveRTHvAudio* m_Miniport;
    PPORTWAVERTSTREAM       m_PortStream;
    ULONG                   m_PinId;
    BOOLEAN                 m_IsCapture;
    KSSTATE                 m_KsState;

    ULONG                   m_SampleRate;
    USHORT                  m_Channels;
    USHORT                  m_BitsPerSample;
    USHORT                  m_BlockAlign;
    ULONG                   m_BytesPerSecond;

    PMDL                    m_DmaMdl;
    PUCHAR                  m_DmaVirtualAddress;
    ULONG                   m_DmaBufferSize;
    ULONG                   m_DmaPosition;
    ULONGLONG               m_TotalBytesTransferred;
    ULONGLONG               m_LastQpcTimestamp;
    LARGE_INTEGER           m_QpcFrequency;

    KTIMER                  m_Timer;
    KDPC                    m_TimerDpc;
    KSPIN_LOCK              m_StreamLock;
    PKEVENT                 m_NotificationEvent;
};

// ============================================================================
// CMiniportWaveRTHvAudio - WaveRT Miniport Filter
// ============================================================================
class CMiniportWaveRTHvAudio :
    public IMiniportWaveRT,
    public CUnknown
{
public:
    DECLARE_STD_UNKNOWN();

    CMiniportWaveRTHvAudio(
        _In_opt_ PUNKNOWN UnknownOuter,
        _In_ HV_AUDIO_DEVICE_TYPE DeviceType
    );
    virtual ~CMiniportWaveRTHvAudio();

    // IMiniport methods
    STDMETHODIMP GetDescription(_Out_ PPCFILTER_DESCRIPTOR* Description) override;
    STDMETHODIMP DataRangeIntersection(
        _In_  ULONG PinId,
        _In_  PKSDATARANGE ClientDataRange,
        _In_  PKSDATARANGE MyDataRange,
        _In_  ULONG OutputBufferLength,
        _Out_writes_bytes_to_opt_(OutputBufferLength, *ResultantFormatLength) PVOID ResultantFormat,
        _Out_ PULONG ResultantFormatLength
    ) override;

    // IMiniportWaveRT methods
    STDMETHODIMP Init(
        _In_ PUNKNOWN      UnknownAdapter,
        _In_ PRESOURCELIST ResourceList,
        _In_ PPORTWAVERT   Port
    ) override;

    STDMETHODIMP NewStream(
        _Out_ PMINIPORTWAVERTSTREAM* OutStream,
        _In_  PPORTWAVERTSTREAM      OuterStream,
        _In_  ULONG                  Pin,
        _In_  BOOLEAN                Capture,
        _In_  PKSDATAFORMAT          DataFormat
    ) override;

    STDMETHODIMP GetDeviceDescription(
        _Out_ PDEVICE_DESCRIPTION DeviceDescription
    ) override;

    PHVAUDIOADAPTERCOMMON GetAdapterCommon() const { return m_AdapterCommon; }
    HV_AUDIO_DEVICE_TYPE  GetDeviceType() const { return m_DeviceType; }

private:
    HV_AUDIO_DEVICE_TYPE  m_DeviceType;
    PHVAUDIOADAPTERCOMMON m_AdapterCommon;
    PPORTWAVERT           m_Port;
};
