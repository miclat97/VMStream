// ============================================================================
// Adapter.cpp - PortCls Adapter Driver, Subdevice Registration & IOCTL Bridge
// Project: VMStream - Guest Virtual Audio Driver (HvVirtualAudio.sys)
// ============================================================================
#define PUT_GUIDS_HERE
#include "AudioCommon.h"
#include "TopologyMiniport.h"
#include "WaveRTMiniport.h"
#include <wdmsec.h>

class CAdapterCommon;

static PDEVICE_OBJECT   g_AudioControlDeviceObject = NULL;
static CAdapterCommon*  g_ActiveAdapterCommon      = NULL;
static PDRIVER_DISPATCH g_PortClsCreateDispatch    = NULL;
static PDRIVER_DISPATCH g_PortClsCloseDispatch     = NULL;
static PDRIVER_DISPATCH g_PortClsCleanupDispatch   = NULL;
static PDRIVER_DISPATCH g_PortClsIoctlDispatch     = NULL;
static PDRIVER_UNLOAD   g_PortClsUnload            = NULL;

// ============================================================================
// CAdapterCommon - Implements IHvAudioAdapterCommon & Cancel-Safe Inverted Call
// ============================================================================
class CAdapterCommon :
    public IHvAudioAdapterCommon,
    public IAdapterPowerManagement,
    public CUnknown
{
public:
    DECLARE_STD_UNKNOWN();

    CAdapterCommon(_In_opt_ PUNKNOWN UnknownOuter)
        : CUnknown(UnknownOuter),
          m_DeviceObject(NULL),
          m_PowerState(PowerDeviceD0),
          m_RenderRunning(FALSE),
          m_CaptureRunning(FALSE),
          m_RenderSampleRate(HV_AUDIO_SAMPLE_RATE),
          m_CaptureSampleRate(HV_AUDIO_SAMPLE_RATE),
          m_SequenceGeneration(1)
    {
        m_VolumeLevel[0] = 0; // 0 dB
        m_VolumeLevel[1] = 0; // 0 dB
        m_MuteState[0] = FALSE;
        m_MuteState[1] = FALSE;
        KeInitializeSpinLock(&m_StateLock);
        InitializeListHead(&m_PendingIrpList);
    }

    virtual ~CAdapterCommon() {
        Cleanup();
    }

    // IHvAudioAdapterCommon implementation
    STDMETHODIMP_(NTSTATUS) Init(_In_ PDEVICE_OBJECT DeviceObject) override {
        m_DeviceObject = DeviceObject;
        m_RenderRing.Initialize();
        m_CaptureRing.Initialize();

        NTSTATUS status = IoCsqInitialize(
            &m_IrpCsq,
            CsqInsertIrp,
            CsqRemoveIrp,
            CsqPeekNextIrp,
            CsqAcquireLock,
            CsqReleaseLock,
            CsqCompleteCanceledIrp
        );
        if (NT_SUCCESS(status)) {
            g_ActiveAdapterCommon = this;
        }
        return status;
    }

    STDMETHODIMP_(void) Cleanup() override {
        if (g_ActiveAdapterCommon == this) {
            g_ActiveAdapterCommon = NULL;
        }
        CompleteAllPendingStateIrps(STATUS_CANCELLED);
    }

    STDMETHODIMP_(void) SetRenderState(_In_ BOOLEAN IsRunning) override {
        KIRQL oldIrql;
        KeAcquireSpinLock(&m_StateLock, &oldIrql);
        BOOLEAN changed = (m_RenderRunning != IsRunning);
        m_RenderRunning = IsRunning;
        if (!IsRunning) {
            m_RenderRing.Reset();
        }
        if (changed) {
            m_SequenceGeneration++;
        }
        KeReleaseSpinLock(&m_StateLock, oldIrql);

        if (changed) {
            CompleteAllPendingStateIrps(STATUS_SUCCESS);
        }
    }

    STDMETHODIMP_(void) SetCaptureState(_In_ BOOLEAN IsRunning) override {
        KIRQL oldIrql;
        KeAcquireSpinLock(&m_StateLock, &oldIrql);
        BOOLEAN changed = (m_CaptureRunning != IsRunning);
        m_CaptureRunning = IsRunning;
        if (!IsRunning) {
            m_CaptureRing.Reset();
        }
        if (changed) {
            m_SequenceGeneration++;
        }
        KeReleaseSpinLock(&m_StateLock, oldIrql);

        if (changed) {
            CompleteAllPendingStateIrps(STATUS_SUCCESS);
        }
    }

    STDMETHODIMP_(void) SetRenderFormat(_In_ ULONG SampleRate) override {
        if (SampleRate != HV_AUDIO_SAMPLE_RATE_44100 && SampleRate != HV_AUDIO_SAMPLE_RATE_48000) {
            return;
        }

        KIRQL oldIrql;
        KeAcquireSpinLock(&m_StateLock, &oldIrql);
        BOOLEAN changed = (m_RenderSampleRate != SampleRate);
        m_RenderSampleRate = SampleRate;
        if (changed) {
            m_RenderRing.Reset();
            m_SequenceGeneration++;
        }
        KeReleaseSpinLock(&m_StateLock, oldIrql);

        if (changed) {
            CompleteAllPendingStateIrps(STATUS_SUCCESS);
        }
    }

    STDMETHODIMP_(void) SetCaptureFormat(_In_ ULONG SampleRate) override {
        if (SampleRate != HV_AUDIO_SAMPLE_RATE_44100 && SampleRate != HV_AUDIO_SAMPLE_RATE_48000) {
            return;
        }

        KIRQL oldIrql;
        KeAcquireSpinLock(&m_StateLock, &oldIrql);
        BOOLEAN changed = (m_CaptureSampleRate != SampleRate);
        m_CaptureSampleRate = SampleRate;
        if (changed) {
            m_CaptureRing.Reset();
            m_SequenceGeneration++;
        }
        KeReleaseSpinLock(&m_StateLock, oldIrql);

        if (changed) {
            CompleteAllPendingStateIrps(STATUS_SUCCESS);
        }
    }

    STDMETHODIMP_(BOOLEAN) IsRenderRunning() override {
        return m_RenderRunning;
    }

    STDMETHODIMP_(BOOLEAN) IsCaptureRunning() override {
        return m_CaptureRunning;
    }

    STDMETHODIMP_(ULONG) GetRenderSampleRate() override {
        KIRQL oldIrql;
        KeAcquireSpinLock(&m_StateLock, &oldIrql);
        ULONG rate = m_RenderSampleRate;
        KeReleaseSpinLock(&m_StateLock, oldIrql);
        return rate;
    }

    STDMETHODIMP_(ULONG) GetCaptureSampleRate() override {
        KIRQL oldIrql;
        KeAcquireSpinLock(&m_StateLock, &oldIrql);
        ULONG rate = m_CaptureSampleRate;
        KeReleaseSpinLock(&m_StateLock, oldIrql);
        return rate;
    }

    STDMETHODIMP_(ULONG) PushRenderPcmFromWaveRT(
        _In_reads_bytes_(Length) const UCHAR* Buffer,
        _In_ ULONG Length
    ) override {
        return m_RenderRing.Write(Buffer, Length);
    }

    STDMETHODIMP_(ULONG) PullRenderPcmForBridge(
        _Out_writes_bytes_(Length) UCHAR* Buffer,
        _In_ ULONG Length
    ) override {
        return m_RenderRing.Read(Buffer, Length);
    }

    STDMETHODIMP_(ULONG) PushCapturePcmFromBridge(
        _In_reads_bytes_(Length) const UCHAR* Buffer,
        _In_ ULONG Length
    ) override {
        return m_CaptureRing.Write(Buffer, Length);
    }

    STDMETHODIMP_(ULONG) PullCapturePcmForWaveRT(
        _Out_writes_bytes_(Length) UCHAR* Buffer,
        _In_ ULONG Length
    ) override {
        return m_CaptureRing.Read(Buffer, Length);
    }

    STDMETHODIMP_(void) GetControlState(
        _Out_ PHV_AUDIO_CONTROL_PAYLOAD OutState
    ) override {
        if (OutState == NULL) {
            return;
        }
        KIRQL oldIrql;
        KeAcquireSpinLock(&m_StateLock, &oldIrql);
        OutState->GuestCaptureActive = m_CaptureRunning ? 1 : 0;
        OutState->GuestRenderActive  = m_RenderRunning ? 1 : 0;
        OutState->HostMicEnabled     = 0;
        OutState->SampleRateConfig   = HvPackAudioRateConfig(m_RenderSampleRate, m_CaptureSampleRate);
        OutState->SequenceGeneration = m_SequenceGeneration;
        KeReleaseSpinLock(&m_StateLock, oldIrql);
    }

    STDMETHODIMP_(LONG) GetVolumeLevel(_In_ HV_AUDIO_DEVICE_TYPE Type) override {
        return m_VolumeLevel[(Type == HvAudioDeviceType_Capture) ? 1 : 0];
    }

    STDMETHODIMP_(void) SetVolumeLevel(_In_ HV_AUDIO_DEVICE_TYPE Type, _In_ LONG LevelDb) override {
        m_VolumeLevel[(Type == HvAudioDeviceType_Capture) ? 1 : 0] = LevelDb;
    }

    STDMETHODIMP_(BOOL) GetMuteState(_In_ HV_AUDIO_DEVICE_TYPE Type) override {
        return m_MuteState[(Type == HvAudioDeviceType_Capture) ? 1 : 0];
    }

    STDMETHODIMP_(void) SetMuteState(_In_ HV_AUDIO_DEVICE_TYPE Type, _In_ BOOL Muted) override {
        m_MuteState[(Type == HvAudioDeviceType_Capture) ? 1 : 0] = Muted;
    }

    // IAdapterPowerManagement implementation
    STDMETHODIMP_(void) PowerChangeState(_In_ POWER_STATE NewState) override {
        m_PowerState = NewState.DeviceState;
    }

    STDMETHODIMP_(NTSTATUS) QueryPowerChangeState(_In_ POWER_STATE NewStateQuery) override {
        UNREFERENCED_PARAMETER(NewStateQuery);
        return STATUS_SUCCESS;
    }

    STDMETHODIMP_(NTSTATUS) QueryDeviceCapabilities(_In_ PDEVICE_CAPABILITIES PowerDeviceCaps) override {
        UNREFERENCED_PARAMETER(PowerDeviceCaps);
        return STATUS_SUCCESS;
    }

    // Inverted-Call Handler for IOCTL_HVAUDIO_WAIT_STATE_CHANGE
    NTSTATUS HandleWaitStateChangeIrp(_Inout_ PIRP Irp) {
        PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);
        ULONG outLen = stack->Parameters.DeviceIoControl.OutputBufferLength;
        ULONG inLen  = stack->Parameters.DeviceIoControl.InputBufferLength;

        if (outLen < sizeof(HV_AUDIO_CONTROL_PAYLOAD)) {
            Irp->IoStatus.Information = 0;
            Irp->IoStatus.Status = STATUS_BUFFER_TOO_SMALL;
            IoCompleteRequest(Irp, IO_NO_INCREMENT);
            return STATUS_BUFFER_TOO_SMALL;
        }

        uint32_t callerKnownGeneration = 0;
        if (inLen >= sizeof(HV_AUDIO_CONTROL_PAYLOAD) && Irp->AssociatedIrp.SystemBuffer != NULL) {
            auto* inPayload = reinterpret_cast<PHV_AUDIO_CONTROL_PAYLOAD>(Irp->AssociatedIrp.SystemBuffer);
            callerKnownGeneration = inPayload->SequenceGeneration;
        }

        KIRQL oldIrql;
        KeAcquireSpinLock(&m_StateLock, &oldIrql);
        BOOLEAN alreadyChanged = (callerKnownGeneration != 0 && callerKnownGeneration != m_SequenceGeneration);
        if (alreadyChanged) {
            auto* outPayload = reinterpret_cast<PHV_AUDIO_CONTROL_PAYLOAD>(Irp->AssociatedIrp.SystemBuffer);
            outPayload->GuestCaptureActive = m_CaptureRunning ? 1 : 0;
            outPayload->GuestRenderActive  = m_RenderRunning ? 1 : 0;
            outPayload->HostMicEnabled     = 0;
            outPayload->SampleRateConfig   = HvPackAudioRateConfig(m_RenderSampleRate, m_CaptureSampleRate);
            outPayload->SequenceGeneration = m_SequenceGeneration;
            KeReleaseSpinLock(&m_StateLock, oldIrql);

            Irp->IoStatus.Information = sizeof(HV_AUDIO_CONTROL_PAYLOAD);
            Irp->IoStatus.Status = STATUS_SUCCESS;
            IoCompleteRequest(Irp, IO_NO_INCREMENT);
            return STATUS_SUCCESS;
        }
        KeReleaseSpinLock(&m_StateLock, oldIrql);

        // Pend IRP in cancel-safe queue until KSSTATE_RUN transitions
        IoCsqInsertIrp(&m_IrpCsq, Irp, NULL);
        return STATUS_PENDING;
    }

    void CompleteAllPendingStateIrps(_In_ NTSTATUS CompletionStatus) {
        for (;;) {
            PIRP pendingIrp = IoCsqRemoveNextIrp(&m_IrpCsq, NULL);
            if (pendingIrp == NULL) {
                break;
            }

            if (NT_SUCCESS(CompletionStatus) && pendingIrp->AssociatedIrp.SystemBuffer != NULL) {
                GetControlState(reinterpret_cast<PHV_AUDIO_CONTROL_PAYLOAD>(pendingIrp->AssociatedIrp.SystemBuffer));
                pendingIrp->IoStatus.Information = sizeof(HV_AUDIO_CONTROL_PAYLOAD);
            } else {
                pendingIrp->IoStatus.Information = 0;
            }

            pendingIrp->IoStatus.Status = CompletionStatus;
            IoCompleteRequest(pendingIrp, IO_SOUND_INCREMENT);
        }
    }

private:
    // Cancel-Safe IRP Queue (IO_CSQ) callbacks
    static VOID CsqInsertIrp(_In_ PIO_CSQ Csq, _In_ PIRP Irp) {
        CAdapterCommon* self = CONTAINING_RECORD(Csq, CAdapterCommon, m_IrpCsq);
        InsertTailList(&self->m_PendingIrpList, &Irp->Tail.Overlay.ListEntry);
    }

    static VOID CsqRemoveIrp(_In_ PIO_CSQ Csq, _In_ PIRP Irp) {
        UNREFERENCED_PARAMETER(Csq);
        RemoveEntryList(&Irp->Tail.Overlay.ListEntry);
    }

    static PIRP CsqPeekNextIrp(_In_ PIO_CSQ Csq, _In_opt_ PIRP Irp, _In_opt_ PVOID PeekContext) {
        UNREFERENCED_PARAMETER(PeekContext);
        CAdapterCommon* self = CONTAINING_RECORD(Csq, CAdapterCommon, m_IrpCsq);
        PLIST_ENTRY head = &self->m_PendingIrpList;
        PLIST_ENTRY next = (Irp == NULL) ? head->Flink : Irp->Tail.Overlay.ListEntry.Flink;
        if (next == head) {
            return NULL;
        }
        return CONTAINING_RECORD(next, IRP, Tail.Overlay.ListEntry);
    }

    static VOID CsqAcquireLock(_In_ PIO_CSQ Csq, _Out_ PKIRQL Irql) {
        CAdapterCommon* self = CONTAINING_RECORD(Csq, CAdapterCommon, m_IrpCsq);
        KeAcquireSpinLock(&self->m_StateLock, Irql);
    }

    static VOID CsqReleaseLock(_In_ PIO_CSQ Csq, _In_ KIRQL Irql) {
        CAdapterCommon* self = CONTAINING_RECORD(Csq, CAdapterCommon, m_IrpCsq);
        KeReleaseSpinLock(&self->m_StateLock, Irql);
    }

    static VOID CsqCompleteCanceledIrp(_In_ PIO_CSQ Csq, _In_ PIRP Irp) {
        UNREFERENCED_PARAMETER(Csq);
        Irp->IoStatus.Status = STATUS_CANCELLED;
        Irp->IoStatus.Information = 0;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
    }

    PDEVICE_OBJECT     m_DeviceObject;
    DEVICE_POWER_STATE m_PowerState;
    KSPIN_LOCK         m_StateLock;
    BOOLEAN            m_RenderRunning;
    BOOLEAN            m_CaptureRunning;
    ULONG              m_RenderSampleRate;
    ULONG              m_CaptureSampleRate;
    uint32_t           m_SequenceGeneration;
    LONG               m_VolumeLevel[2];
    BOOL               m_MuteState[2];
    CHvAudioRingBuffer m_RenderRing;
    CHvAudioRingBuffer m_CaptureRing;
    IO_CSQ             m_IrpCsq;
    LIST_ENTRY         m_PendingIrpList;
};

STDMETHODIMP
CAdapterCommon::NonDelegatingQueryInterface(
    _In_ REFIID Interface,
    _COM_Outptr_ PVOID* Object
)
{
    if (IsEqualGUIDAligned(Interface, IID_IUnknown)) {
        *Object = reinterpret_cast<PVOID>(static_cast<PUNKNOWN>(static_cast<IHvAudioAdapterCommon*>(this)));
    } else if (IsEqualGUIDAligned(Interface, IID_IHvAudioAdapterCommon)) {
        *Object = reinterpret_cast<PVOID>(static_cast<PHVAUDIOADAPTERCOMMON>(this));
    } else if (IsEqualGUIDAligned(Interface, IID_IAdapterPowerManagement)) {
        *Object = reinterpret_cast<PVOID>(static_cast<PADAPTERPOWERMANAGEMENT>(this));
    } else {
        *Object = NULL;
        return STATUS_INVALID_PARAMETER;
    }

    reinterpret_cast<PUNKNOWN>(*Object)->AddRef();
    return STATUS_SUCCESS;
}

// ============================================================================
// Sideband Control Device Dispatch Routines (\\.\HvVirtualAudio)
// ============================================================================
static NTSTATUS
HvAudioDispatchCreateCloseCleanup(
    _In_ PDEVICE_OBJECT DeviceObject,
    _Inout_ PIRP Irp
)
{
    if (DeviceObject == g_AudioControlDeviceObject) {
        PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);
        if (stack->MajorFunction == IRP_MJ_CLEANUP && g_ActiveAdapterCommon != NULL) {
            g_ActiveAdapterCommon->CompleteAllPendingStateIrps(STATUS_CANCELLED);
        }
        Irp->IoStatus.Status = STATUS_SUCCESS;
        Irp->IoStatus.Information = 0;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return STATUS_SUCCESS;
    }

    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);
    if (stack->MajorFunction == IRP_MJ_CREATE && g_PortClsCreateDispatch != NULL) {
        return g_PortClsCreateDispatch(DeviceObject, Irp);
    }
    if (stack->MajorFunction == IRP_MJ_CLOSE && g_PortClsCloseDispatch != NULL) {
        return g_PortClsCloseDispatch(DeviceObject, Irp);
    }
    if (stack->MajorFunction == IRP_MJ_CLEANUP && g_PortClsCleanupDispatch != NULL) {
        return g_PortClsCleanupDispatch(DeviceObject, Irp);
    }

    return PcDispatchIrp(DeviceObject, Irp);
}

static NTSTATUS
HvAudioDispatchDeviceControl(
    _In_ PDEVICE_OBJECT DeviceObject,
    _Inout_ PIRP Irp
)
{
    if (DeviceObject != g_AudioControlDeviceObject) {
        if (g_PortClsIoctlDispatch != NULL) {
            return g_PortClsIoctlDispatch(DeviceObject, Irp);
        }
        return PcDispatchIrp(DeviceObject, Irp);
    }

    CAdapterCommon* adapter = g_ActiveAdapterCommon;
    if (adapter == NULL) {
        Irp->IoStatus.Status = STATUS_DEVICE_NOT_READY;
        Irp->IoStatus.Information = 0;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return STATUS_DEVICE_NOT_READY;
    }

    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);
    ULONG ioctl  = stack->Parameters.DeviceIoControl.IoControlCode;
    ULONG inLen  = stack->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outLen = stack->Parameters.DeviceIoControl.OutputBufferLength;
    PVOID buffer = Irp->AssociatedIrp.SystemBuffer;

    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR bytesTransferred = 0;

    switch (ioctl) {
    case IOCTL_HVAUDIO_READ_RENDER_PCM:
        if (buffer == NULL || outLen == 0) {
            status = STATUS_INVALID_PARAMETER;
        } else {
            bytesTransferred = adapter->PullRenderPcmForBridge(
                reinterpret_cast<UCHAR*>(buffer),
                outLen
            );
            status = STATUS_SUCCESS;
        }
        break;

    case IOCTL_HVAUDIO_WRITE_CAPTURE_PCM:
        if (buffer == NULL || inLen == 0) {
            status = STATUS_INVALID_PARAMETER;
        } else {
            bytesTransferred = adapter->PushCapturePcmFromBridge(
                reinterpret_cast<const UCHAR*>(buffer),
                inLen
            );
            status = STATUS_SUCCESS;
        }
        break;

    case IOCTL_HVAUDIO_GET_STATE:
        if (buffer == NULL || outLen < sizeof(HV_AUDIO_CONTROL_PAYLOAD)) {
            status = STATUS_BUFFER_TOO_SMALL;
        } else {
            adapter->GetControlState(reinterpret_cast<PHV_AUDIO_CONTROL_PAYLOAD>(buffer));
            bytesTransferred = sizeof(HV_AUDIO_CONTROL_PAYLOAD);
            status = STATUS_SUCCESS;
        }
        break;

    case IOCTL_HVAUDIO_WAIT_STATE_CHANGE:
        return adapter->HandleWaitStateChangeIrp(Irp);

    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    Irp->IoStatus.Status = status;
    Irp->IoStatus.Information = bytesTransferred;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return status;
}

static VOID
HvAudioDriverUnload(
    _In_ PDRIVER_OBJECT DriverObject
)
{
    if (g_AudioControlDeviceObject != NULL) {
        UNICODE_STRING symLink;
        RtlInitUnicodeString(&symLink, HVAUDIO_DOS_DEVICE_NAME);
        IoDeleteSymbolicLink(&symLink);
        IoDeleteDevice(g_AudioControlDeviceObject);
        g_AudioControlDeviceObject = NULL;
    }

    if (g_PortClsUnload != NULL) {
        g_PortClsUnload(DriverObject);
    }
}

// ============================================================================
// InstallSubdeviceHelper - Creates Port + Miniport Pair and Registers Subdevice
// ============================================================================
static NTSTATUS
InstallSubdevice(
    _In_  PDEVICE_OBJECT       DeviceObject,
    _In_  PIRP                 Irp,
    _In_  PCWSTR               Name,
    _In_  REFGUID              PortClassId,
    _In_  PUNKNOWN             AdapterCommon,
    _In_  HV_AUDIO_DEVICE_TYPE DeviceType,
    _In_  BOOLEAN              IsTopology,
    _Out_ PUNKNOWN*            OutUnknownPort
)
{
    PPORT port = NULL;
    NTSTATUS status = PcNewPort(&port, PortClassId);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    PUNKNOWN miniport = NULL;
    if (IsTopology) {
        status = CreateMiniportTopologyHvAudio(
            &miniport,
            HV_STREAM_GUID_ZERO,
            NULL,
            NonPagedPoolNx,
            AdapterCommon,
            DeviceType
        );
    } else {
        status = CreateMiniportWaveRTHvAudio(
            &miniport,
            HV_STREAM_GUID_ZERO,
            NULL,
            NonPagedPoolNx,
            AdapterCommon,
            DeviceType
        );
    }

    if (!NT_SUCCESS(status)) {
        port->Release();
        return status;
    }

    status = port->Init(DeviceObject, Irp, miniport, AdapterCommon, NULL);
    if (NT_SUCCESS(status)) {
        status = PcRegisterSubdevice(DeviceObject, const_cast<PWSTR>(Name), port);
        if (NT_SUCCESS(status) && OutUnknownPort != NULL) {
            port->QueryInterface(IID_IUnknown, reinterpret_cast<PVOID*>(OutUnknownPort));
        }
    }

    miniport->Release();
    port->Release();
    return status;
}

// ============================================================================
// StartDevice - Initializes AdapterCommon & Registers Speaker + Mic Endpoints
// ============================================================================
static NTSTATUS
HvAudioStartDevice(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PIRP           Irp,
    _In_ PRESOURCELIST  ResourceList
)
{
    UNREFERENCED_PARAMETER(ResourceList);

    auto* adapter = new (NonPagedPoolNx, HVAUDIO_POOL_TAG) CAdapterCommon(NULL);
    if (adapter == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    adapter->AddRef();
    NTSTATUS status = adapter->Init(DeviceObject);
    if (!NT_SUCCESS(status)) {
        adapter->Release();
        return status;
    }

    PUNKNOWN unknownAdapter = static_cast<PUNKNOWN>(static_cast<IHvAudioAdapterCommon*>(adapter));
    PcRegisterAdapterPowerManagement(unknownAdapter, DeviceObject);

    PUNKNOWN unkTopoRender  = NULL;
    PUNKNOWN unkWaveRender  = NULL;
    PUNKNOWN unkTopoCapture = NULL;
    PUNKNOWN unkWaveCapture = NULL;

    // 1. Register "Hyper-V Console Speakers" (Render Topology + WaveRT)
    status = InstallSubdevice(
        DeviceObject, Irp, L"TopologySpeaker",
        CLSID_PortTopology, unknownAdapter,
        HvAudioDeviceType_Render, TRUE, &unkTopoRender
    );

    if (NT_SUCCESS(status)) {
        status = InstallSubdevice(
            DeviceObject, Irp, L"WaveSpeaker",
            CLSID_PortWaveRT, unknownAdapter,
            HvAudioDeviceType_Render, FALSE, &unkWaveRender
        );
    }

    if (NT_SUCCESS(status) && unkWaveRender != NULL && unkTopoRender != NULL) {
        status = PcRegisterPhysicalConnection(
            DeviceObject,
            unkWaveRender, KSPIN_WAVERT_BRIDGE,
            unkTopoRender, KSPIN_TOPO_WAVEOUT_SOURCE
        );
    }

    // 2. Register "Hyper-V Console Microphone" (Capture Topology + WaveRT)
    if (NT_SUCCESS(status)) {
        status = InstallSubdevice(
            DeviceObject, Irp, L"TopologyMicrophone",
            CLSID_PortTopology, unknownAdapter,
            HvAudioDeviceType_Capture, TRUE, &unkTopoCapture
        );
    }

    if (NT_SUCCESS(status)) {
        status = InstallSubdevice(
            DeviceObject, Irp, L"WaveMicrophone",
            CLSID_PortWaveRT, unknownAdapter,
            HvAudioDeviceType_Capture, FALSE, &unkWaveCapture
        );
    }

    if (NT_SUCCESS(status) && unkTopoCapture != NULL && unkWaveCapture != NULL) {
        status = PcRegisterPhysicalConnection(
            DeviceObject,
            unkTopoCapture, KSPIN_TOPO_WAVEIN_DEST,
            unkWaveCapture, KSPIN_WAVERT_BRIDGE
        );
    }

    if (unkTopoRender)  unkTopoRender->Release();
    if (unkWaveRender)  unkWaveRender->Release();
    if (unkTopoCapture) unkTopoCapture->Release();
    if (unkWaveCapture) unkWaveCapture->Release();
    adapter->Release();

    return status;
}

// ============================================================================
// AddDevice & DriverEntry
// ============================================================================
static NTSTATUS
HvAudioAddDevice(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PDEVICE_OBJECT PhysicalDeviceObject
)
{
    return PcAddAdapterDevice(
        DriverObject,
        PhysicalDeviceObject,
        HvAudioStartDevice,
        MAX_MINIPORTS,
        0
    );
}

extern "C"
NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath
)
{
    NTSTATUS status = PcInitializeAdapterDriver(
        DriverObject,
        RegistryPath,
        reinterpret_cast<PDRIVER_ADD_DEVICE>(HvAudioAddDevice)
    );
    if (!NT_SUCCESS(status)) {
        return status;
    }

    // Create sideband Control Device (\\.\HvVirtualAudio) for HvGuestBridge.exe
    UNICODE_STRING ntDeviceName;
    RtlInitUnicodeString(&ntDeviceName, HVAUDIO_NT_DEVICE_NAME);
    UNICODE_STRING sddl;
    RtlInitUnicodeString(&sddl, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");

    status = IoCreateDeviceSecure(
        DriverObject,
        0,
        &ntDeviceName,
        FILE_DEVICE_HVAUDIO,
        FILE_DEVICE_SECURE_OPEN,
        FALSE,
        &sddl,
        &GUID_DEVINTERFACE_HVAUDIO,
        &g_AudioControlDeviceObject
    );
    if (NT_SUCCESS(status)) {
        UNICODE_STRING symLink;
        RtlInitUnicodeString(&symLink, HVAUDIO_DOS_DEVICE_NAME);
        status = IoCreateSymbolicLink(&symLink, &ntDeviceName);
        if (NT_SUCCESS(status)) {
            g_AudioControlDeviceObject->Flags |= DO_BUFFERED_IO;
            g_AudioControlDeviceObject->Flags &= ~DO_DEVICE_INITIALIZING;
        } else {
            IoDeleteDevice(g_AudioControlDeviceObject);
            g_AudioControlDeviceObject = NULL;
        }
    }

    // Hook dispatch table so IRPs targeting \\.\HvVirtualAudio go to our IOCTL handler,
    // while audio/PnP IRPs on the PortCls FDO continue to PortCls seamlessly.
    g_PortClsCreateDispatch  = DriverObject->MajorFunction[IRP_MJ_CREATE];
    g_PortClsCloseDispatch   = DriverObject->MajorFunction[IRP_MJ_CLOSE];
    g_PortClsCleanupDispatch = DriverObject->MajorFunction[IRP_MJ_CLEANUP];
    g_PortClsIoctlDispatch   = DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL];
    g_PortClsUnload          = DriverObject->DriverUnload;

    DriverObject->MajorFunction[IRP_MJ_CREATE]         = HvAudioDispatchCreateCloseCleanup;
    DriverObject->MajorFunction[IRP_MJ_CLOSE]          = HvAudioDispatchCreateCloseCleanup;
    DriverObject->MajorFunction[IRP_MJ_CLEANUP]        = HvAudioDispatchCreateCloseCleanup;
    DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = HvAudioDispatchDeviceControl;
    DriverObject->DriverUnload                         = HvAudioDriverUnload;

    return STATUS_SUCCESS;
}
