// ============================================================================
// WaveRTMiniport.cpp - PortCls WaveRT Miniport & Stream Implementation
// Project: VMStream - Guest Virtual Audio Driver (HvVirtualAudio.sys)
// ============================================================================
#include "WaveRTMiniport.h"

// ============================================================================
// Supported PCM Data Ranges (44.1 kHz & 48 kHz, 16/24-bit Render; 16-bit Mic)
// ============================================================================
static KSDATARANGE_AUDIO g_RenderPcmDataRanges[] = {
    // 48 kHz, 16-bit, 2-channel PCM
    {
        {
            sizeof(KSDATARANGE_AUDIO),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        HV_AUDIO_CHANNELS_STEREO,
        HV_AUDIO_BITS_PER_SAMPLE_16,
        HV_AUDIO_BITS_PER_SAMPLE_16,
        HV_AUDIO_SAMPLE_RATE_48000,
        HV_AUDIO_SAMPLE_RATE_48000
    },
    // 44.1 kHz, 16-bit, 2-channel PCM
    {
        {
            sizeof(KSDATARANGE_AUDIO),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        HV_AUDIO_CHANNELS_STEREO,
        HV_AUDIO_BITS_PER_SAMPLE_16,
        HV_AUDIO_BITS_PER_SAMPLE_16,
        HV_AUDIO_SAMPLE_RATE_44100,
        HV_AUDIO_SAMPLE_RATE_44100
    },
    // 48 kHz, 24-bit, 2-channel PCM
    {
        {
            sizeof(KSDATARANGE_AUDIO),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        HV_AUDIO_CHANNELS_STEREO,
        HV_AUDIO_BITS_PER_SAMPLE_24,
        HV_AUDIO_BITS_PER_SAMPLE_24,
        HV_AUDIO_SAMPLE_RATE_48000,
        HV_AUDIO_SAMPLE_RATE_48000
    },
    // 44.1 kHz, 24-bit, 2-channel PCM
    {
        {
            sizeof(KSDATARANGE_AUDIO),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        HV_AUDIO_CHANNELS_STEREO,
        HV_AUDIO_BITS_PER_SAMPLE_24,
        HV_AUDIO_BITS_PER_SAMPLE_24,
        HV_AUDIO_SAMPLE_RATE_44100,
        HV_AUDIO_SAMPLE_RATE_44100
    }
};

static PKSDATARANGE g_RenderPcmDataRangePointers[] = {
    reinterpret_cast<PKSDATARANGE>(&g_RenderPcmDataRanges[0]),
    reinterpret_cast<PKSDATARANGE>(&g_RenderPcmDataRanges[1]),
    reinterpret_cast<PKSDATARANGE>(&g_RenderPcmDataRanges[2]),
    reinterpret_cast<PKSDATARANGE>(&g_RenderPcmDataRanges[3])
};

static KSDATARANGE_AUDIO g_CapturePcmDataRanges[] = {
    // 48 kHz, 16-bit, 2-channel PCM
    {
        {
            sizeof(KSDATARANGE_AUDIO),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        HV_AUDIO_CHANNELS_STEREO,
        HV_AUDIO_BITS_PER_SAMPLE_16,
        HV_AUDIO_BITS_PER_SAMPLE_16,
        HV_AUDIO_SAMPLE_RATE_48000,
        HV_AUDIO_SAMPLE_RATE_48000
    },
    // 44.1 kHz, 16-bit, 2-channel PCM
    {
        {
            sizeof(KSDATARANGE_AUDIO),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        HV_AUDIO_CHANNELS_STEREO,
        HV_AUDIO_BITS_PER_SAMPLE_16,
        HV_AUDIO_BITS_PER_SAMPLE_16,
        HV_AUDIO_SAMPLE_RATE_44100,
        HV_AUDIO_SAMPLE_RATE_44100
    },
    // 48 kHz, 16-bit, 1-channel Mono PCM
    {
        {
            sizeof(KSDATARANGE_AUDIO),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        HV_AUDIO_CHANNELS_MONO,
        HV_AUDIO_BITS_PER_SAMPLE_16,
        HV_AUDIO_BITS_PER_SAMPLE_16,
        HV_AUDIO_SAMPLE_RATE_48000,
        HV_AUDIO_SAMPLE_RATE_48000
    },
    // 44.1 kHz, 16-bit, 1-channel Mono PCM
    {
        {
            sizeof(KSDATARANGE_AUDIO),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        HV_AUDIO_CHANNELS_MONO,
        HV_AUDIO_BITS_PER_SAMPLE_16,
        HV_AUDIO_BITS_PER_SAMPLE_16,
        HV_AUDIO_SAMPLE_RATE_44100,
        HV_AUDIO_SAMPLE_RATE_44100
    }
};

static PKSDATARANGE g_CapturePcmDataRangePointers[] = {
    reinterpret_cast<PKSDATARANGE>(&g_CapturePcmDataRanges[0]),
    reinterpret_cast<PKSDATARANGE>(&g_CapturePcmDataRanges[1]),
    reinterpret_cast<PKSDATARANGE>(&g_CapturePcmDataRanges[2]),
    reinterpret_cast<PKSDATARANGE>(&g_CapturePcmDataRanges[3])
};

static KSDATARANGE g_WaveBridgeDataRanges[] = {
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

static PKSDATARANGE g_WaveBridgeDataRangePointers[] = {
    &g_WaveBridgeDataRanges[0]
};

// ============================================================================
// WaveRT Filter Pin Descriptors & Connections
// ============================================================================
static PCPIN_DESCRIPTOR g_RenderWavePins[] = {
    // KSPIN_WAVERT_HOST_OR_SINK (0) - System Render Stream Sink
    {
        2, 2, 0,
        NULL,
        {
            0, NULL, 0, NULL,
            SIZEOF_ARRAY(g_RenderPcmDataRangePointers),
            g_RenderPcmDataRangePointers,
            KSPIN_DATAFLOW_IN,
            KSPIN_COMMUNICATION_SINK,
            &KSCATEGORY_AUDIO,
            NULL,
            0
        }
    },
    // KSPIN_WAVERT_BRIDGE (1) - Bridge to Render Topology
    {
        0, 0, 0,
        NULL,
        {
            0, NULL, 0, NULL,
            SIZEOF_ARRAY(g_WaveBridgeDataRangePointers),
            g_WaveBridgeDataRangePointers,
            KSPIN_DATAFLOW_OUT,
            KSPIN_COMMUNICATION_NONE,
            &KSCATEGORY_AUDIO,
            NULL,
            0
        }
    }
};

static PCPIN_DESCRIPTOR g_CaptureWavePins[] = {
    // KSPIN_WAVERT_HOST_OR_SINK (0) - System Capture Stream Source
    {
        2, 2, 0,
        NULL,
        {
            0, NULL, 0, NULL,
            SIZEOF_ARRAY(g_CapturePcmDataRangePointers),
            g_CapturePcmDataRangePointers,
            KSPIN_DATAFLOW_OUT,
            KSPIN_COMMUNICATION_SINK,
            &KSCATEGORY_AUDIO,
            &KSAUDFNAME_RECORDING_CONTROL,
            0
        }
    },
    // KSPIN_WAVERT_BRIDGE (1) - Bridge from Capture Topology
    {
        0, 0, 0,
        NULL,
        {
            0, NULL, 0, NULL,
            SIZEOF_ARRAY(g_WaveBridgeDataRangePointers),
            g_WaveBridgeDataRangePointers,
            KSPIN_DATAFLOW_IN,
            KSPIN_COMMUNICATION_NONE,
            &KSCATEGORY_AUDIO,
            NULL,
            0
        }
    }
};

static PCNODE_DESCRIPTOR g_WaveNodes[] = {
    // Node 0: DAC (Render) or ADC (Capture)
    {
        0, NULL, &KSNODETYPE_DAC, NULL
    }
};

static PCNODE_DESCRIPTOR g_CaptureWaveNodes[] = {
    {
        0, NULL, &KSNODETYPE_ADC, NULL
    }
};

static PCCONNECTION_DESCRIPTOR g_RenderWaveConnections[] = {
    { PCFILTER_NODE, 0, 0,             1 },
    { 0,             0, PCFILTER_NODE, 1 }
};

static PCCONNECTION_DESCRIPTOR g_CaptureWaveConnections[] = {
    { PCFILTER_NODE, 1, 0,             1 },
    { 0,             0, PCFILTER_NODE, 0 }
};

static GUID g_RenderWaveCategories[] = {
    STATICGUIDOF(KSCATEGORY_AUDIO),
    STATICGUIDOF(KSCATEGORY_RENDER),
    STATICGUIDOF(KSCATEGORY_REALTIME)
};

static GUID g_CaptureWaveCategories[] = {
    STATICGUIDOF(KSCATEGORY_AUDIO),
    STATICGUIDOF(KSCATEGORY_CAPTURE),
    STATICGUIDOF(KSCATEGORY_REALTIME)
};

static PCFILTER_DESCRIPTOR g_RenderWaveFilterDescriptor = {
    0,
    NULL,
    sizeof(PCPIN_DESCRIPTOR),
    SIZEOF_ARRAY(g_RenderWavePins),
    g_RenderWavePins,
    sizeof(PCNODE_DESCRIPTOR),
    SIZEOF_ARRAY(g_WaveNodes),
    g_WaveNodes,
    SIZEOF_ARRAY(g_RenderWaveConnections),
    g_RenderWaveConnections,
    SIZEOF_ARRAY(g_RenderWaveCategories),
    g_RenderWaveCategories
};

static PCFILTER_DESCRIPTOR g_CaptureWaveFilterDescriptor = {
    0,
    NULL,
    sizeof(PCPIN_DESCRIPTOR),
    SIZEOF_ARRAY(g_CaptureWavePins),
    g_CaptureWavePins,
    sizeof(PCNODE_DESCRIPTOR),
    SIZEOF_ARRAY(g_CaptureWaveNodes),
    g_CaptureWaveNodes,
    SIZEOF_ARRAY(g_CaptureWaveConnections),
    g_CaptureWaveConnections,
    SIZEOF_ARRAY(g_CaptureWaveCategories),
    g_CaptureWaveCategories
};

// ============================================================================
// Factory Function
// ============================================================================
NTSTATUS CreateMiniportWaveRTHvAudio(
    _Out_ PUNKNOWN* Unknown,
    _In_  REFCLSID,
    _In_opt_ PUNKNOWN UnknownOuter,
    _In_  POOL_TYPE PoolType,
    _In_  PUNKNOWN UnknownAdapter,
    _In_  HV_AUDIO_DEVICE_TYPE DeviceType
)
{
    UNREFERENCED_PARAMETER(UnknownAdapter);

    CMiniportWaveRTHvAudio* obj = new (PoolType, HVAUDIO_POOL_TAG)
        CMiniportWaveRTHvAudio(UnknownOuter, DeviceType);
    if (obj == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    obj->AddRef();
    *Unknown = reinterpret_cast<IUnknown*>(obj);
    return STATUS_SUCCESS;
}

// ============================================================================
// CMiniportWaveRTHvAudio Implementation
// ============================================================================
CMiniportWaveRTHvAudio::CMiniportWaveRTHvAudio(
    _In_opt_ PUNKNOWN UnknownOuter,
    _In_ HV_AUDIO_DEVICE_TYPE DeviceType
) : CUnknown(UnknownOuter),
    m_DeviceType(DeviceType),
    m_AdapterCommon(NULL),
    m_Port(NULL)
{
}

CMiniportWaveRTHvAudio::~CMiniportWaveRTHvAudio()
{
    if (m_Port != NULL) {
        m_Port->Release();
        m_Port = NULL;
    }
    if (m_AdapterCommon != NULL) {
        m_AdapterCommon->Release();
        m_AdapterCommon = NULL;
    }
}

STDMETHODIMP
CMiniportWaveRTHvAudio::NonDelegatingQueryInterface(
    _In_ REFIID Interface,
    _COM_Outptr_ PVOID* Object
)
{
    if (IsEqualGUIDAligned(Interface, IID_IUnknown)) {
        *Object = reinterpret_cast<PVOID>(static_cast<PUNKNOWN>(this));
    } else if (IsEqualGUIDAligned(Interface, IID_IMiniport)) {
        *Object = reinterpret_cast<PVOID>(static_cast<PMINIPORT>(this));
    } else if (IsEqualGUIDAligned(Interface, IID_IMiniportWaveRT)) {
        *Object = reinterpret_cast<PVOID>(static_cast<PMINIPORTWAVERT>(this));
    } else {
        *Object = NULL;
        return STATUS_INVALID_PARAMETER;
    }

    reinterpret_cast<PUNKNOWN>(*Object)->AddRef();
    return STATUS_SUCCESS;
}

STDMETHODIMP
CMiniportWaveRTHvAudio::Init(
    _In_ PUNKNOWN      UnknownAdapter,
    _In_ PRESOURCELIST ResourceList,
    _In_ PPORTWAVERT   Port
)
{
    UNREFERENCED_PARAMETER(ResourceList);

    if (UnknownAdapter == NULL || Port == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    m_Port = Port;
    m_Port->AddRef();

    return UnknownAdapter->QueryInterface(
        IID_IHvAudioAdapterCommon,
        reinterpret_cast<PVOID*>(&m_AdapterCommon)
    );
}

STDMETHODIMP
CMiniportWaveRTHvAudio::GetDescription(
    _Out_ PPCFILTER_DESCRIPTOR* Description
)
{
    if (Description == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    *Description = (m_DeviceType == HvAudioDeviceType_Render)
        ? &g_RenderWaveFilterDescriptor
        : &g_CaptureWaveFilterDescriptor;

    return STATUS_SUCCESS;
}

STDMETHODIMP
CMiniportWaveRTHvAudio::GetDeviceDescription(
    _Out_ PDEVICE_DESCRIPTION DeviceDescription
)
{
    if (DeviceDescription == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    RtlZeroMemory(DeviceDescription, sizeof(DEVICE_DESCRIPTION));
    DeviceDescription->Version = DEVICE_DESCRIPTION_VERSION2;
    DeviceDescription->Master = TRUE;
    DeviceDescription->ScatterGather = TRUE;
    DeviceDescription->Dma32BitAddresses = TRUE;
    DeviceDescription->Dma64BitAddresses = TRUE;
    DeviceDescription->InterfaceType = Internal;
    DeviceDescription->MaximumLength = 0x100000;

    return STATUS_SUCCESS;
}

STDMETHODIMP
CMiniportWaveRTHvAudio::DataRangeIntersection(
    _In_  ULONG PinId,
    _In_  PKSDATARANGE ClientDataRange,
    _In_  PKSDATARANGE MyDataRange,
    _In_  ULONG OutputBufferLength,
    _Out_writes_bytes_to_opt_(OutputBufferLength, *ResultantFormatLength) PVOID ResultantFormat,
    _Out_ PULONG ResultantFormatLength
)
{
    if (ClientDataRange == NULL || MyDataRange == NULL || ResultantFormatLength == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    if (PinId == KSPIN_WAVERT_BRIDGE || MyDataRange->FormatSize < sizeof(KSDATARANGE_AUDIO)) {
        return STATUS_NO_MATCH;
    }

    if (!IsEqualGUIDAligned(ClientDataRange->MajorFormat, KSDATAFORMAT_TYPE_AUDIO) &&
        !IsEqualGUIDAligned(ClientDataRange->MajorFormat, KSDATAFORMAT_TYPE_WILDCARD))
    {
        return STATUS_NO_MATCH;
    }

    if (!IsEqualGUIDAligned(ClientDataRange->SubFormat, KSDATAFORMAT_SUBTYPE_PCM) &&
        !IsEqualGUIDAligned(ClientDataRange->SubFormat, KSDATAFORMAT_SUBTYPE_WILDCARD))
    {
        return STATUS_NO_MATCH;
    }

    if (!IsEqualGUIDAligned(ClientDataRange->Specifier, KSDATAFORMAT_SPECIFIER_WAVEFORMATEX) &&
        !IsEqualGUIDAligned(ClientDataRange->Specifier, KSDATAFORMAT_SPECIFIER_WILDCARD))
    {
        return STATUS_NO_MATCH;
    }

    auto* myAudioRange = reinterpret_cast<PKSDATARANGE_AUDIO>(MyDataRange);
    ULONG  targetSampleRate    = myAudioRange->MaximumSampleFrequency;
    USHORT targetChannels      = static_cast<USHORT>(myAudioRange->MaximumChannels);
    USHORT targetBitsPerSample = static_cast<USHORT>(myAudioRange->MaximumBitsPerSample);

    // Validate client constraints BEFORE checking OutputBufferLength so PortCls iterates cleanly
    if (ClientDataRange->FormatSize >= sizeof(KSDATARANGE_AUDIO)) {
        auto* clientAudio = reinterpret_cast<PKSDATARANGE_AUDIO>(ClientDataRange);

        if (clientAudio->MaximumChannels == 0) {
            return STATUS_NO_MATCH;
        }
        if (clientAudio->MaximumChannels < targetChannels) {
            if (m_DeviceType == HvAudioDeviceType_Capture || clientAudio->MaximumChannels < 1) {
                return STATUS_NO_MATCH;
            }
            targetChannels = static_cast<USHORT>(clientAudio->MaximumChannels);
        }

        if (clientAudio->MaximumBitsPerSample > 0 &&
            (targetBitsPerSample < clientAudio->MinimumBitsPerSample ||
             targetBitsPerSample > clientAudio->MaximumBitsPerSample))
        {
            return STATUS_NO_MATCH;
        }

        if (clientAudio->MaximumSampleFrequency > 0 &&
            (targetSampleRate < clientAudio->MinimumSampleFrequency ||
             targetSampleRate > clientAudio->MaximumSampleFrequency))
        {
            return STATUS_NO_MATCH;
        }
    }

    const ULONG requiredSize = sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE);
    *ResultantFormatLength = requiredSize;

    if (OutputBufferLength == 0) {
        return STATUS_BUFFER_OVERFLOW;
    }

    if (OutputBufferLength < requiredSize) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    if (ResultantFormat == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    auto* waveFormatEx = reinterpret_cast<PKSDATAFORMAT_WAVEFORMATEXTENSIBLE>(ResultantFormat);
    RtlZeroMemory(waveFormatEx, requiredSize);

    USHORT containerBytes = (targetBitsPerSample == 24) ? 3 : 2;
    USHORT blockAlign = static_cast<USHORT>(targetChannels * containerBytes);

    waveFormatEx->DataFormat.FormatSize = requiredSize;
    waveFormatEx->DataFormat.Flags = 0;
    waveFormatEx->DataFormat.SampleSize = blockAlign;
    waveFormatEx->DataFormat.Reserved = 0;
    waveFormatEx->DataFormat.MajorFormat = KSDATAFORMAT_TYPE_AUDIO;
    waveFormatEx->DataFormat.SubFormat = KSDATAFORMAT_SUBTYPE_PCM;
    waveFormatEx->DataFormat.Specifier = KSDATAFORMAT_SPECIFIER_WAVEFORMATEX;

    waveFormatEx->WaveFormatExt.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    waveFormatEx->WaveFormatExt.Format.nChannels = targetChannels;
    waveFormatEx->WaveFormatExt.Format.nSamplesPerSec = targetSampleRate;
    waveFormatEx->WaveFormatExt.Format.wBitsPerSample = targetBitsPerSample;
    waveFormatEx->WaveFormatExt.Format.nBlockAlign = blockAlign;
    waveFormatEx->WaveFormatExt.Format.nAvgBytesPerSec = targetSampleRate * blockAlign;
    waveFormatEx->WaveFormatExt.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
    waveFormatEx->WaveFormatExt.Samples.wValidBitsPerSample = targetBitsPerSample;
    waveFormatEx->WaveFormatExt.dwChannelMask = (targetChannels == 1)
        ? SPEAKER_FRONT_CENTER
        : (SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT);
    waveFormatEx->WaveFormatExt.SubFormat = KSDATAFORMAT_SUBTYPE_PCM;

    return STATUS_SUCCESS;
}

STDMETHODIMP
CMiniportWaveRTHvAudio::NewStream(
    _Out_ PMINIPORTWAVERTSTREAM* OutStream,
    _In_  PPORTWAVERTSTREAM      OuterStream,
    _In_  ULONG                  Pin,
    _In_  BOOLEAN                Capture,
    _In_  PKSDATAFORMAT          DataFormat
)
{
    if (OutStream == NULL || DataFormat == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    auto* stream = new (NonPagedPoolNx, HVAUDIO_POOL_TAG) CMiniportWaveRTStreamHvAudio(NULL);
    if (stream == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    stream->AddRef();
    NTSTATUS status = stream->Init(this, OuterStream, Pin, Capture, DataFormat);
    if (!NT_SUCCESS(status)) {
        stream->Release();
        *OutStream = NULL;
        return status;
    }

    *OutStream = static_cast<PMINIPORTWAVERTSTREAM>(stream);
    return STATUS_SUCCESS;
}

// ============================================================================
// CMiniportWaveRTStreamHvAudio Implementation
// ============================================================================
CMiniportWaveRTStreamHvAudio::CMiniportWaveRTStreamHvAudio(
    _In_opt_ PUNKNOWN UnknownOuter
) : CUnknown(UnknownOuter),
    m_Miniport(NULL),
    m_PortStream(NULL),
    m_PinId(0),
    m_IsCapture(FALSE),
    m_KsState(KSSTATE_STOP),
    m_SampleRate(HV_AUDIO_SAMPLE_RATE),
    m_Channels(HV_AUDIO_CHANNELS_STEREO),
    m_BitsPerSample(HV_AUDIO_BITS_PER_SAMPLE_16),
    m_BlockAlign(HV_AUDIO_BLOCK_ALIGN_STEREO_16),
    m_BytesPerSecond(HV_AUDIO_SAMPLE_RATE * HV_AUDIO_BLOCK_ALIGN_STEREO_16),
    m_DmaMdl(NULL),
    m_DmaVirtualAddress(NULL),
    m_DmaBufferSize(0),
    m_DmaPosition(0),
    m_NotificationCount(2),
    m_TotalBytesTransferred(0),
    m_TotalFramesTransferred(0),
    m_RunStartQpc(0),
    m_NotificationEvent(NULL)
{
    KeInitializeSpinLock(&m_StreamLock);
    KeInitializeTimerEx(&m_Timer, NotificationTimer);
    KeInitializeDpc(&m_TimerDpc, WaveRTTimerDpcRoutine, this);
    KeQueryPerformanceCounter(&m_QpcFrequency);
}

CMiniportWaveRTStreamHvAudio::~CMiniportWaveRTStreamHvAudio()
{
    KeCancelTimer(&m_Timer);
    KeFlushQueuedDpcs();

    if (m_Miniport != NULL && m_Miniport->GetAdapterCommon() != NULL) {
        if (m_IsCapture) {
            m_Miniport->GetAdapterCommon()->SetCaptureState(FALSE);
        } else {
            m_Miniport->GetAdapterCommon()->SetRenderState(FALSE);
        }
    }

    if (m_PortStream != NULL) {
        m_PortStream->Release();
        m_PortStream = NULL;
    }
    if (m_Miniport != NULL) {
        m_Miniport->Release();
        m_Miniport = NULL;
    }
}

STDMETHODIMP
CMiniportWaveRTStreamHvAudio::NonDelegatingQueryInterface(
    _In_ REFIID Interface,
    _COM_Outptr_ PVOID* Object
)
{
    if (IsEqualGUIDAligned(Interface, IID_IUnknown)) {
        *Object = reinterpret_cast<PVOID>(static_cast<PUNKNOWN>(this));
    } else if (IsEqualGUIDAligned(Interface, IID_IMiniportWaveRTStream)) {
        *Object = reinterpret_cast<PVOID>(static_cast<PMINIPORTWAVERTSTREAM>(this));
    } else if (IsEqualGUIDAligned(Interface, IID_IMiniportWaveRTStreamNotification)) {
        *Object = reinterpret_cast<PVOID>(static_cast<PMINIPORTWAVERTSTREAMNOTIFICATION>(this));
    } else {
        *Object = NULL;
        return STATUS_INVALID_PARAMETER;
    }

    reinterpret_cast<PUNKNOWN>(*Object)->AddRef();
    return STATUS_SUCCESS;
}

NTSTATUS
CMiniportWaveRTStreamHvAudio::Init(
    _In_ CMiniportWaveRTHvAudio* Miniport,
    _In_ PPORTWAVERTSTREAM       PortStream,
    _In_ ULONG                   PinId,
    _In_ BOOLEAN                 Capture,
    _In_ PKSDATAFORMAT           DataFormat
)
{
    if (Miniport == NULL || PortStream == NULL || DataFormat == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    m_Miniport = Miniport;
    m_Miniport->AddRef();

    m_PortStream = PortStream;
    m_PortStream->AddRef();

    m_PinId = PinId;
    m_IsCapture = Capture;

    return SetFormat(DataFormat);
}

STDMETHODIMP
CMiniportWaveRTStreamHvAudio::SetFormat(
    _In_ PKSDATAFORMAT DataFormat
)
{
    if (DataFormat == NULL || DataFormat->FormatSize < sizeof(KSDATAFORMAT_WAVEFORMATEX)) {
        return STATUS_INVALID_PARAMETER;
    }

    if (!IsEqualGUIDAligned(DataFormat->MajorFormat, KSDATAFORMAT_TYPE_AUDIO) ||
        !IsEqualGUIDAligned(DataFormat->SubFormat, KSDATAFORMAT_SUBTYPE_PCM))
    {
        return STATUS_NOT_SUPPORTED;
    }

    auto* ksWaveFmt = reinterpret_cast<PKSDATAFORMAT_WAVEFORMATEX>(DataFormat);
    PWAVEFORMATEX waveFmt = &ksWaveFmt->WaveFormatEx;

    if (waveFmt->wFormatTag == WAVE_FORMAT_EXTENSIBLE) {
        if (DataFormat->FormatSize < sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE) ||
            waveFmt->cbSize < (sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)))
        {
            return STATUS_INVALID_PARAMETER;
        }
        auto* waveExt = reinterpret_cast<PWAVEFORMATEXTENSIBLE>(waveFmt);
        if (!IsEqualGUIDAligned(waveExt->SubFormat, KSDATAFORMAT_SUBTYPE_PCM)) {
            return STATUS_NOT_SUPPORTED;
        }
        if (waveExt->Samples.wValidBitsPerSample != 0 &&
            waveExt->Samples.wValidBitsPerSample != waveFmt->wBitsPerSample)
        {
            return STATUS_NOT_SUPPORTED;
        }
    } else if (waveFmt->wFormatTag != WAVE_FORMAT_PCM) {
        return STATUS_NOT_SUPPORTED;
    }

    if (waveFmt->nSamplesPerSec != HV_AUDIO_SAMPLE_RATE_44100 &&
        waveFmt->nSamplesPerSec != HV_AUDIO_SAMPLE_RATE_48000)
    {
        return STATUS_NOT_SUPPORTED;
    }
    if (waveFmt->nChannels != HV_AUDIO_CHANNELS_MONO &&
        waveFmt->nChannels != HV_AUDIO_CHANNELS_STEREO)
    {
        return STATUS_NOT_SUPPORTED;
    }
    if (waveFmt->wBitsPerSample != HV_AUDIO_BITS_PER_SAMPLE_16 &&
        waveFmt->wBitsPerSample != HV_AUDIO_BITS_PER_SAMPLE_24)
    {
        return STATUS_NOT_SUPPORTED;
    }
    if (m_IsCapture && waveFmt->wBitsPerSample != HV_AUDIO_BITS_PER_SAMPLE_16) {
        return STATUS_NOT_SUPPORTED;
    }

    USHORT expectedBlockAlign = static_cast<USHORT>(waveFmt->nChannels * (waveFmt->wBitsPerSample / 8));
    if (waveFmt->nBlockAlign != expectedBlockAlign) {
        return STATUS_INVALID_PARAMETER;
    }

    ULONG configuredRate = waveFmt->nSamplesPerSec;

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_StreamLock, &oldIrql);
    m_SampleRate = configuredRate;
    m_Channels = waveFmt->nChannels;
    m_BitsPerSample = waveFmt->wBitsPerSample;
    m_BlockAlign = waveFmt->nBlockAlign;
    m_BytesPerSecond = m_SampleRate * m_BlockAlign;
    m_TotalFramesTransferred = 0;
    KeReleaseSpinLock(&m_StreamLock, oldIrql);

    if (m_Miniport != NULL && m_Miniport->GetAdapterCommon() != NULL) {
        if (m_IsCapture) {
            m_Miniport->GetAdapterCommon()->SetCaptureFormat(configuredRate);
        } else {
            m_Miniport->GetAdapterCommon()->SetRenderFormat(configuredRate);
        }
    }

    return STATUS_SUCCESS;
}

STDMETHODIMP
CMiniportWaveRTStreamHvAudio::AllocateAudioBuffer(
    _In_  ULONG               RequestedSize,
    _Out_ PMDL*               AudioBufferMdl,
    _Out_ ULONG*              ActualSize,
    _Out_ ULONG*              OffsetFromFirstPage,
    _Out_ MEMORY_CACHING_TYPE* CacheType
)
{
    if (RequestedSize == 0 || m_PortStream == NULL || m_BlockAlign == 0 ||
        AudioBufferMdl == NULL || ActualSize == NULL ||
        OffsetFromFirstPage == NULL || CacheType == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }

    // Ensure buffer size is an exact integer multiple of frame BlockAlign so DMA wrap never splits frames
    ULONG alignedSize = (RequestedSize / m_BlockAlign) * m_BlockAlign;
    if (alignedSize == 0) {
        alignedSize = m_BlockAlign;
    }

    ULONG allocSize = ROUND_TO_PAGES(alignedSize);
    PHYSICAL_ADDRESS highAddress;
    highAddress.QuadPart = 0xFFFFFFFFFFFFFFFFULL;

    PMDL mdl = m_PortStream->AllocatePagesForMdl(highAddress, allocSize);
    if (mdl == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    PVOID virtAddr = m_PortStream->MapAllocatedPages(mdl, MmCached);
    if (virtAddr == NULL) {
        m_PortStream->FreePagesFromMdl(mdl);
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    RtlZeroMemory(virtAddr, allocSize);

    m_DmaMdl = mdl;
    m_DmaVirtualAddress = reinterpret_cast<PUCHAR>(virtAddr);
    m_DmaBufferSize = alignedSize;
    m_DmaPosition = 0;

    *AudioBufferMdl = mdl;
    *ActualSize = alignedSize;
    *OffsetFromFirstPage = 0;
    *CacheType = MmCached;

    return STATUS_SUCCESS;
}

STDMETHODIMP_(VOID)
CMiniportWaveRTStreamHvAudio::FreeAudioBuffer(
    _In_opt_ PMDL  AudioBufferMdl,
    _In_     ULONG BufferSize
)
{
    UNREFERENCED_PARAMETER(BufferSize);

    KeCancelTimer(&m_Timer);
    KeFlushQueuedDpcs();

    if (AudioBufferMdl != NULL && m_PortStream != NULL) {
        if (m_DmaVirtualAddress != NULL) {
            m_PortStream->UnmapAllocatedPages(m_DmaVirtualAddress, AudioBufferMdl);
            m_DmaVirtualAddress = NULL;
        }
        m_PortStream->FreePagesFromMdl(AudioBufferMdl);
    }

    m_DmaMdl = NULL;
    m_DmaBufferSize = 0;
    m_DmaPosition = 0;
}

STDMETHODIMP
CMiniportWaveRTStreamHvAudio::AllocateBufferWithNotification(
    _In_  ULONG               NotificationCount,
    _In_  ULONG               RequestedSize,
    _Out_ PMDL*               AudioBufferMdl,
    _Out_ ULONG*              ActualSize,
    _Out_ ULONG*              OffsetFromFirstPage,
    _Out_ MEMORY_CACHING_TYPE* CacheType
)
{
    m_NotificationCount = (NotificationCount > 0) ? NotificationCount : 2;
    return AllocateAudioBuffer(
        RequestedSize,
        AudioBufferMdl,
        ActualSize,
        OffsetFromFirstPage,
        CacheType
    );
}

STDMETHODIMP_(VOID)
CMiniportWaveRTStreamHvAudio::FreeBufferWithNotification(
    _In_ PMDL  AudioBufferMdl,
    _In_ ULONG BufferSize
)
{
    FreeAudioBuffer(AudioBufferMdl, BufferSize);
}

STDMETHODIMP
CMiniportWaveRTStreamHvAudio::RegisterNotificationEvent(
    _In_ PKEVENT NotificationEvent
)
{
    KIRQL oldIrql;
    KeAcquireSpinLock(&m_StreamLock, &oldIrql);
    m_NotificationEvent = NotificationEvent;
    KeReleaseSpinLock(&m_StreamLock, oldIrql);
    return STATUS_SUCCESS;
}

STDMETHODIMP
CMiniportWaveRTStreamHvAudio::UnregisterNotificationEvent(
    _In_ PKEVENT NotificationEvent
)
{
    KIRQL oldIrql;
    KeAcquireSpinLock(&m_StreamLock, &oldIrql);
    if (m_NotificationEvent == NotificationEvent) {
        m_NotificationEvent = NULL;
    }
    KeReleaseSpinLock(&m_StreamLock, oldIrql);
    return STATUS_SUCCESS;
}

STDMETHODIMP_(VOID)
CMiniportWaveRTStreamHvAudio::GetHWLatency(
    _Out_ KSRTAUDIO_HWLATENCY* HwLatency
)
{
    if (HwLatency != NULL) {
        KIRQL oldIrql;
        KeAcquireSpinLock(&m_StreamLock, &oldIrql);
        USHORT align = (m_BlockAlign > 0) ? m_BlockAlign : HV_AUDIO_BLOCK_ALIGN_STEREO_16;
        KeReleaseSpinLock(&m_StreamLock, oldIrql);

        // Must be an exact multiple of m_BlockAlign (for both 16-bit and 24-bit frames)
        HwLatency->FifoSize = static_cast<ULONG>(align) * 32u;
        HwLatency->ChipsetDelay = 0;
        HwLatency->CodecDelay = 0;
    }
}

STDMETHODIMP
CMiniportWaveRTStreamHvAudio::GetPositionRegister(
    _Out_ KSRTAUDIO_HWREGISTER* Register
)
{
    UNREFERENCED_PARAMETER(Register);
    return STATUS_NOT_SUPPORTED;
}

STDMETHODIMP
CMiniportWaveRTStreamHvAudio::GetClockRegister(
    _Out_ KSRTAUDIO_HWREGISTER* Register
)
{
    UNREFERENCED_PARAMETER(Register);
    return STATUS_NOT_SUPPORTED;
}

STDMETHODIMP
CMiniportWaveRTStreamHvAudio::GetPosition(
    _Out_ PKSAUDIO_POSITION Position
)
{
    if (Position == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_StreamLock, &oldIrql);
    Position->PlayOffset = m_DmaPosition;
    if (m_DmaBufferSize > 0) {
        if (m_IsCapture) {
            // Capture hardware writes up to m_DmaPosition; reader must stay behind m_DmaPosition
            Position->WriteOffset = m_DmaPosition;
        } else {
            ULONG align = (m_BlockAlign > 0) ? m_BlockAlign : HV_AUDIO_BLOCK_ALIGN_STEREO_16;
            ULONG fifoBytes = align * 32u;
            if (fifoBytes >= m_DmaBufferSize) {
                fifoBytes = align;
            }
            Position->WriteOffset = (m_DmaPosition + fifoBytes) % m_DmaBufferSize;
        }
    } else {
        Position->WriteOffset = 0;
    }
    KeReleaseSpinLock(&m_StreamLock, oldIrql);

    return STATUS_SUCCESS;
}

// ============================================================================
// SetState - Transitions WaveRT Stream & Notifies Host on KSSTATE_RUN
// ============================================================================
STDMETHODIMP
CMiniportWaveRTStreamHvAudio::SetState(
    _In_ KSSTATE NewState
)
{
    KIRQL oldIrql;
    KeAcquireSpinLock(&m_StreamLock, &oldIrql);
    KSSTATE prevState = m_KsState;
    ULONG configuredRate = m_SampleRate;
    m_KsState = NewState;

    if (NewState == KSSTATE_STOP) {
        m_DmaPosition = 0;
        m_TotalBytesTransferred = 0;
        m_TotalFramesTransferred = 0;
        m_RunStartQpc = 0;
        if (m_DmaVirtualAddress != NULL && m_DmaBufferSize > 0) {
            RtlZeroMemory(m_DmaVirtualAddress, m_DmaBufferSize);
        }
    } else if (NewState == KSSTATE_RUN && prevState != KSSTATE_RUN) {
        LARGE_INTEGER now = KeQueryPerformanceCounter(NULL);
        m_RunStartQpc = static_cast<ULONGLONG>(now.QuadPart);
        m_TotalFramesTransferred = 0;
    }
    KeReleaseSpinLock(&m_StreamLock, oldIrql);

    if (NewState == KSSTATE_RUN && prevState != KSSTATE_RUN) {
        // Notify AdapterCommon so HvGuestBridge & Host learn immediately that
        // the Guest Speakers or Microphone Pin entered KSSTATE_RUN and at which rate!
        if (m_Miniport != NULL && m_Miniport->GetAdapterCommon() != NULL) {
            if (m_IsCapture) {
                m_Miniport->GetAdapterCommon()->SetCaptureFormat(configuredRate);
                m_Miniport->GetAdapterCommon()->SetCaptureState(TRUE);
            } else {
                m_Miniport->GetAdapterCommon()->SetRenderFormat(configuredRate);
                m_Miniport->GetAdapterCommon()->SetRenderState(TRUE);
            }
        }

        // Start 10ms periodic DMA timer
        LARGE_INTEGER dueTime;
        dueTime.QuadPart = -100000LL; // 10 ms in 100-ns relative units
        KeSetTimerEx(&m_Timer, dueTime, HV_AUDIO_PERIOD_MS, &m_TimerDpc);
    }
    else if (NewState != KSSTATE_RUN && prevState == KSSTATE_RUN) {
        KeCancelTimer(&m_Timer);

        if (m_Miniport != NULL && m_Miniport->GetAdapterCommon() != NULL) {
            if (m_IsCapture) {
                m_Miniport->GetAdapterCommon()->SetCaptureState(FALSE);
            } else {
                m_Miniport->GetAdapterCommon()->SetRenderState(FALSE);
            }
        }
    }

    return STATUS_SUCCESS;
}

// ============================================================================
// WaveRTTimerDpcRoutine & ServiceWaveRTDmaTick (QPC-Accurate 44.1 / 48 kHz)
// ============================================================================
VOID
CMiniportWaveRTStreamHvAudio::WaveRTTimerDpcRoutine(
    _In_     PKDPC   Dpc,
    _In_opt_ PVOID   DeferredContext,
    _In_opt_ PVOID   SystemArgument1,
    _In_opt_ PVOID   SystemArgument2
)
{
    UNREFERENCED_PARAMETER(Dpc);
    UNREFERENCED_PARAMETER(SystemArgument1);
    UNREFERENCED_PARAMETER(SystemArgument2);

    auto* stream = reinterpret_cast<CMiniportWaveRTStreamHvAudio*>(DeferredContext);
    if (stream != NULL) {
        stream->ServiceWaveRTDmaTick();
    }
}

VOID
CMiniportWaveRTStreamHvAudio::ServiceWaveRTDmaTick()
{
    static constexpr ULONG kConvertChunkFrames = 256;

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_StreamLock, &oldIrql);

    if (m_KsState != KSSTATE_RUN || m_DmaVirtualAddress == NULL ||
        m_DmaBufferSize == 0 || m_BlockAlign == 0)
    {
        KeReleaseSpinLock(&m_StreamLock, oldIrql);
        return;
    }

    LARGE_INTEGER now = KeQueryPerformanceCounter(NULL);
    ULONGLONG currentQpc = static_cast<ULONGLONG>(now.QuadPart);
    ULONGLONG qpcFreq = (m_QpcFrequency.QuadPart > 0) ? static_cast<ULONGLONG>(m_QpcFrequency.QuadPart) : 0ULL;

    ULONG maxFramesPerTick = m_DmaBufferSize / (2u * m_BlockAlign);
    if (maxFramesPerTick == 0) {
        maxFramesPerTick = m_DmaBufferSize / m_BlockAlign;
    }

    ULONG framesToAdvance = HvAudioPeriodFramesForRate(m_SampleRate);
    if (qpcFreq > 0 && currentQpc > m_RunStartQpc) {
        ULONGLONG elapsedQpc = currentQpc - m_RunStartQpc;
        ULONGLONG wholeSeconds = elapsedQpc / qpcFreq;
        ULONGLONG remTicks = elapsedQpc % qpcFreq;
        ULONGLONG targetTotalFrames = (wholeSeconds * m_SampleRate) + ((remTicks * m_SampleRate) / qpcFreq);

        if (targetTotalFrames <= m_TotalFramesTransferred) {
            KeReleaseSpinLock(&m_StreamLock, oldIrql);
            return;
        }

        ULONGLONG deltaFrames = targetTotalFrames - m_TotalFramesTransferred;
        if (deltaFrames > maxFramesPerTick) {
            // Clamp after VM pause/resume and rebase QPC origin to avoid chasing stale debt
            framesToAdvance = min(HvAudioPeriodFramesForRate(m_SampleRate), maxFramesPerTick);
            m_RunStartQpc = currentQpc;
            m_TotalFramesTransferred = 0;
        } else {
            framesToAdvance = static_cast<ULONG>(deltaFrames);
            m_TotalFramesTransferred = targetTotalFrames;
        }
    } else if (framesToAdvance > maxFramesPerTick) {
        framesToAdvance = maxFramesPerTick;
    }

    if (framesToAdvance == 0) {
        KeReleaseSpinLock(&m_StreamLock, oldIrql);
        return;
    }

    ULONG bytesToAdvance = framesToAdvance * m_BlockAlign;
    if (bytesToAdvance > m_DmaBufferSize) {
        bytesToAdvance = (m_DmaBufferSize / m_BlockAlign) * m_BlockAlign;
        framesToAdvance = bytesToAdvance / m_BlockAlign;
    }

    PHVAUDIOADAPTERCOMMON adapter = (m_Miniport != NULL) ? m_Miniport->GetAdapterCommon() : NULL;
    ULONG startPos = m_DmaPosition;
    ULONG firstPart = min(bytesToAdvance, m_DmaBufferSize - startPos);
    ULONG secondPart = bytesToAdvance - firstPart;

    if (!m_IsCapture) {
        // RENDER ("Hyper-V Console Speakers"): Convert/Copy from WaveRT DMA into Stereo 16-bit Ring Buffer
        BOOL muted = (adapter != NULL) ? adapter->GetMuteState(HvAudioDeviceType_Render) : FALSE;
        if (adapter != NULL && !muted) {
            if (m_BitsPerSample == 16 && m_Channels == 2) {
                adapter->PushRenderPcmFromWaveRT(m_DmaVirtualAddress + startPos, firstPart);
                if (secondPart > 0) {
                    adapter->PushRenderPcmFromWaveRT(m_DmaVirtualAddress, secondPart);
                }
            } else if (m_BitsPerSample == 16 && m_Channels == 1) {
                UCHAR convBuf[kConvertChunkFrames * HV_AUDIO_BLOCK_ALIGN_STEREO_16];
                ULONG remaining = framesToAdvance;
                ULONG readPos = startPos;
                while (remaining > 0) {
                    ULONG chunk = min(remaining, kConvertChunkFrames);
                    for (ULONG f = 0; f < chunk; ++f) {
                        UCHAR b0 = m_DmaVirtualAddress[readPos];
                        UCHAR b1 = m_DmaVirtualAddress[(readPos + 1) % m_DmaBufferSize];
                        convBuf[(f * 4) + 0] = b0;
                        convBuf[(f * 4) + 1] = b1;
                        convBuf[(f * 4) + 2] = b0;
                        convBuf[(f * 4) + 3] = b1;
                        readPos = (readPos + 2) % m_DmaBufferSize;
                    }
                    adapter->PushRenderPcmFromWaveRT(convBuf, chunk * HV_AUDIO_BLOCK_ALIGN_STEREO_16);
                    remaining -= chunk;
                }
            } else if (m_BitsPerSample == 24 && m_Channels == 2) {
                UCHAR convBuf[kConvertChunkFrames * HV_AUDIO_BLOCK_ALIGN_STEREO_16];
                ULONG remaining = framesToAdvance;
                ULONG readPos = startPos;
                while (remaining > 0) {
                    ULONG chunk = min(remaining, kConvertChunkFrames);
                    for (ULONG f = 0; f < chunk; ++f) {
                        for (ULONG ch = 0; ch < 2; ++ch) {
                            UCHAR b1 = m_DmaVirtualAddress[(readPos + 1) % m_DmaBufferSize];
                            UCHAR b2 = m_DmaVirtualAddress[(readPos + 2) % m_DmaBufferSize];
                            convBuf[(f * 4) + (ch * 2)]     = b1;
                            convBuf[(f * 4) + (ch * 2) + 1] = b2;
                            readPos = (readPos + 3) % m_DmaBufferSize;
                        }
                    }
                    adapter->PushRenderPcmFromWaveRT(convBuf, chunk * HV_AUDIO_BLOCK_ALIGN_STEREO_16);
                    remaining -= chunk;
                }
            } else if (m_BitsPerSample == 24 && m_Channels == 1) {
                UCHAR convBuf[kConvertChunkFrames * HV_AUDIO_BLOCK_ALIGN_STEREO_16];
                ULONG remaining = framesToAdvance;
                ULONG readPos = startPos;
                while (remaining > 0) {
                    ULONG chunk = min(remaining, kConvertChunkFrames);
                    for (ULONG f = 0; f < chunk; ++f) {
                        UCHAR b1 = m_DmaVirtualAddress[(readPos + 1) % m_DmaBufferSize];
                        UCHAR b2 = m_DmaVirtualAddress[(readPos + 2) % m_DmaBufferSize];
                        convBuf[(f * 4) + 0] = b1;
                        convBuf[(f * 4) + 1] = b2;
                        convBuf[(f * 4) + 2] = b1;
                        convBuf[(f * 4) + 3] = b2;
                        readPos = (readPos + 3) % m_DmaBufferSize;
                    }
                    adapter->PushRenderPcmFromWaveRT(convBuf, chunk * HV_AUDIO_BLOCK_ALIGN_STEREO_16);
                    remaining -= chunk;
                }
            }
        }
    } else {
        // CAPTURE ("Hyper-V Console Microphone"): Pull Stereo 16-bit PCM from Ring Buffer into WaveRT DMA
        // If Host Mic is OFF (privacy default) or buffer is empty, fill with zeroed silence.
        BOOL muted = (adapter != NULL) ? adapter->GetMuteState(HvAudioDeviceType_Capture) : FALSE;
        if (adapter == NULL || muted) {
            RtlZeroMemory(m_DmaVirtualAddress + startPos, firstPart);
            if (secondPart > 0) {
                RtlZeroMemory(m_DmaVirtualAddress, secondPart);
            }
        } else if (m_BitsPerSample == 16 && m_Channels == 2) {
            ULONG readFirst = adapter->PullCapturePcmForWaveRT(m_DmaVirtualAddress + startPos, firstPart);
            if (readFirst < firstPart) {
                RtlZeroMemory(m_DmaVirtualAddress + startPos + readFirst, firstPart - readFirst);
            }
            if (secondPart > 0) {
                ULONG readSecond = adapter->PullCapturePcmForWaveRT(m_DmaVirtualAddress, secondPart);
                if (readSecond < secondPart) {
                    RtlZeroMemory(m_DmaVirtualAddress + readSecond, secondPart - readSecond);
                }
            }
        } else if (m_BitsPerSample == 16 && m_Channels == 1) {
            UCHAR stereoBuf[kConvertChunkFrames * HV_AUDIO_BLOCK_ALIGN_STEREO_16];
            ULONG remaining = framesToAdvance;
            ULONG writePos = startPos;
            while (remaining > 0) {
                ULONG chunk = min(remaining, kConvertChunkFrames);
                ULONG pulledBytes = adapter->PullCapturePcmForWaveRT(
                    stereoBuf,
                    chunk * HV_AUDIO_BLOCK_ALIGN_STEREO_16
                );
                ULONG pulledFrames = pulledBytes / HV_AUDIO_BLOCK_ALIGN_STEREO_16;
                const SHORT* stereoSamples = reinterpret_cast<const SHORT*>(stereoBuf);

                for (ULONG f = 0; f < chunk; ++f) {
                    SHORT monoSample = 0;
                    if (f < pulledFrames) {
                        LONG l = stereoSamples[f * 2];
                        LONG r = stereoSamples[(f * 2) + 1];
                        monoSample = static_cast<SHORT>((l + r) / 2);
                    }
                    m_DmaVirtualAddress[writePos] = static_cast<UCHAR>(monoSample & 0xFF);
                    m_DmaVirtualAddress[(writePos + 1) % m_DmaBufferSize] =
                        static_cast<UCHAR>((static_cast<USHORT>(monoSample) >> 8) & 0xFF);
                    writePos = (writePos + 2) % m_DmaBufferSize;
                }
                remaining -= chunk;
            }
        } else {
            RtlZeroMemory(m_DmaVirtualAddress + startPos, firstPart);
            if (secondPart > 0) {
                RtlZeroMemory(m_DmaVirtualAddress, secondPart);
            }
        }
    }

    m_DmaPosition = (startPos + bytesToAdvance) % m_DmaBufferSize;
    m_TotalBytesTransferred += bytesToAdvance;
    if (m_NotificationEvent != NULL) {
        KeSetEvent(m_NotificationEvent, IO_SOUND_INCREMENT, FALSE);
    }

    KeReleaseSpinLock(&m_StreamLock, oldIrql);
}
