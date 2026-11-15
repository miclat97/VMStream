// ============================================================================
// TopologyMiniport.cpp - PortCls Topology Miniport Implementation
// Exposes "Hyper-V Console Speakers" (KSNODETYPE_SPEAKER) and
// "Hyper-V Console Microphone" (KSNODETYPE_MICROPHONE) Core Audio Endpoints
// ============================================================================
#include "TopologyMiniport.h"

// Custom Component/Name GUIDs for Endpoint Friendly Names in HvVirtualAudio.inf
// {8D2A4E61-1010-4B9E-8F21-5C7E9A1D0001} -> "Hyper-V Console Speakers"
static const GUID HV_SPEAKER_NAME_GUID =
    { 0x8d2a4e61, 0x1010, 0x4b9e, { 0x8f, 0x21, 0x5c, 0x7e, 0x9a, 0x1d, 0x00, 0x01 } };

// {8D2A4E61-1010-4B9E-8F21-5C7E9A1D0002} -> "Hyper-V Console Microphone"
static const GUID HV_MIC_NAME_GUID =
    { 0x8d2a4e61, 0x1010, 0x4b9e, { 0x8f, 0x21, 0x5c, 0x7e, 0x9a, 0x1d, 0x00, 0x02 } };

static KSDATARANGE g_TopoBridgeDataRanges[] = {
    {
        sizeof(KSDATARANGE),
        0,
        0,
        0,
        STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
        STATICGUIDOF(KSDATAFORMAT_SUBTYPE_ANALOG),
        STATICGUIDOF(KSDATAFORMAT_SPECIFIER_NONE)
    }
};

static PKSDATARANGE g_TopoBridgeDataRangePointers[] = {
    &g_TopoBridgeDataRanges[0]
};

static PCPROPERTY_ITEM g_VolumeProperties[] = {
    {
        &KSPROPSETID_Audio,
        KSPROPERTY_AUDIO_VOLUMELEVEL,
        KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_SET | KSPROPERTY_TYPE_BASICSUPPORT,
        CMiniportTopologyHvAudio::PropertyHandlerVolume
    }
};

static PCAUTOMATION_TABLE g_VolumeAutomation = {
    sizeof(PCPROPERTY_ITEM),
    SIZEOF_ARRAY(g_VolumeProperties),
    g_VolumeProperties,
    0, 0, NULL,
    0, 0, NULL,
    0
};

static PCPROPERTY_ITEM g_MuteProperties[] = {
    {
        &KSPROPSETID_Audio,
        KSPROPERTY_AUDIO_MUTE,
        KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_SET | KSPROPERTY_TYPE_BASICSUPPORT,
        CMiniportTopologyHvAudio::PropertyHandlerMute
    }
};

static PCAUTOMATION_TABLE g_MuteAutomation = {
    sizeof(PCPROPERTY_ITEM),
    SIZEOF_ARRAY(g_MuteProperties),
    g_MuteProperties,
    0, 0, NULL,
    0, 0, NULL,
    0
};

static PCPROPERTY_ITEM g_PinJackProperties[] = {
    {
        &KSPROPSETID_Jack,
        KSPROPERTY_JACK_DESCRIPTION,
        KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_BASICSUPPORT,
        CMiniportTopologyHvAudio::PropertyHandlerJackDescription
    }
};

static PCAUTOMATION_TABLE g_PinJackAutomation = {
    sizeof(PCPROPERTY_ITEM),
    SIZEOF_ARRAY(g_PinJackProperties),
    g_PinJackProperties,
    0, 0, NULL,
    0, 0, NULL,
    0
};

static PCNODE_DESCRIPTOR g_TopologyNodes[] = {
    // KSNODE_TOPO_VOLUME (0)
    {
        0,
        &g_VolumeAutomation,
        &KSNODETYPE_VOLUME,
        &KSAUDFNAME_MASTER_VOLUME
    },
    // KSNODE_TOPO_MUTE (1)
    {
        0,
        &g_MuteAutomation,
        &KSNODETYPE_MUTE,
        &KSAUDFNAME_MASTER_MUTE
    }
};

// Render Topology Pins ("Hyper-V Console Speakers")
static PCPIN_DESCRIPTOR g_RenderTopoPins[] = {
    // KSPIN_TOPO_WAVEOUT_SOURCE (0) - Connected from WaveRT Render Bridge Pin
    {
        0, 0, 0,
        NULL,
        {
            0, NULL, 0, NULL,
            SIZEOF_ARRAY(g_TopoBridgeDataRangePointers),
            g_TopoBridgeDataRangePointers,
            KSPIN_DATAFLOW_IN,
            KSPIN_COMMUNICATION_NONE,
            &KSCATEGORY_AUDIO,
            NULL,
            0
        }
    },
    // KSPIN_TOPO_LINEOUT_DEST (1) - Speaker Endpoint Pin
    {
        0, 0, 0,
        &g_PinJackAutomation,
        {
            0, NULL, 0, NULL,
            SIZEOF_ARRAY(g_TopoBridgeDataRangePointers),
            g_TopoBridgeDataRangePointers,
            KSPIN_DATAFLOW_OUT,
            KSPIN_COMMUNICATION_NONE,
            &KSNODETYPE_SPEAKER,
            &HV_SPEAKER_NAME_GUID,
            0
        }
    }
};

// Capture Topology Pins ("Hyper-V Console Microphone")
static PCPIN_DESCRIPTOR g_CaptureTopoPins[] = {
    // KSPIN_TOPO_MIC_SOURCE (0) - Microphone Endpoint Pin
    {
        0, 0, 0,
        &g_PinJackAutomation,
        {
            0, NULL, 0, NULL,
            SIZEOF_ARRAY(g_TopoBridgeDataRangePointers),
            g_TopoBridgeDataRangePointers,
            KSPIN_DATAFLOW_IN,
            KSPIN_COMMUNICATION_NONE,
            &KSNODETYPE_MICROPHONE,
            &HV_MIC_NAME_GUID,
            0
        }
    },
    // KSPIN_TOPO_WAVEIN_DEST (1) - Connected to WaveRT Capture Bridge Pin
    {
        0, 0, 0,
        NULL,
        {
            0, NULL, 0, NULL,
            SIZEOF_ARRAY(g_TopoBridgeDataRangePointers),
            g_TopoBridgeDataRangePointers,
            KSPIN_DATAFLOW_OUT,
            KSPIN_COMMUNICATION_NONE,
            &KSCATEGORY_AUDIO,
            NULL,
            0
        }
    }
};

static PCCONNECTION_DESCRIPTOR g_TopoConnections[] = {
    // Pin 0 -> Volume Node -> Mute Node -> Pin 1
    { PCFILTER_NODE,      0,                  KSNODE_TOPO_VOLUME, 1 },
    { KSNODE_TOPO_VOLUME, 0,                  KSNODE_TOPO_MUTE,   1 },
    { KSNODE_TOPO_MUTE,   0,                  PCFILTER_NODE,      1 }
};

static GUID g_RenderTopoCategories[] = {
    STATICGUIDOF(KSCATEGORY_AUDIO),
    STATICGUIDOF(KSCATEGORY_TOPOLOGY)
};

static GUID g_CaptureTopoCategories[] = {
    STATICGUIDOF(KSCATEGORY_AUDIO),
    STATICGUIDOF(KSCATEGORY_TOPOLOGY)
};

static PCFILTER_DESCRIPTOR g_RenderTopoFilterDescriptor = {
    0,
    NULL,
    sizeof(PCPIN_DESCRIPTOR),
    SIZEOF_ARRAY(g_RenderTopoPins),
    g_RenderTopoPins,
    sizeof(PCNODE_DESCRIPTOR),
    SIZEOF_ARRAY(g_TopologyNodes),
    g_TopologyNodes,
    sizeof(PCCONNECTION_DESCRIPTOR),
    SIZEOF_ARRAY(g_TopoConnections),
    g_TopoConnections,
    SIZEOF_ARRAY(g_RenderTopoCategories),
    g_RenderTopoCategories
};

static PCFILTER_DESCRIPTOR g_CaptureTopoFilterDescriptor = {
    0,
    NULL,
    sizeof(PCPIN_DESCRIPTOR),
    SIZEOF_ARRAY(g_CaptureTopoPins),
    g_CaptureTopoPins,
    sizeof(PCNODE_DESCRIPTOR),
    SIZEOF_ARRAY(g_TopologyNodes),
    g_TopologyNodes,
    sizeof(PCCONNECTION_DESCRIPTOR),
    SIZEOF_ARRAY(g_TopoConnections),
    g_TopoConnections,
    SIZEOF_ARRAY(g_CaptureTopoCategories),
    g_CaptureTopoCategories
};

// ============================================================================
// Factory Function
// ============================================================================
NTSTATUS CreateMiniportTopologyHvAudio(
    _Out_ PUNKNOWN* Unknown,
    _In_  REFCLSID,
    _In_opt_ PUNKNOWN UnknownOuter,
    _In_  POOL_TYPE PoolType,
    _In_  PUNKNOWN UnknownAdapter,
    _In_  HV_AUDIO_DEVICE_TYPE DeviceType
)
{
    UNREFERENCED_PARAMETER(UnknownAdapter);

    CMiniportTopologyHvAudio* obj = new (PoolType, HVAUDIO_POOL_TAG)
        CMiniportTopologyHvAudio(UnknownOuter, DeviceType);
    if (obj == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    obj->AddRef();
    *Unknown = reinterpret_cast<IUnknown*>(obj);
    return STATUS_SUCCESS;
}

// ============================================================================
// CMiniportTopologyHvAudio Implementation
// ============================================================================
CMiniportTopologyHvAudio::CMiniportTopologyHvAudio(
    _In_opt_ PUNKNOWN UnknownOuter,
    _In_ HV_AUDIO_DEVICE_TYPE DeviceType
) : CUnknown(UnknownOuter),
    m_DeviceType(DeviceType),
    m_AdapterCommon(NULL)
{
}

CMiniportTopologyHvAudio::~CMiniportTopologyHvAudio()
{
    if (m_AdapterCommon != NULL) {
        m_AdapterCommon->Release();
        m_AdapterCommon = NULL;
    }
}

STDMETHODIMP
CMiniportTopologyHvAudio::NonDelegatingQueryInterface(
    _In_ REFIID Interface,
    _COM_Outptr_ PVOID* Object
)
{
    if (IsEqualGUIDAligned(Interface, IID_IUnknown)) {
        *Object = reinterpret_cast<PVOID>(static_cast<PUNKNOWN>(this));
    } else if (IsEqualGUIDAligned(Interface, IID_IMiniport)) {
        *Object = reinterpret_cast<PVOID>(static_cast<PMINIPORT>(this));
    } else if (IsEqualGUIDAligned(Interface, IID_IMiniportTopology)) {
        *Object = reinterpret_cast<PVOID>(static_cast<PMINIPORTTOPOLOGY>(this));
    } else {
        *Object = NULL;
        return STATUS_INVALID_PARAMETER;
    }

    reinterpret_cast<PUNKNOWN>(*Object)->AddRef();
    return STATUS_SUCCESS;
}

STDMETHODIMP
CMiniportTopologyHvAudio::Init(
    _In_ PUNKNOWN      UnknownAdapter,
    _In_ PRESOURCELIST ResourceList,
    _In_ PPORTTOPOLOGY Port
)
{
    UNREFERENCED_PARAMETER(ResourceList);
    UNREFERENCED_PARAMETER(Port);

    if (UnknownAdapter == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    return UnknownAdapter->QueryInterface(
        IID_IHvAudioAdapterCommon,
        reinterpret_cast<PVOID*>(&m_AdapterCommon)
    );
}

STDMETHODIMP
CMiniportTopologyHvAudio::GetDescription(
    _Out_ PPCFILTER_DESCRIPTOR* Description
)
{
    if (Description == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    *Description = (m_DeviceType == HvAudioDeviceType_Render)
        ? &g_RenderTopoFilterDescriptor
        : &g_CaptureTopoFilterDescriptor;

    return STATUS_SUCCESS;
}

STDMETHODIMP
CMiniportTopologyHvAudio::DataRangeIntersection(
    _In_  ULONG PinId,
    _In_  PKSDATARANGE ClientDataRange,
    _In_  PKSDATARANGE MyDataRange,
    _In_  ULONG OutputBufferLength,
    _Out_writes_bytes_to_opt_(OutputBufferLength, *ResultantFormatLength) PVOID ResultantFormat,
    _Out_ PULONG ResultantFormatLength
)
{
    UNREFERENCED_PARAMETER(PinId);
    UNREFERENCED_PARAMETER(ClientDataRange);
    UNREFERENCED_PARAMETER(MyDataRange);
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(ResultantFormat);
    UNREFERENCED_PARAMETER(ResultantFormatLength);

    return STATUS_NOT_IMPLEMENTED;
}

// ============================================================================
// Property Handlers (Volume, Mute, KSJackDescription)
// ============================================================================
NTSTATUS
CMiniportTopologyHvAudio::PropertyHandlerVolume(
    _In_ PPCPROPERTY_REQUEST PropertyRequest
)
{
    auto* miniport = reinterpret_cast<CMiniportTopologyHvAudio*>(PropertyRequest->MajorTarget);
    if (miniport == NULL || miniport->GetAdapterCommon() == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    if (PropertyRequest->Verb & KSPROPERTY_TYPE_BASICSUPPORT) {
        ULONG flags = PropertyRequest->PropertyItem->Flags;
        if (PropertyRequest->ValueSize >= sizeof(KSPROPERTY_DESCRIPTION) + sizeof(KSPROPERTY_MEMBERSHEADER) + sizeof(KSPROPERTY_STEPPING_LONG)) {
            auto* desc = reinterpret_cast<PKSPROPERTY_DESCRIPTION>(PropertyRequest->Value);
            desc->AccessFlags = flags;
            desc->DescriptionSize = sizeof(KSPROPERTY_DESCRIPTION) + sizeof(KSPROPERTY_MEMBERSHEADER) + sizeof(KSPROPERTY_STEPPING_LONG);
            desc->PropTypeSet.Set = KSPROPTYPESETID_General;
            desc->PropTypeSet.Id = VT_I4;
            desc->PropTypeSet.Flags = 0;
            desc->MembersListCount = 1;
            desc->Reserved = 0;

            auto* members = reinterpret_cast<PKSPROPERTY_MEMBERSHEADER>(desc + 1);
            members->MembersFlags = KSPROPERTY_MEMBER_STEPPEDRANGES;
            members->MembersSize = sizeof(KSPROPERTY_STEPPING_LONG);
            members->MembersCount = 1;
            members->Flags = 0;

            auto* range = reinterpret_cast<PKSPROPERTY_STEPPING_LONG>(members + 1);
            range->SteppingDelta = 0x00008000;      // 0.5 dB step in 1/65536 dB units
            range->Reserved = 0;
            range->Bounds.SignedMinimum = -96 * 0x10000; // -96 dB
            range->Bounds.SignedMaximum = 0;             // 0 dB

            PropertyRequest->ValueSize = desc->DescriptionSize;
            return STATUS_SUCCESS;
        } else if (PropertyRequest->ValueSize >= sizeof(ULONG)) {
            *reinterpret_cast<PULONG>(PropertyRequest->Value) = flags;
            PropertyRequest->ValueSize = sizeof(ULONG);
            return STATUS_SUCCESS;
        }
        PropertyRequest->ValueSize = sizeof(KSPROPERTY_DESCRIPTION) + sizeof(KSPROPERTY_MEMBERSHEADER) + sizeof(KSPROPERTY_STEPPING_LONG);
        return STATUS_BUFFER_OVERFLOW;
    }

    if (PropertyRequest->ValueSize < sizeof(LONG)) {
        PropertyRequest->ValueSize = sizeof(LONG);
        return STATUS_BUFFER_TOO_SMALL;
    }

    if (PropertyRequest->Verb & KSPROPERTY_TYPE_GET) {
        *reinterpret_cast<PLONG>(PropertyRequest->Value) =
            miniport->GetAdapterCommon()->GetVolumeLevel(miniport->GetDeviceType());
        PropertyRequest->ValueSize = sizeof(LONG);
        return STATUS_SUCCESS;
    }

    if (PropertyRequest->Verb & KSPROPERTY_TYPE_SET) {
        miniport->GetAdapterCommon()->SetVolumeLevel(
            miniport->GetDeviceType(),
            *reinterpret_cast<PLONG>(PropertyRequest->Value)
        );
        return STATUS_SUCCESS;
    }

    return STATUS_INVALID_PARAMETER;
}

NTSTATUS
CMiniportTopologyHvAudio::PropertyHandlerMute(
    _In_ PPCPROPERTY_REQUEST PropertyRequest
)
{
    auto* miniport = reinterpret_cast<CMiniportTopologyHvAudio*>(PropertyRequest->MajorTarget);
    if (miniport == NULL || miniport->GetAdapterCommon() == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    if (PropertyRequest->ValueSize < sizeof(BOOL)) {
        PropertyRequest->ValueSize = sizeof(BOOL);
        return STATUS_BUFFER_TOO_SMALL;
    }

    if (PropertyRequest->Verb & KSPROPERTY_TYPE_GET) {
        *reinterpret_cast<PBOOL>(PropertyRequest->Value) =
            miniport->GetAdapterCommon()->GetMuteState(miniport->GetDeviceType());
        PropertyRequest->ValueSize = sizeof(BOOL);
        return STATUS_SUCCESS;
    }

    if (PropertyRequest->Verb & KSPROPERTY_TYPE_SET) {
        miniport->GetAdapterCommon()->SetMuteState(
            miniport->GetDeviceType(),
            *reinterpret_cast<PBOOL>(PropertyRequest->Value)
        );
        return STATUS_SUCCESS;
    }

    return STATUS_INVALID_PARAMETER;
}

NTSTATUS
CMiniportTopologyHvAudio::PropertyHandlerJackDescription(
    _In_ PPCPROPERTY_REQUEST PropertyRequest
)
{
    auto* miniport = reinterpret_cast<CMiniportTopologyHvAudio*>(PropertyRequest->MajorTarget);
    if (miniport == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    ULONG requiredSize = sizeof(KSMULTIPLE_ITEM) + sizeof(KSJACK_DESCRIPTION);
    if (PropertyRequest->ValueSize < requiredSize) {
        PropertyRequest->ValueSize = requiredSize;
        return STATUS_BUFFER_TOO_SMALL;
    }

    if (PropertyRequest->Verb & KSPROPERTY_TYPE_GET) {
        auto* multiItem = reinterpret_cast<PKSMULTIPLE_ITEM>(PropertyRequest->Value);
        multiItem->Size = requiredSize;
        multiItem->Count = 1;

        auto* jack = reinterpret_cast<PKSJACK_DESCRIPTION>(multiItem + 1);
        RtlZeroMemory(jack, sizeof(KSJACK_DESCRIPTION));
        jack->ChannelMapping = SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT;
        jack->Color = 0x00000000;
        jack->ConnectionType = eConnTypeOtherDigital;
        jack->GeoLocation = eGeoLocVirtual;
        jack->GenLocation = eGenLocInternal;
        jack->PortConnection = ePortConnIntegratedDevice;
        jack->IsConnected = TRUE;

        PropertyRequest->ValueSize = requiredSize;
        return STATUS_SUCCESS;
    }

    return STATUS_INVALID_PARAMETER;
}
