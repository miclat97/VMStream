// ============================================================================
// HvTouchDescriptor.h - Complete HID Multi-Touch Report Descriptor for VHF
// Project: VMStream - Guest Multi-Touch Digitizer Driver (HvTouchHid.sys)
// ============================================================================
#pragma once

#if defined(_KERNEL_MODE)
    #include <ntddk.h>
    #include <wdf.h>
    #include <vhf.h>
#endif

#include "../../Shared/HvProtocol.h"

// Single Finger Logical Collection Block
// Encodes: Tip Switch (1b) + Confidence (1b) + Padding (6b) + Contact ID (8b) + X (16b) + Y (16b) = 6 bytes report payload
// Physical dimensions represent a 16:9 display (34.56 cm x 19.44 cm at 10^-2 cm) so Windows 11
// DirectManipulation / Precision Touch computes accurate physical scroll/pan/edge-swipe distances.
#define HID_TOUCH_FINGER_BLOCK \
    0x05, 0x0D,                         /*   Usage Page (Digitizers)                */ \
    0x09, 0x22,                         /*   Usage (Finger)                         */ \
    0xA1, 0x02,                         /*   Collection (Logical)                   */ \
    0x55, 0x00,                         /*     Unit Exponent (0)                    */ \
    0x65, 0x00,                         /*     Unit (None)                          */ \
    0x35, 0x00,                         /*     Physical Minimum (0)                 */ \
    0x45, 0x00,                         /*     Physical Maximum (0)                 */ \
    0x09, 0x42,                         /*     Usage (Tip Switch)                   */ \
    0x09, 0x47,                         /*     Usage (Confidence)                   */ \
    0x15, 0x00,                         /*     Logical Minimum (0)                  */ \
    0x25, 0x01,                         /*     Logical Maximum (1)                  */ \
    0x75, 0x01,                         /*     Report Size (1)                      */ \
    0x95, 0x02,                         /*     Report Count (2)                     */ \
    0x81, 0x02,                         /*     Input (Data,Var,Abs) -> 2 bits       */ \
    0x95, 0x06,                         /*     Report Count (6)                     */ \
    0x81, 0x03,                         /*     Input (Const,Var,Abs) -> 6b padding  */ \
    0x09, 0x51,                         /*     Usage (Contact Identifier)           */ \
    0x75, 0x08,                         /*     Report Size (8)                      */ \
    0x95, 0x01,                         /*     Report Count (1)                     */ \
    0x25, 0x7F,                         /*     Logical Maximum (127)                */ \
    0x81, 0x02,                         /*     Input (Data,Var,Abs) -> 1 byte       */ \
    0x05, 0x01,                         /*     Usage Page (Generic Desktop)         */ \
    0x16, 0x00, 0x00,                   /*     Logical Minimum (0)                  */ \
    0x26, 0xFF, 0x7F,                   /*     Logical Maximum (32767)              */ \
    0x35, 0x00,                         /*     Physical Minimum (0)                 */ \
    0x55, 0x0E,                         /*     Unit Exponent (-2 => 0.01 cm)        */ \
    0x65, 0x11,                         /*     Unit (SI Linear: Centimeter)         */ \
    0x75, 0x10,                         /*     Report Size (16)                     */ \
    0x95, 0x01,                         /*     Report Count (1)                     */ \
    0x46, 0x80, 0x0D,                   /*     Physical Maximum (3456 => 34.56 cm)  */ \
    0x09, 0x30,                         /*     Usage (X)                            */ \
    0x81, 0x02,                         /*     Input (Data,Var,Abs) -> 2 bytes X    */ \
    0x46, 0x98, 0x07,                   /*     Physical Maximum (1944 => 19.44 cm)  */ \
    0x09, 0x31,                         /*     Usage (Y)                            */ \
    0x81, 0x02,                         /*     Input (Data,Var,Abs) -> 2 bytes Y    */ \
    0xC0                                /*   End Collection (Logical)               */

// Complete HID Multi-Touch Digitizer Report Descriptor
// Compliant with Windows 10/11 Precision Touch / Multi-Touch Digitizer requirements:
// - Usage Page 0x0D (Digitizers), Usage 0x04 (Touch Screen)
// - Report ID 1: 10 parallel finger contacts + Contact Count (0x54) + Scan Time (0x56, 100us unit)
// - Report ID 2: Feature Report Contact Count Maximum (0x55 = 10)
// - Report ID 3: Feature Report Vendor THQA Certification Blob (Usage Page 0xFF00, Usage 0xC5, 256 bytes)
static const uint8_t g_HvTouchReportDescriptor[] = {
    0x05, 0x0D,                         // Usage Page (Digitizers)
    0x09, 0x04,                         // Usage (Touch Screen)
    0xA1, 0x01,                         // Collection (Application)

    // =========================================================================
    // REPORT ID 1: Input Report (10 parallel contacts + ContactCount + ScanTime)
    // Total byte length: 1 (ID) + 10 * 6 (Contacts) + 1 (Count) + 2 (ScanTime) = 64 bytes
    // =========================================================================
    0x85, 0x01,                         //   Report ID (1)

    HID_TOUCH_FINGER_BLOCK,             //   Contact Slot 0
    HID_TOUCH_FINGER_BLOCK,             //   Contact Slot 1
    HID_TOUCH_FINGER_BLOCK,             //   Contact Slot 2
    HID_TOUCH_FINGER_BLOCK,             //   Contact Slot 3
    HID_TOUCH_FINGER_BLOCK,             //   Contact Slot 4
    HID_TOUCH_FINGER_BLOCK,             //   Contact Slot 5
    HID_TOUCH_FINGER_BLOCK,             //   Contact Slot 6
    HID_TOUCH_FINGER_BLOCK,             //   Contact Slot 7
    HID_TOUCH_FINGER_BLOCK,             //   Contact Slot 8
    HID_TOUCH_FINGER_BLOCK,             //   Contact Slot 9

    // --- Frame-level metadata (3 bytes) ---
    0x05, 0x0D,                         //   Usage Page (Digitizers)
    0x55, 0x00,                         //   Unit Exponent (0)
    0x65, 0x00,                         //   Unit (None)
    0x35, 0x00,                         //   Physical Minimum (0)
    0x45, 0x00,                         //   Physical Maximum (0)
    0x09, 0x54,                         //   Usage (Contact Count)
    0x15, 0x00,                         //   Logical Minimum (0)
    0x25, 0x0A,                         //   Logical Maximum (10)
    0x75, 0x08,                         //   Report Size (8)
    0x95, 0x01,                         //   Report Count (1)
    0x81, 0x02,                         //   Input (Data,Var,Abs) -> 1 byte

    0x09, 0x56,                         //   Usage (Scan Time)
    0x66, 0x01, 0x10,                   //   Unit (SI Linear: Time in Seconds = 0x1001)
    0x55, 0x0C,                         //   Unit Exponent (-4 => 100 microseconds resolution)
    0x15, 0x00,                         //   Logical Minimum (0)
    0x27, 0xFF, 0xFF, 0x00, 0x00,       //   Logical Maximum (65535)
    0x35, 0x00,                         //   Physical Minimum (0)
    0x47, 0xFF, 0xFF, 0x00, 0x00,       //   Physical Maximum (65535)
    0x75, 0x10,                         //   Report Size (16)
    0x95, 0x01,                         //   Report Count (1)
    0x81, 0x02,                         //   Input (Data,Var,Abs) -> 2 bytes

    // =========================================================================
    // REPORT ID 2: Feature Report - Contact Count Maximum (2 bytes)
    // =========================================================================
    0x85, 0x02,                         //   Report ID (2)
    0x05, 0x0D,                         //   Usage Page (Digitizers)
    0x55, 0x00,                         //   Unit Exponent (0)
    0x65, 0x00,                         //   Unit (None)
    0x35, 0x00,                         //   Physical Minimum (0)
    0x45, 0x00,                         //   Physical Maximum (0)
    0x09, 0x55,                         //   Usage (Contact Count Maximum)
    0x15, 0x00,                         //   Logical Minimum (0)
    0x25, 0x0A,                         //   Logical Maximum (10)
    0x75, 0x08,                         //   Report Size (8)
    0x95, 0x01,                         //   Report Count (1)
    0xB1, 0x02,                         //   Feature (Data,Var,Abs)

    // =========================================================================
    // REPORT ID 3: Feature Report - Windows 8/10/11 THQA Certification Blob (257 bytes)
    // =========================================================================
    0x85, 0x03,                         //   Report ID (3)
    0x06, 0x00, 0xFF,                   //   Usage Page (Vendor Defined 0xFF00)
    0x09, 0xC5,                         //   Usage (0xC5 - Device Certification Status)
    0x15, 0x00,                         //   Logical Minimum (0)
    0x26, 0xFF, 0x00,                   //   Logical Maximum (255)
    0x75, 0x08,                         //   Report Size (8)
    0x96, 0x00, 0x01,                   //   Report Count (256)
    0xB1, 0x02,                         //   Feature (Data,Var,Abs)

    0xC0,                               // End Collection (Application - Touch Screen)

    // =========================================================================
    // TOP-LEVEL COLLECTION 2: High-Resolution Scroll Mouse (Report ID 0x04 / 0x05)
    // Delivers native WM_MOUSEHWHEEL (AC Pan) & hi-res scroll via mouhid.sys
    // across all Guest sessions, including UAC prompts and Lock Screen.
    // =========================================================================
    0x05, 0x01,                         // Usage Page (Generic Desktop)
    0x09, 0x02,                         // Usage (Mouse)
    0xA1, 0x01,                         // Collection (Application)
    0x09, 0x01,                         //   Usage (Pointer)
    0xA1, 0x00,                         //   Collection (Physical)

    // Reset inherited global items from Touch Screen TLC (Unit / Exponent / Physical)
    0x55, 0x00,                         //     Unit Exponent (0)
    0x65, 0x00,                         //     Unit (None)
    0x35, 0x00,                         //     Physical Minimum (0)
    0x45, 0x00,                         //     Physical Maximum (0)

    0x85, HV_TOUCH_REPORT_ID_MOUSE,     //     Report ID (4)

    // 3 Buttons (3 bits) + 5 bits constant padding = 1 byte
    0x05, 0x09,                         //     Usage Page (Button)
    0x19, 0x01,                         //     Usage Minimum (Button 1)
    0x29, 0x03,                         //     Usage Maximum (Button 3)
    0x15, 0x00,                         //     Logical Minimum (0)
    0x25, 0x01,                         //     Logical Maximum (1)
    0x95, 0x03,                         //     Report Count (3)
    0x75, 0x01,                         //     Report Size (1)
    0x81, 0x02,                         //     Input (Data,Var,Abs)
    0x95, 0x01,                         //     Report Count (1)
    0x75, 0x05,                         //     Report Size (5)
    0x81, 0x03,                         //     Input (Const,Var,Abs)

    // Relative X, Y (8-bit signed, 2 bytes; always 0 when injecting pan/wheel)
    0x05, 0x01,                         //     Usage Page (Generic Desktop)
    0x09, 0x30,                         //     Usage (X)
    0x09, 0x31,                         //     Usage (Y)
    0x15, 0x81,                         //     Logical Minimum (-127)
    0x25, 0x7F,                         //     Logical Maximum (127)
    0x75, 0x08,                         //     Report Size (8)
    0x95, 0x02,                         //     Report Count (2)
    0x81, 0x06,                         //     Input (Data,Var,Rel)

    // Vertical Wheel (Usage 0x38, 16-bit signed) + Resolution Multiplier (Report ID 5)
    0xA1, 0x02,                         //     Collection (Logical) - Vertical Wheel
    0x09, 0x48,                         //       Usage (Resolution Multiplier)
    0x85, HV_TOUCH_REPORT_ID_MOUSE_RES_MULT, // Report ID (5)
    0x95, 0x01,                         //       Report Count (1)
    0x75, 0x02,                         //       Report Size (2)
    0x15, 0x00,                         //       Logical Minimum (0)
    0x25, 0x01,                         //       Logical Maximum (1)
    0x35, 0x01,                         //       Physical Minimum (1)
    0x45, 0x78,                         //       Physical Maximum (120)
    0xB1, 0x02,                         //       Feature (Data,Var,Abs)

    0x85, HV_TOUCH_REPORT_ID_MOUSE,     //       Report ID (4)
    0x09, 0x38,                         //       Usage (Wheel)
    0x35, 0x00,                         //       Physical Minimum (0)
    0x45, 0x00,                         //       Physical Maximum (0)
    0x16, 0x01, 0x80,                   //       Logical Minimum (-32767)
    0x26, 0xFF, 0x7F,                   //       Logical Maximum (32767)
    0x75, 0x10,                         //       Report Size (16)
    0x95, 0x01,                         //       Report Count (1)
    0x81, 0x06,                         //       Input (Data,Var,Rel)
    0xC0,                               //     End Collection (Logical)

    // Horizontal AC Pan (Usage Page 0x0C, Usage 0x0238, 16-bit signed) + Resolution Multiplier
    0xA1, 0x02,                         //     Collection (Logical) - Horizontal AC Pan
    0x05, 0x01,                         //       Usage Page (Generic Desktop)
    0x09, 0x48,                         //       Usage (Resolution Multiplier)
    0x85, HV_TOUCH_REPORT_ID_MOUSE_RES_MULT, // Report ID (5)
    0x95, 0x01,                         //       Report Count (1)
    0x75, 0x02,                         //       Report Size (2)
    0x15, 0x00,                         //       Logical Minimum (0)
    0x25, 0x01,                         //       Logical Maximum (1)
    0x35, 0x01,                         //       Physical Minimum (1)
    0x45, 0x78,                         //       Physical Maximum (120)
    0xB1, 0x02,                         //       Feature (Data,Var,Abs)

    // 4-bit padding in Feature Report 0x05 so feature report payload is 8 bits total
    0x35, 0x00,                         //       Physical Minimum (0)
    0x45, 0x00,                         //       Physical Maximum (0)
    0x75, 0x04,                         //       Report Size (4)
    0x95, 0x01,                         //       Report Count (1)
    0xB1, 0x03,                         //       Feature (Const,Var,Abs)

    0x85, HV_TOUCH_REPORT_ID_MOUSE,     //       Report ID (4)
    0x05, 0x0C,                         //       Usage Page (Consumer)
    0x0A, 0x38, 0x02,                   //       Usage (AC Pan)
    0x16, 0x01, 0x80,                   //       Logical Minimum (-32767)
    0x26, 0xFF, 0x7F,                   //       Logical Maximum (32767)
    0x75, 0x10,                         //       Report Size (16)
    0x95, 0x01,                         //       Report Count (1)
    0x81, 0x06,                         //       Input (Data,Var,Rel)
    0xC0,                               //     End Collection (Logical)

    0xC0,                               //   End Collection (Physical)
    0xC0                                // End Collection (Application - Mouse)
};

// Standard Microsoft WDK Touch Hardware Quality Assurance (THQA) 256-byte blob
// Required by hidclass.sys / win32kbase.sys for immediate multi-touch digitizer promotion
static const uint8_t g_HvTouchThqaBlob[256] = {
    0xFC, 0x28, 0xFE, 0x84, 0x40, 0xCB, 0x9A, 0x87, 0x0D, 0xBE, 0x57, 0x3C, 0xB6, 0x70, 0x09, 0x88,
    0x07, 0x97, 0x2D, 0x2B, 0xE3, 0x38, 0x34, 0xB6, 0x6C, 0xED, 0xB0, 0xF7, 0xE5, 0x9C, 0xF6, 0xC2,
    0x2E, 0x84, 0x1B, 0xE8, 0xB4, 0x51, 0x78, 0x43, 0x1F, 0x28, 0x4B, 0x7C, 0x2D, 0x53, 0xAF, 0xFC,
    0x47, 0x70, 0x1B, 0x59, 0x6F, 0x74, 0x43, 0xC4, 0xF3, 0x47, 0x18, 0x53, 0x1A, 0xA2, 0xA1, 0x71,
    0xC7, 0x95, 0x0E, 0x31, 0x55, 0x21, 0xD3, 0xB5, 0x1E, 0xE9, 0x0C, 0xBA, 0xEC, 0xB8, 0x89, 0x19,
    0x3E, 0xB3, 0xAF, 0x75, 0x81, 0x9D, 0x53, 0xB9, 0x41, 0x57, 0xF4, 0x6D, 0x39, 0x25, 0x29, 0x7C,
    0x87, 0xD9, 0xB4, 0x98, 0x45, 0x7D, 0xA7, 0x26, 0x9C, 0x65, 0x3B, 0x85, 0x68, 0x89, 0xD7, 0x3B,
    0xBD, 0xFF, 0x14, 0x67, 0xF2, 0x2B, 0xF0, 0x2A, 0x41, 0x54, 0xF0, 0xFD, 0x2C, 0x66, 0x7C, 0xF8,
    0xC0, 0x8F, 0x33, 0x13, 0x03, 0xF1, 0xD3, 0xC1, 0x0B, 0x89, 0xD9, 0x1B, 0x62, 0xCD, 0x51, 0xB7,
    0x80, 0xB8, 0xAF, 0x3A, 0x10, 0xC1, 0x8A, 0x5B, 0xE8, 0x8A, 0x56, 0xF0, 0x8C, 0xAA, 0xFA, 0x35,
    0xE9, 0x42, 0xC4, 0xD8, 0x55, 0xC3, 0x38, 0xCC, 0x2B, 0x53, 0x5C, 0x69, 0x52, 0xD5, 0xC8, 0x73,
    0x02, 0x38, 0x7C, 0x73, 0xB6, 0x41, 0xE7, 0xFF, 0x05, 0xD8, 0x2B, 0x79, 0x9A, 0xE2, 0x34, 0x60,
    0x8F, 0xA3, 0x32, 0x1F, 0x09, 0x78, 0x62, 0xBC, 0x80, 0xE3, 0x0F, 0xBD, 0x65, 0x20, 0x08, 0x13,
    0xC1, 0xE2, 0xEE, 0x53, 0x2D, 0x86, 0x7E, 0xA7, 0x5A, 0xC5, 0xD3, 0x7D, 0x98, 0xBE, 0x31, 0x48,
    0x1F, 0xFB, 0xDA, 0xAF, 0xA2, 0xA8, 0x6A, 0x89, 0xD6, 0xBF, 0xF2, 0xD3, 0x32, 0x2A, 0x9A, 0xE4,
    0xCF, 0x17, 0xB7, 0xB8, 0xF4, 0xE1, 0x33, 0x08, 0x24, 0x8B, 0xC4, 0x43, 0xA5, 0xE5, 0x24, 0xC2
};
