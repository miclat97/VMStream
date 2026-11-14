// ============================================================================
// TopologyMiniport.h — PortCls Topology Miniport for Speakers & Microphone
// Project: VMStream — Guest Virtual Audio Driver (HvVirtualAudio.sys)
// ============================================================================
#pragma once

#include "AudioCommon.h"

class CMiniportTopologyHvAudio :
    public IMiniportTopology,
    public CUnknown
{
public:
    DECLARE_STD_UNKNOWN();

    CMiniportTopologyHvAudio(
        _In_opt_ PUNKNOWN UnknownOuter,
        _In_ HV_AUDIO_DEVICE_TYPE DeviceType
    );
    virtual ~CMiniportTopologyHvAudio();

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

    // IMiniportTopology methods
    STDMETHODIMP Init(
        _In_ PUNKNOWN      UnknownAdapter,
        _In_ PRESOURCELIST ResourceList,
        _In_ PPORTTOPOLOGY Port
    ) override;

    static NTSTATUS PropertyHandlerVolume(
        _In_ PPCPROPERTY_REQUEST PropertyRequest
    );

    static NTSTATUS PropertyHandlerMute(
        _In_ PPCPROPERTY_REQUEST PropertyRequest
    );

    static NTSTATUS PropertyHandlerJackDescription(
        _In_ PPCPROPERTY_REQUEST PropertyRequest
    );

    HV_AUDIO_DEVICE_TYPE GetDeviceType() const { return m_DeviceType; }
    PHVAUDIOADAPTERCOMMON GetAdapterCommon() const { return m_AdapterCommon; }

private:
    HV_AUDIO_DEVICE_TYPE  m_DeviceType;
    PHVAUDIOADAPTERCOMMON m_AdapterCommon;
};
