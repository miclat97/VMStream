// ============================================================================
// Driver.cpp - KMDF + Virtual HID Framework (VHF) Multi-Touch Digitizer Driver
// Project: VMStream - HvTouchHid.sys
// ============================================================================
#include "Driver.h"
#include <wdmsec.h>

static WDFDEVICE g_ControlDevice = NULL;
static KSPIN_LOCK g_ControlDeviceLock;

#ifdef ALLOC_PRAGMA
#pragma alloc_text(INIT, DriverEntry)
#pragma alloc_text(PAGE, HvTouchEvtDriverDeviceAdd)
#pragma alloc_text(PAGE, HvTouchEvtDeviceSelfManagedIoCleanup)
#endif

// ============================================================================
// DriverEntry - Initializes KMDF Driver Object
// ============================================================================
extern "C"
NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath
)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, HvTouchEvtDriverDeviceAdd);

    KeInitializeSpinLock(&g_ControlDeviceLock);

    NTSTATUS status = WdfDriverCreate(
        DriverObject,
        RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES,
        &config,
        WDF_NO_HANDLE
    );

    return status;
}

// ============================================================================
// HvTouchEvtDriverDeviceAdd - Attaches LowerFilter to mshidkmdf & initializes VHF
// ============================================================================
NTSTATUS
HvTouchEvtDriverDeviceAdd(
    _In_    WDFDRIVER       Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
)
{
    PAGED_CODE();

    // Configure as a filter driver below mshidkmdf.sys (which owns the HID FDO)
    WdfFdoInitSetFilter(DeviceInit);

    WDF_PNPPOWER_EVENT_CALLBACKS pnpCallbacks;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpCallbacks);
    pnpCallbacks.EvtDeviceSelfManagedIoInit = HvTouchEvtDeviceSelfManagedIoInit;
    pnpCallbacks.EvtDeviceSelfManagedIoCleanup = HvTouchEvtDeviceSelfManagedIoCleanup;
    WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnpCallbacks);

    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&deviceAttributes, HVTOUCH_DEVICE_CONTEXT);
    deviceAttributes.EvtCleanupCallback = HvTouchEvtDeviceContextCleanup;

    WDFDEVICE device = NULL;
    NTSTATUS status = WdfDeviceCreate(&DeviceInit, &deviceAttributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    PHVTOUCH_DEVICE_CONTEXT devCtx = HvTouchGetDeviceContext(device);
    RtlZeroMemory(devCtx, sizeof(HVTOUCH_DEVICE_CONTEXT));
    devCtx->Device = device;
    devCtx->LastScanTime = 0;
    devCtx->VhfStarted = FALSE;

    WDF_OBJECT_ATTRIBUTES lockAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);
    lockAttributes.ParentObject = device;
    status = WdfSpinLockCreate(&lockAttributes, &devCtx->ReportLock);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    // Configure Virtual HID Framework (VHF)
    VHF_CONFIG vhfConfig;
    VHF_CONFIG_INIT(
        &vhfConfig,
        WdfDeviceWdmGetDeviceObject(device),
        sizeof(g_HvTouchReportDescriptor),
        const_cast<PUCHAR>(g_HvTouchReportDescriptor)
    );

    vhfConfig.VendorID = 0x045E;    // Microsoft Corp.
    vhfConfig.ProductID = 0x09AA;   // VMStream Virtual Precision Touch Digitizer
    vhfConfig.VersionNumber = 0x0100;
    vhfConfig.VhfClientContext = devCtx;
    vhfConfig.EvtVhfAsyncOperationGetFeature = EvtVhfAsyncOperationGetFeature;

    status = VhfCreate(&vhfConfig, &devCtx->VhfHandle);
    if (!NT_SUCCESS(status)) {
        devCtx->VhfHandle = WDF_NO_HANDLE;
        return status;
    }

    // Also create a default I/O queue on the filter device for direct IOCTLs
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = HvTouchEvtIoDeviceControl;

    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &devCtx->IoQueue);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    // Create the sideband Control Device (\\.\HvTouchHid) so HvGuestBridge.exe
    // can submit touch reports without being blocked by hidclass.sys IRP_MJ_CREATE checks.
    status = HvTouchCreateControlDevice(Driver, devCtx);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    return STATUS_SUCCESS;
}

// ============================================================================
// HvTouchCreateControlDevice - Creates \\.\HvTouchHid sideband IOCTL endpoint
// ============================================================================
NTSTATUS
HvTouchCreateControlDevice(
    _In_ WDFDRIVER Driver,
    _In_ PHVTOUCH_DEVICE_CONTEXT DeviceContext
)
{
    if (g_ControlDevice != NULL) {
        PHVTOUCH_CONTROL_CONTEXT existingCtx = HvTouchGetControlContext(g_ControlDevice);
        existingCtx->TargetDeviceContext = DeviceContext;
        return STATUS_SUCCESS;
    }

    // Allow LocalSystem and Administrators full access to \\.\HvTouchHid
    UNICODE_STRING sddl;
    RtlInitUnicodeString(&sddl, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
    PWDFDEVICE_INIT controlInit = WdfControlDeviceInitAllocate(Driver, &sddl);
    if (controlInit == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    WdfDeviceInitSetExclusive(controlInit, FALSE);

    UNICODE_STRING ntDeviceName;
    RtlInitUnicodeString(&ntDeviceName, HVTOUCH_NT_DEVICE_NAME);
    NTSTATUS status = WdfDeviceInitAssignName(controlInit, &ntDeviceName);
    if (!NT_SUCCESS(status)) {
        WdfDeviceInitFree(controlInit);
        return status;
    }

    WDF_OBJECT_ATTRIBUTES controlAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&controlAttributes, HVTOUCH_CONTROL_CONTEXT);

    WDFDEVICE controlDevice = NULL;
    status = WdfDeviceCreate(&controlInit, &controlAttributes, &controlDevice);
    if (!NT_SUCCESS(status)) {
        if (controlInit != NULL) {
            WdfDeviceInitFree(controlInit);
        }
        return status;
    }

    UNICODE_STRING symbolicLinkName;
    RtlInitUnicodeString(&symbolicLinkName, HVTOUCH_DOS_DEVICE_NAME);
    status = WdfDeviceCreateSymbolicLink(controlDevice, &symbolicLinkName);
    if (!NT_SUCCESS(status)) {
        WdfObjectDelete(controlDevice);
        return status;
    }

    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = HvTouchEvtIoDeviceControl;

    WDFQUEUE controlQueue = NULL;
    status = WdfIoQueueCreate(controlDevice, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &controlQueue);
    if (!NT_SUCCESS(status)) {
        WdfObjectDelete(controlDevice);
        return status;
    }

    PHVTOUCH_CONTROL_CONTEXT ctrlCtx = HvTouchGetControlContext(controlDevice);
    ctrlCtx->TargetDeviceContext = DeviceContext;

    WdfControlFinishInitializing(controlDevice);
    g_ControlDevice = controlDevice;

    return STATUS_SUCCESS;
}

VOID
HvTouchDeleteControlDevice(
    VOID
)
{
    WDFDEVICE devToDelete = NULL;
    KLOCK_QUEUE_HANDLE lockHandle;
    KeAcquireInStackQueuedSpinLock(&g_ControlDeviceLock, &lockHandle);
    devToDelete = g_ControlDevice;
    g_ControlDevice = NULL;
    KeReleaseInStackQueuedSpinLock(&lockHandle);

    if (devToDelete != NULL) {
        WdfObjectDelete(devToDelete);
    }
}

// ============================================================================
// HvTouchEvtDeviceSelfManagedIoInit / Cleanup - Starts & Deletes VHF
// ============================================================================
NTSTATUS
HvTouchEvtDeviceSelfManagedIoInit(
    _In_ WDFDEVICE Device
)
{
    PHVTOUCH_DEVICE_CONTEXT devCtx = HvTouchGetDeviceContext(Device);
    if (devCtx->VhfHandle == WDF_NO_HANDLE) {
        return STATUS_INVALID_DEVICE_STATE;
    }

    NTSTATUS status = VhfStart(devCtx->VhfHandle);
    if (NT_SUCCESS(status)) {
        devCtx->VhfStarted = TRUE;
    }
    return status;
}

VOID
HvTouchEvtDeviceSelfManagedIoCleanup(
    _In_ WDFDEVICE Device
)
{
    PAGED_CODE();

    PHVTOUCH_DEVICE_CONTEXT devCtx = HvTouchGetDeviceContext(Device);
    devCtx->VhfStarted = FALSE;

    if (devCtx->VhfHandle != WDF_NO_HANDLE) {
        VhfDelete(devCtx->VhfHandle, TRUE);
        devCtx->VhfHandle = WDF_NO_HANDLE;
    }
}

VOID
HvTouchEvtDeviceContextCleanup(
    _In_ WDFOBJECT DeviceObject
)
{
    PHVTOUCH_DEVICE_CONTEXT devCtx = HvTouchGetDeviceContext(DeviceObject);
    devCtx->VhfStarted = FALSE;

    if (g_ControlDevice != NULL) {
        PHVTOUCH_CONTROL_CONTEXT ctrlCtx = HvTouchGetControlContext(g_ControlDevice);
        if (ctrlCtx != NULL && ctrlCtx->TargetDeviceContext == devCtx) {
            ctrlCtx->TargetDeviceContext = NULL;
            HvTouchDeleteControlDevice();
        }
    }
}

// ============================================================================
// EvtVhfAsyncOperationGetFeature - Handles Report ID 0x02 & 0x03 Feature Queries
// Without handling both 0x02 (Max Contacts = 10) and 0x03 (THQA Certification),
// Windows 11 hidclass.sys / win32kbase.sys disables multi-touch digitizer mode.
// ============================================================================
VOID
EvtVhfAsyncOperationGetFeature(
    _In_     PVOID              VhfClientContext,
    _In_     VHFOPERATIONHANDLE VhfOperationHandle,
    _In_opt_ PVOID              VhfOperationContext,
    _In_     PHID_XFER_PACKET   HidTransferPacket
)
{
    UNREFERENCED_PARAMETER(VhfClientContext);
    UNREFERENCED_PARAMETER(VhfOperationContext);

    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;

    if (HidTransferPacket != NULL && HidTransferPacket->reportBuffer != NULL) {
        if (HidTransferPacket->reportId == HV_TOUCH_REPORT_ID_MAX_COUNT &&
            HidTransferPacket->reportBufferLen >= sizeof(HV_TOUCH_MAX_COUNT_FEATURE))
        {
            auto* feat = reinterpret_cast<PHV_TOUCH_MAX_COUNT_FEATURE>(HidTransferPacket->reportBuffer);
            feat->ReportId = HV_TOUCH_REPORT_ID_MAX_COUNT;
            feat->MaxCount = HV_MAX_TOUCH_CONTACTS;
            HidTransferPacket->reportBufferLen = sizeof(HV_TOUCH_MAX_COUNT_FEATURE);
            status = STATUS_SUCCESS;
        }
        else if (HidTransferPacket->reportId == HV_TOUCH_REPORT_ID_THQA_CERT &&
                 HidTransferPacket->reportBufferLen >= sizeof(HV_TOUCH_CERT_FEATURE))
        {
            auto* cert = reinterpret_cast<PHV_TOUCH_CERT_FEATURE>(HidTransferPacket->reportBuffer);
            cert->ReportId = HV_TOUCH_REPORT_ID_THQA_CERT;
            RtlCopyMemory(cert->Blob, g_HvTouchThqaBlob, sizeof(g_HvTouchThqaBlob));
            HidTransferPacket->reportBufferLen = sizeof(HV_TOUCH_CERT_FEATURE);
            status = STATUS_SUCCESS;
        }
    }

    VhfAsyncOperationComplete(VhfOperationHandle, status);
}

// ============================================================================
// HvTouchSubmitInputReport - Sanitizes and submits 64-byte HID Input Report
// ============================================================================
NTSTATUS
HvTouchSubmitInputReport(
    _In_ PHVTOUCH_DEVICE_CONTEXT DeviceContext,
    _In_reads_bytes_(sizeof(HV_TOUCH_INPUT_REPORT)) const HV_TOUCH_INPUT_REPORT* Report
)
{
    if (DeviceContext == NULL || !DeviceContext->VhfStarted || DeviceContext->VhfHandle == WDF_NO_HANDLE) {
        return STATUS_DEVICE_NOT_READY;
    }

    HV_TOUCH_INPUT_REPORT sanitized;
    RtlCopyMemory(&sanitized, Report, sizeof(HV_TOUCH_INPUT_REPORT));
    sanitized.ReportId = HV_TOUCH_REPORT_ID_INPUT;

    if (sanitized.ContactCount > HV_MAX_TOUCH_CONTACTS) {
        sanitized.ContactCount = HV_MAX_TOUCH_CONTACTS;
    }

    // Clamp coordinates to [0..32767] and enforce Confidence=1 for active contacts
    for (UCHAR i = 0; i < HV_MAX_TOUCH_CONTACTS; ++i) {
        if (sanitized.Contacts[i].X > HV_TOUCH_COORDINATE_MAX) {
            sanitized.Contacts[i].X = HV_TOUCH_COORDINATE_MAX;
        }
        if (sanitized.Contacts[i].Y > HV_TOUCH_COORDINATE_MAX) {
            sanitized.Contacts[i].Y = HV_TOUCH_COORDINATE_MAX;
        }
        if (sanitized.Contacts[i].TipSwitch) {
            sanitized.Contacts[i].Confidence = 1;
        }
        sanitized.Contacts[i].Padding = 0;
    }

    WdfSpinLockAcquire(DeviceContext->ReportLock);
    DeviceContext->LastScanTime = sanitized.ScanTime;

    HID_XFER_PACKET transferPacket;
    transferPacket.reportBuffer = reinterpret_cast<PUCHAR>(&sanitized);
    transferPacket.reportBufferLen = sizeof(HV_TOUCH_INPUT_REPORT);
    transferPacket.reportId = HV_TOUCH_REPORT_ID_INPUT;

    NTSTATUS status = VhfReadReportSubmit(DeviceContext->VhfHandle, &transferPacket);
    WdfSpinLockRelease(DeviceContext->ReportLock);

    return status;
}

// ============================================================================
// HvTouchResetAllContacts - Releases all touch contacts (TipSwitch = 0)
// ============================================================================
NTSTATUS
HvTouchResetAllContacts(
    _In_ PHVTOUCH_DEVICE_CONTEXT DeviceContext
)
{
    if (DeviceContext == NULL || !DeviceContext->VhfStarted || DeviceContext->VhfHandle == WDF_NO_HANDLE) {
        return STATUS_DEVICE_NOT_READY;
    }

    HV_TOUCH_INPUT_REPORT emptyReport;
    RtlZeroMemory(&emptyReport, sizeof(HV_TOUCH_INPUT_REPORT));
    emptyReport.ReportId = HV_TOUCH_REPORT_ID_INPUT;
    emptyReport.ContactCount = 0;

    WdfSpinLockAcquire(DeviceContext->ReportLock);
    DeviceContext->LastScanTime = static_cast<USHORT>(DeviceContext->LastScanTime + 100);
    emptyReport.ScanTime = DeviceContext->LastScanTime;

    HID_XFER_PACKET transferPacket;
    transferPacket.reportBuffer = reinterpret_cast<PUCHAR>(&emptyReport);
    transferPacket.reportBufferLen = sizeof(HV_TOUCH_INPUT_REPORT);
    transferPacket.reportId = HV_TOUCH_REPORT_ID_INPUT;

    NTSTATUS status = VhfReadReportSubmit(DeviceContext->VhfHandle, &transferPacket);
    WdfSpinLockRelease(DeviceContext->ReportLock);

    return status;
}

// ============================================================================
// HvTouchEvtIoDeviceControl - Dispatches IOCTL_HVTOUCH_SUBMIT_FRAME / RESET
// ============================================================================
VOID
HvTouchEvtIoDeviceControl(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t     OutputBufferLength,
    _In_ size_t     InputBufferLength,
    _In_ ULONG      IoControlCode
)
{
    UNREFERENCED_PARAMETER(OutputBufferLength);

    WDFDEVICE queueDevice = WdfIoQueueGetDevice(Queue);
    PHVTOUCH_DEVICE_CONTEXT devCtx = HvTouchGetDeviceContext(queueDevice);
    if (devCtx == NULL) {
        PHVTOUCH_CONTROL_CONTEXT ctrlCtx = HvTouchGetControlContext(queueDevice);
        if (ctrlCtx != NULL) {
            devCtx = ctrlCtx->TargetDeviceContext;
        }
    }

    if (devCtx == NULL) {
        WdfRequestComplete(Request, STATUS_DEVICE_NOT_READY);
        return;
    }

    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;

    switch (IoControlCode) {
    case IOCTL_HVTOUCH_SUBMIT_FRAME:
    {
        if (InputBufferLength < sizeof(HV_TOUCH_INPUT_REPORT)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        PVOID inputBuffer = NULL;
        size_t bufferLen = 0;
        status = WdfRequestRetrieveInputBuffer(
            Request,
            sizeof(HV_TOUCH_INPUT_REPORT),
            &inputBuffer,
            &bufferLen
        );
        if (NT_SUCCESS(status)) {
            status = HvTouchSubmitInputReport(
                devCtx,
                reinterpret_cast<const HV_TOUCH_INPUT_REPORT*>(inputBuffer)
            );
        }
        break;
    }

    case IOCTL_HVTOUCH_RESET_CONTACTS:
    {
        status = HvTouchResetAllContacts(devCtx);
        break;
    }

    default:
        // Forward unhandled internal/HID requests down the stack if received on the FDO queue
        if (queueDevice == devCtx->Device) {
            WDF_REQUEST_SEND_OPTIONS sendOptions;
            WDF_REQUEST_SEND_OPTIONS_INIT(&sendOptions, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
            if (WdfRequestSend(Request, WdfDeviceGetIoTarget(queueDevice), &sendOptions)) {
                return;
            }
            status = WdfRequestGetStatus(Request);
        }
        break;
    }

    WdfRequestComplete(Request, status);
}
