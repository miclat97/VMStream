// ============================================================================
// VmStreamVersion.h - Single Source of Truth for VMStream C++ Binary Versions
// Used by HvGuestBridge.rc, HvTouchHid.rc, and HvVirtualAudio.rc
// ============================================================================
#ifndef VMSTREAM_VERSION_H
#define VMSTREAM_VERSION_H

#define VMSTREAM_VERSION_MAJOR    1
#define VMSTREAM_VERSION_MINOR    2
#define VMSTREAM_VERSION_PATCH    0
#define VMSTREAM_VERSION_BUILD    0

#define VMSTREAM_VERSION_CSV      1,2,0,0
#define VMSTREAM_VERSION_STR      "1.2.0.0"

#define VMSTREAM_COMPANY_NAME     "VMStream Project"
#define VMSTREAM_PRODUCT_NAME     "VMStream"

// Standard Win32 VERSIONINFO constants (from verrsrc.h) defined as fallbacks
// so .rc files compile cleanly in both user-mode SDK and kernel-mode WDK builds.
#ifndef VS_VERSION_INFO
#define VS_VERSION_INFO           1
#endif

#ifndef VS_FF_DEBUG
#define VS_FF_DEBUG               0x00000001L
#endif

#ifndef VS_FFI_FILEFLAGSMASK
#define VS_FFI_FILEFLAGSMASK      0x0000003FL
#endif

#ifndef VOS_NT_WINDOWS32
#define VOS_NT_WINDOWS32          0x00040004L
#endif

#ifndef VFT_APP
#define VFT_APP                   0x00000001L
#endif

#ifndef VFT_DRV
#define VFT_DRV                   0x00000004L
#endif

#ifndef VFT2_UNKNOWN
#define VFT2_UNKNOWN              0x00000000L
#endif

#ifndef VFT2_DRV_SYSTEM
#define VFT2_DRV_SYSTEM           0x00000007L
#endif

#ifndef VFT2_DRV_SOUND
#define VFT2_DRV_SOUND            0x00000009L
#endif

#endif // VMSTREAM_VERSION_H
