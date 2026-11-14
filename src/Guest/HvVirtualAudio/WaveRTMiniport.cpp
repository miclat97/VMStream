// ============================================================================
// WaveRTMiniport.cpp - PortCls WaveRT Miniport & Stream Implementation
// Project: VMStream - Guest Virtual Audio Driver (HvVirtualAudio.sys)
// ============================================================================
#include "WaveRTMiniport.h"

// ============================================================================
// Supported PCM Data Ranges (48 kHz, 16-bit/24-bit Stereo Render; 48 kHz 16-bit Mic)
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
        HV_AUDIO_SAMPLE_RATE,
        HV_AUDIO_SAMPLE_RATE
    },
    // 48 kHz, 24-bit (in 24-bit or 32-bit container), 2-channel PCM
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
        HV_AUDIO_SAMPLE_RATE,
        HV_AUDIO_SAMPLE_RATE
    }
};

static PKSDATARANGE g_RenderPcmDataRangePointers[] = {
    reinterpret_cast<PKSDATARANGE>(&g_RenderPcmDataRanges[0]),
    reinterpret_cast<PKSDATARANGE>(&g_RenderPcmDataRanges[1])
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
        HV_AUDIO_SAMPLE_RATE,
        HV_AUDIO_SAMPLE_RATE
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
        HV_AUDIO_SAMPLE_RATE,
        HV_AUDIO_SAMPLE_RATE
    }
};

static PKSDATARANGE g_CapturePcmDataRangePointers[] = {
    reinterpret_cast<PKSDATARANGE>(&g_CapturePcmDataRanges[0]),
    reinterpret_cast<PKSDATARANGE>(&g_CapturePcmDataRanges[1])
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
    sizeof(PCCONNECTION_DESCRIPTOR),
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
    sizeof(PCCONNECTION_DESCRIPTOR),
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
    UNREFERENCED_PARAMETER(PinId);

    if (!IsEqualGUIDAligned(ClientDataRange->FormatType, KSDATAFORMAT_TYPE_AUDIO) &&
        !IsEqualGUIDAligned(ClientDataRange->FormatType, KSDATAFORMAT_TYPE_WILDCARD))
    {
        return STATUS_NO_MATCH;
    }

    auto* myAudioRange = reinterpret_cast<PKSDATARANGE_AUDIO>(MyDataRange);
    ULONG requiredSize = sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE);

    if (OutputBufferLength == 0) {
        *ResultantFormatLength = requiredSize;
        return STATUS_BUFFER_OVERFLOW;
    }

    if (OutputBufferLength < requiredSize) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    auto* waveFormatEx = reinterpret_cast<PKSDATAFORMAT_WAVEFORMATEXTENSIBLE>(ResultantFormat);
    RtlZeroMemory(waveFormatEx, requiredSize);

    USHORT channels = static_cast<USHORT>(myAudioRange->MaximumChannels);
    USHORT bitsPerSample = static_cast<USHORT>(myAudioRange->MaximumBitsPerSample);
    USHORT containerBytes = (bitsPerSample == 24) ? 3 : 2;
    USHORT blockAlign = channels * containerBytes;

    waveFormatEx->DataFormat.FormatSize = requiredSize;
    waveFormatEx->DataFormat.Flags = 0;
    waveFormatEx->DataFormat.SampleSize = blockAlign;
    waveFormatEx->DataFormat.Reserved = 0;
    waveFormatEx->DataFormat.MajorFormat = KSDATAFORMAT_TYPE_AUDIO;
    waveFormatEx->DataFormat.SubFormat = KSDATAFORMAT_SUBTYPE_PCM;
    waveFormatEx->DataFormat.Specifier = KSDATAFORMAT_SPECIFIER_WAVEFORMATEX;

    waveFormatEx->WaveFormatExt.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    waveFormatEx->WaveFormatExt.Format.nChannels = channels;
    waveFormatEx->WaveFormatExt.Format.nSamplesPerSec = HV_AUDIO_SAMPLE_RATE;
    waveFormatEx->WaveFormatExt.Format.wBitsPerSample = bitsPerSample;
    waveFormatEx->WaveFormatExt.Format.nBlockAlign = blockAlign;
    waveFormatEx->WaveFormatExt.Format.nAvgBytesPerSec = HV_AUDIO_SAMPLE_RATE * blockAlign;
    waveFormatEx->WaveFormatExt.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
    waveFormatEx->WaveFormatExt.Samples.wValidBitsPerSample = bitsPerSample;
    waveFormatEx->WaveFormatExt.dwChannelMask = (channels == 1)
        ? SPEAKER_FRONT_CENTER
        : (SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT);
    waveFormatEx->WaveFormatExt.SubFormat = KSDATAFORMAT_SUBTYPE_PCM;

    *ResultantFormatLength = requiredSize;
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
    m_TotalBytesTransferred(0),
    m_LastQpcTimestamp(0),
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

    auto* waveFmt = reinterpret_cast<PWAVEFORMATEX>(DataFormat + 1);
    if (waveFmt->nSamplesPerSec != HV_AUDIO_SAMPLE_RATE) {
        return STATUS_NOT_SUPPORTED;
    }
    if (waveFmt->nChannels != 1 && waveFmt->nChannels != 2) {
        return STATUS_NOT_SUPPORTED;
    }
    if (waveFmt->wBitsPerSample != 16 && waveFmt->wBitsPerSample != 24) {
        return STATUS_NOT_SUPPORTED;
    }

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_StreamLock, &oldIrql);
    m_SampleRate = waveFmt->nSamplesPerSec;
    m_Channels = waveFmt->nChannels;
    m_BitsPerSample = waveFmt->wBitsPerSample;
    m_BlockAlign = waveFmt->nBlockAlign;
    m_BytesPerSecond = m_SampleRate * m_BlockAlign;
    KeReleaseSpinLock(&m_StreamLock, oldIrql);

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
    if (RequestedSize == 0 || m_PortStream == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    // Align size to page boundary & block alignment
    ULONG allocSize = ROUND_TO_PAGES(RequestedSize);
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
    m_DmaBufferSize = RequestedSize;
    m_DmaPosition = 0;

    *AudioBufferMdl = mdl;
    *ActualSize = RequestedSize;
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
    UNREFERENCED_PARAMETER(NotificationCount);
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
        HwLatency->FifoSize = 64;
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
    Position->WriteOffset = (m_DmaBufferSize > 0)
        ? ((m_DmaPosition + (m_BlockAlign * 48)) % m_DmaBufferSize)
        : 0;
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
    m_KsState = NewState;

    if (NewState == KSSTATE_STOP) {
        m_DmaPosition = 0;
        m_TotalBytesTransferred = 0;
        if (m_DmaVirtualAddress != NULL && m_DmaBufferSize > 0) {
            RtlZeroMemory(m_DmaVirtualAddress, m_DmaBufferSize);
        }
    } else if (NewState == KSSTATE_RUN && prevState != KSSTATE_RUN) {
        LARGE_INTEGER now = KeQueryPerformanceCounter(NULL);
        m_LastQpcTimestamp = static_cast<ULONGLONG>(now.QuadPart);
    }
    KeReleaseSpinLock(&m_StreamLock, oldIrql);

    if (NewState == KSSTATE_RUN && prevState != KSSTATE_RUN) {
        // Notify AdapterCommon so HvGuestBridge & Host learn immediately that
        // the Guest Speakers or Microphone Pin entered KSSTATE_RUN!
        if (m_Miniport != NULL && m_Miniport->GetAdapterCommon() != NULL) {
            if (m_IsCapture) {
                m_Miniport->GetAdapterCommon()->SetCaptureState(TRUE);
            } else {
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
// WaveRTTimerDpcRoutine & ServiceWaveRTDmaTick (10ms Period @ 48 kHz)
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
    KIRQL oldIrql;
    KeAcquireSpinLock(&m_StreamLock, &oldIrql);

    if (m_KsState != KSSTATE_RUN || m_DmaVirtualAddress == NULL || m_DmaBufferSize == 0) {
        KeReleaseSpinLock(&m_StreamLock, oldIrql);
        return;
    }

    LARGE_INTEGER now = KeQueryPerformanceCounter(NULL);
    ULONGLONG currentQpc = static_cast<ULONGLONG>(now.QuadPart);
    ULONGLONG elapsedTicks = (currentQpc > m_LastQpcTimestamp) ? (currentQpc - m_LastQpcTimestamp) : 0;
    m_LastQpcTimestamp = currentQpc;

    ULONG bytesToAdvance = HV_AUDIO_FRAMES_PER_PERIOD * m_BlockAlign;
    if (m_QpcFrequency.QuadPart > 0 && elapsedTicks > 0) {
        ULONGLONG exactBytes = (elapsedTicks * m_BytesPerSecond) / static_cast<ULONGLONG>(m_QpcFrequency.QuadPart);
        exactBytes = (exactBytes / m_BlockAlign) * m_BlockAlign;
        if (exactBytes > 0 && exactBytes <= m_DmaBufferSize / 2) {
            bytesToAdvance = static_cast<ULONG>(exactBytes);
        }
    }

    PHVAUDIOADAPTERCOMMON adapter = (m_Miniport != NULL) ? m_Miniport->GetAdapterCommon() : NULL;
    ULONG startPos = m_DmaPosition;
    ULONG firstPart = min(bytesToAdvance, m_DmaBufferSize - startPos);
    ULONG secondPart = bytesToAdvance - firstPart;

    if (!m_IsCapture) {
        // RENDER ("Hyper-V Console Speakers"): Copy from WaveRT DMA buffer into Render Ring Buffer
        BOOL muted = (adapter != NULL) ? adapter->GetMuteState(HvAudioDeviceType_Render) : FALSE;
        if (adapter != NULL && !muted) {
            if (m_BitsPerSample == 16 && m_Channels == 2) {
                adapter->PushRenderPcmFromWaveRT(m_DmaVirtualAddress + startPos, firstPart);
                if (secondPart > 0) {
                    adapter->PushRenderPcmFromWaveRT(m_DmaVirtualAddress, secondPart);
                }
            } else if (m_BitsPerSample == 24 && m_Channels == 2) {
                // Downconvert 24-bit packed stereo (3 bytes/sample) to 16-bit stereo on the fly
                UCHAR convBuf[HV_AUDIO_PERIOD_BYTES_STEREO_16];
                ULONG frames = min(bytesToAdvance / 6, static_cast<ULONG>(HV_AUDIO_FRAMES_PER_PERIOD));
                ULONG readPos = startPos;
                for (ULONG f = 0; f < frames; ++f) {
                    for (ULONG ch = 0; ch < 2; ++ch) {
                        UCHAR b1 = m_DmaVirtualAddress[(readPos + 1) % m_DmaBufferSize];
                        UCHAR b2 = m_DmaVirtualAddress[(readPos + 2) % m_DmaBufferSize];
                        convBuf[(f * 4) + (ch * 2)]     = b1;
                        convBuf[(f * 4) + (ch * 2) + 1] = b2;
                        readPos = (readPos + 3) % m_DmaBufferSize;
                    }
                }
                adapter->PushRenderPcmFromWaveRT(convBuf, frames * 4);
            }
        }
    } else {
        // CAPTURE ("Hyper-V Console Microphone"): Pull PCM from Capture Ring Buffer into WaveRT DMA
        // If Host Mic is OFF (privacy default) or buffer is empty, fill with zeroed silence!
        BOOL muted = (adapter != NULL) ? adapter->GetMuteState(HvAudioDeviceType_Capture) : FALSE;
        if (adapter == NULL || muted) {
            RtlZeroMemory(m_DmaVirtualAddress + startPos, firstPart);
            if (secondPart > 0) {
                RtlZeroMemory(m_DmaVirtualAddress, secondPart);
            }
        } else {
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
        }
    }

    m_DmaPosition = (startPos + bytesToAdvance) % m_DmaBufferSize;
    m_TotalBytesTransferred += bytesToAdvance;
    PKEVENT notifyEvt = m_NotificationEvent;

    KeReleaseSpinLock(&m_StreamLock, oldIrql);

    if (notifyEvt != NULL) {
        KeSetEvent(notifyEvt, IO_SOUND_INCREMENT, FALSE);
    }
}
