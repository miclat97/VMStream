// ============================================================================
// Driver.h — KMDF + Virtual HID Framework (VHF) Multi-Touch Digitizer Driver
// Project: VMStream — HvTouchHid.sys
// ============================================================================
#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <hidport.h>
#include <vhf.h>

#include "HvTouchDescriptor.h"

#define HVTOUCH_POOL_TAG 'cuTH' // 'HTuc'

typedef struct _HVTOUCH_DEVICE_CONTEXT {
    WDFDEVICE       Device;
    VHFHANDLE       VhfHandle;
    WDFQUEUE        IoQueue;
    WDFSPINLOCK     ReportLock;
    USHORT          LastScanTime;
    BOOLEAN         VhfStarted;
} HVTOUCH_DEVICE_CONTEXT, *PHVTOUCH_DEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(HVTOUCH_DEVICE_CONTEXT, HvTouchGetDeviceContext)

typedef struct _HVTOUCH_CONTROL_CONTEXT {
    PHVTOUCH_DEVICE_CONTEXT TargetDeviceContext;
} HVTOUCH_CONTROL_CONTEXT, *PHVTOUCH_CONTROL_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(HVTOUCH_CONTROL_CONTEXT, HvTouchGetControlContext)

EXTERN_C_START

DRIVER_INITIALIZE                   DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD           HvTouchEvtDriverDeviceAdd;
EVT_WDF_DEVICE_SELF_MANAGED_IO_INIT HvTouchEvtDeviceSelfManagedIoInit;
EVT_WDF_DEVICE_SELF_MANAGED_IO_CLEANUP HvTouchEvtDeviceSelfManagedIoCleanup;
EVT_WDF_OBJECT_CONTEXT_CLEANUP      HvTouchEvtDeviceContextCleanup;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL  HvTouchEvtIoDeviceControl;

EVT_VHF_ASYNC_OPERATION             EvtVhfAsyncOperationGetFeature;

NTSTATUS
HvTouchCreateControlDevice(
    _In_ WDFDRIVER Driver,
    _In_ PHVTOUCH_DEVICE_CONTEXT DeviceContext
);

VOID
HvTouchDeleteControlDevice(
    VOID
);

NTSTATUS
HvTouchSubmitInputReport(
    _In_ PHVTOUCH_DEVICE_CONTEXT DeviceContext,
    _In_reads_bytes_(sizeof(HV_TOUCH_INPUT_REPORT)) const HV_TOUCH_INPUT_REPORT* Report
);

NTSTATUS
HvTouchResetAllContacts(
    _In_ PHVTOUCH_DEVICE_CONTEXT DeviceContext
);

EXTERN_C_END
