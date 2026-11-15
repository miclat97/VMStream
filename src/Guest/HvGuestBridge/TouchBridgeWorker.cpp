// ============================================================================
// TouchBridgeWorker.cpp - Guest Multi-Touch AF_HYPERV to VHF / TouchInjection Bridge
// Project: VMStream - Guest Service Bridge (HvGuestBridge.exe)
// ============================================================================
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif

#include "TouchBridgeWorker.h"
#include <vector>
#include <cstdio>
#include <cstring>

TouchBridgeWorker::TouchBridgeWorker(const std::string& tcpFallbackHost)
    : m_TcpFallbackHost(tcpFallbackHost),
      m_Transport(std::make_unique<HvSocketTransport>(
          HV_STREAM_TOUCH_SERVICE_GUID,
          HV_STREAM_TCP_PORT_TOUCH,
          tcpFallbackHost
      )),
      m_DriverHandle(INVALID_HANDLE_VALUE),
      m_TouchInjectionInitialized(false),
      m_LoggedDriverStatus(false),
      m_LastDriverOpenState(false),
      m_PrimaryContactId(-1),
      m_Running(false),
      m_LastScanTime(0),
      m_HadActiveContacts(false)
{
    std::memset(m_ActiveContactIds, 0, sizeof(m_ActiveContactIds));
    std::memset(m_LastContactPixels, 0, sizeof(m_LastContactPixels));
}

TouchBridgeWorker::~TouchBridgeWorker()
{
    Stop();
}

void TouchBridgeWorker::Start()
{
    bool expected = false;
    if (!m_Running.compare_exchange_strong(expected, true)) {
        return;
    }

    m_Thread = std::thread(&TouchBridgeWorker::WorkerLoop, this);
}

void TouchBridgeWorker::Stop()
{
    bool expected = true;
    if (!m_Running.compare_exchange_strong(expected, false)) {
        return;
    }

    if (m_Transport) {
        m_Transport->Disconnect();
    }

    if (m_Thread.joinable()) {
        m_Thread.join();
    }

    ReleaseAllTouchContacts();
    CloseDriverHandle();
}

bool TouchBridgeWorker::EnsureDriverHandleOpen()
{
    if (m_DriverHandle != INVALID_HANDLE_VALUE) {
        return true;
    }

    m_DriverHandle = CreateFileW(
        HVTOUCH_WIN32_DEVICE_PATH,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr
    );

    if (m_DriverHandle != INVALID_HANDLE_VALUE) {
        if (!m_LoggedDriverStatus || !m_LastDriverOpenState) {
            m_LoggedDriverStatus = true;
            m_LastDriverOpenState = true;
            std::printf("[HvGuestBridge][Touch] Opened \\\\.\\HvTouchHid kernel VHF digitizer device.\n");
        }
        return true;
    }

    if (!m_LoggedDriverStatus || m_LastDriverOpenState) {
        const DWORD err = GetLastError();
        m_LoggedDriverStatus = true;
        m_LastDriverOpenState = false;
        std::printf(
            "[HvGuestBridge][Touch] \\\\.\\HvTouchHid unavailable (err=%lu); using InjectTouchInput fallback.\n",
            static_cast<unsigned long>(err)
        );
    }

    return false;
}

void TouchBridgeWorker::CloseDriverHandle()
{
    if (m_DriverHandle != INVALID_HANDLE_VALUE) {
        CloseHandle(m_DriverHandle);
        m_DriverHandle = INVALID_HANDLE_VALUE;
        m_LoggedDriverStatus = false;
    }
}

bool TouchBridgeWorker::EnsureUserModeTouchInjectionInitialized()
{
    if (m_TouchInjectionInitialized) {
        return true;
    }

    if (InitializeTouchInjection(HV_MAX_TOUCH_CONTACTS, TOUCH_FEEDBACK_DEFAULT)) {
        m_TouchInjectionInitialized = true;
        return true;
    }

    return false;
}

void TouchBridgeWorker::InjectFrameUserMode(const HV_TOUCH_INPUT_REPORT& report)
{
    if (!EnsureUserModeTouchInjectionInitialized()) {
        return;
    }

    int screenX = GetSystemMetrics(SM_XVIRTUALSCREEN);
    int screenY = GetSystemMetrics(SM_YVIRTUALSCREEN);
    int screenW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    int screenH = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    if (screenW <= 0) screenW = GetSystemMetrics(SM_CXSCREEN);
    if (screenH <= 0) screenH = GetSystemMetrics(SM_CYSCREEN);
    if (screenW <= 0) screenW = 1920;
    if (screenH <= 0) screenH = 1080;

    POINTER_TOUCH_INFO contacts[HV_MAX_TOUCH_CONTACTS]{};
    UINT32 injectCount = 0;
    bool seenInFrame[128]{};

    const uint8_t count = (report.ContactCount <= HV_MAX_TOUCH_CONTACTS)
        ? report.ContactCount
        : HV_MAX_TOUCH_CONTACTS;

    for (uint8_t i = 0; i < count; ++i) {
        const HV_TOUCH_CONTACT& c = report.Contacts[i];
        const uint8_t id = static_cast<uint8_t>(c.ContactId & 0x7F);
        seenInFrame[id] = true;

        LONG px = screenX + static_cast<LONG>((static_cast<int64_t>(c.X) * (screenW - 1)) / HV_TOUCH_COORDINATE_MAX);
        LONG py = screenY + static_cast<LONG>((static_cast<int64_t>(c.Y) * (screenH - 1)) / HV_TOUCH_COORDINATE_MAX);

        bool wasDown = m_ActiveContactIds[id];
        bool isDown  = (c.TipSwitch != 0);

        if (!isDown && !wasDown) {
            continue;
        }

        if (isDown && m_PrimaryContactId < 0) {
            m_PrimaryContactId = static_cast<int>(id);
        }

        POINTER_TOUCH_INFO& ti = contacts[injectCount++];
        ti.pointerInfo.pointerType = PT_TOUCH;
        ti.pointerInfo.pointerId = id;
        ti.pointerInfo.ptPixelLocation.x = px;
        ti.pointerInfo.ptPixelLocation.y = py;

        UINT32 ptrFlags = 0;
        if (isDown && !wasDown) {
            ptrFlags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
            m_ActiveContactIds[id] = true;
            m_LastContactPixels[id] = { px, py };
        } else if (isDown && wasDown) {
            ptrFlags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
            m_LastContactPixels[id] = { px, py };
        } else {
            ptrFlags = POINTER_FLAG_UP;
            m_ActiveContactIds[id] = false;
        }

        if (c.Confidence != 0) {
            ptrFlags |= POINTER_FLAG_CONFIDENCE;
        }
        if (m_PrimaryContactId == static_cast<int>(id)) {
            ptrFlags |= POINTER_FLAG_PRIMARY;
        }
        ti.pointerInfo.pointerFlags = ptrFlags;

        ti.touchFlags = TOUCH_FLAG_NONE;
        ti.touchMask  = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE;
        ti.rcContact.left   = px - 12;
        ti.rcContact.top    = py - 12;
        ti.rcContact.right  = px + 12;
        ti.rcContact.bottom = py + 12;
        ti.orientation = 90;
        ti.pressure    = 512;
    }

    // Release any previously active contact IDs that were omitted from this frame
    for (uint8_t id = 0; id < 128 && injectCount < HV_MAX_TOUCH_CONTACTS; ++id) {
        if (m_ActiveContactIds[id] && !seenInFrame[id]) {
            POINTER_TOUCH_INFO& ti = contacts[injectCount++];
            ti.pointerInfo.pointerType = PT_TOUCH;
            ti.pointerInfo.pointerId = id;
            ti.pointerInfo.ptPixelLocation = m_LastContactPixels[id];
            ti.pointerInfo.pointerFlags = POINTER_FLAG_UP | POINTER_FLAG_CONFIDENCE |
                ((m_PrimaryContactId == static_cast<int>(id)) ? POINTER_FLAG_PRIMARY : 0u);
            m_ActiveContactIds[id] = false;
        }
    }

    bool anyStillActive = false;
    for (uint8_t id = 0; id < 128; ++id) {
        if (m_ActiveContactIds[id]) {
            anyStillActive = true;
            break;
        }
    }
    if (!anyStillActive) {
        m_PrimaryContactId = -1;
    }

    if (injectCount > 0) {
        if (!InjectTouchInput(injectCount, contacts)) {
            // Re-initialize on desktop switch if needed
            m_TouchInjectionInitialized = false;
            if (EnsureUserModeTouchInjectionInitialized()) {
                InjectTouchInput(injectCount, contacts);
            }
        }
    }
}

void TouchBridgeWorker::InjectMouseScrollUserMode(const HV_MOUSE_SCROLL_PAYLOAD& payload)
{
    INPUT inputs[2]{};
    UINT count = 0;

    if (payload.HorizontalDelta != 0) {
        inputs[count].type = INPUT_MOUSE;
        inputs[count].mi.dwFlags = MOUSEEVENTF_HWHEEL;
        inputs[count].mi.mouseData = static_cast<DWORD>(static_cast<int32_t>(payload.HorizontalDelta));
        ++count;
    }

    if (payload.VerticalDelta != 0) {
        inputs[count].type = INPUT_MOUSE;
        inputs[count].mi.dwFlags = MOUSEEVENTF_WHEEL;
        inputs[count].mi.mouseData = static_cast<DWORD>(static_cast<int32_t>(payload.VerticalDelta));
        ++count;
    }

    if (count > 0) {
        SendInput(count, inputs, sizeof(INPUT));
    }
}

// ============================================================================
// ReleaseAllTouchContacts - Anti-Stuck-Touch Protection
// Immediately submits a zeroed 64-byte HV_TOUCH_INPUT_REPORT (ContactCount=0,
// all 10 TipSwitch=0) + IOCTL_HVTOUCH_RESET_CONTACTS whenever the AF_HYPERV
// socket disconnects mid-gesture.
// ============================================================================
void TouchBridgeWorker::ReleaseAllTouchContacts()
{
    if (EnsureDriverHandleOpen()) {
        HV_TOUCH_INPUT_REPORT zeroReport{};
        zeroReport.ReportId = HV_TOUCH_REPORT_ID_INPUT;
        zeroReport.ContactCount = 0;
        m_LastScanTime = static_cast<uint16_t>(m_LastScanTime + 100);
        zeroReport.ScanTime = m_LastScanTime;

        DWORD bytesReturned = 0;
        DeviceIoControl(
            m_DriverHandle,
            IOCTL_HVTOUCH_SUBMIT_FRAME,
            &zeroReport,
            sizeof(zeroReport),
            nullptr,
            0,
            &bytesReturned,
            nullptr
        );

        DeviceIoControl(
            m_DriverHandle,
            IOCTL_HVTOUCH_RESET_CONTACTS,
            nullptr,
            0,
            nullptr,
            0,
            &bytesReturned,
            nullptr
        );
    }

    if (m_TouchInjectionInitialized) {
        POINTER_TOUCH_INFO upContacts[HV_MAX_TOUCH_CONTACTS]{};
        UINT32 count = 0;
        for (uint8_t id = 0; id < 128 && count < HV_MAX_TOUCH_CONTACTS; ++id) {
            if (m_ActiveContactIds[id]) {
                POINTER_TOUCH_INFO& ti = upContacts[count++];
                ti.pointerInfo.pointerType = PT_TOUCH;
                ti.pointerInfo.pointerId = id;
                ti.pointerInfo.ptPixelLocation = m_LastContactPixels[id];
                ti.pointerInfo.pointerFlags = POINTER_FLAG_UP | POINTER_FLAG_CONFIDENCE |
                    ((m_PrimaryContactId == static_cast<int>(id)) ? POINTER_FLAG_PRIMARY : 0u);
                m_ActiveContactIds[id] = false;
            }
        }
        if (count > 0) {
            InjectTouchInput(count, upContacts);
        }
    }

    m_PrimaryContactId = -1;
    m_HadActiveContacts = false;
}

void TouchBridgeWorker::WorkerLoop()
{
    // Boost touch bridge thread priority for sub-millisecond digitizer dispatch
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);

    std::vector<uint8_t> scratchPayload(1024);

    while (m_Running.load(std::memory_order_acquire)) {
        EnsureDriverHandleOpen();

        if (!m_Transport->Connect(1500)) {
            Sleep(500);
            continue;
        }

        std::printf("[HvGuestBridge][Touch] Connected to Host AF_HYPERV Touch Service.\n");

        // Packet receive loop
        while (m_Running.load(std::memory_order_acquire) && m_Transport->IsConnected()) {
            HV_PACKET_HEADER hdr{};
            if (!m_Transport->RecvExact(&hdr, sizeof(hdr))) {
                break;
            }

            if (hdr.Magic != HV_PACKET_MAGIC || hdr.Version != HV_PROTOCOL_VERSION) {
                std::printf("[HvGuestBridge][Touch] Invalid packet header magic/version; resetting connection.\n");
                m_Transport->Disconnect();
                break;
            }

            const auto packetType = static_cast<HV_PACKET_TYPE>(hdr.Type);

            if (packetType == HvPacketType_TouchFrame && hdr.PayloadLength == sizeof(HV_TOUCH_INPUT_REPORT)) {
                HV_TOUCH_INPUT_REPORT report{};
                if (!m_Transport->RecvExact(&report, sizeof(report))) {
                    break;
                }

                report.ReportId = HV_TOUCH_REPORT_ID_INPUT;
                m_LastScanTime = report.ScanTime;

                bool anyActive = (report.ContactCount > 0);
                for (int i = 0; i < HV_MAX_TOUCH_CONTACTS; ++i) {
                    if (report.Contacts[i].TipSwitch != 0) {
                        anyActive = true;
                        break;
                    }
                }
                m_HadActiveContacts = anyActive;

                if (EnsureDriverHandleOpen()) {
                    DWORD bytesReturned = 0;
                    BOOL ok = DeviceIoControl(
                        m_DriverHandle,
                        IOCTL_HVTOUCH_SUBMIT_FRAME,
                        &report,
                        sizeof(report),
                        nullptr,
                        0,
                        &bytesReturned,
                        nullptr
                    );
                    if (!ok && GetLastError() == ERROR_INVALID_HANDLE) {
                        CloseDriverHandle();
                    }
                } else {
                    // Fallback to Win32 InitializeTouchInjection + InjectTouchInput when HvTouchHid.sys is not yet installed
                    InjectFrameUserMode(report);
                }
            }
            else if (packetType == HvPacketType_MouseScroll && hdr.PayloadLength == sizeof(HV_MOUSE_SCROLL_PAYLOAD)) {
                HV_MOUSE_SCROLL_PAYLOAD scrollPayload{};
                if (!m_Transport->RecvExact(&scrollPayload, sizeof(scrollPayload))) {
                    break;
                }

                if (scrollPayload.HorizontalDelta != 0 || scrollPayload.VerticalDelta != 0) {
                    bool injectedViaDriver = false;
                    if (EnsureDriverHandleOpen()) {
                        DWORD bytesReturned = 0;
                        BOOL ok = DeviceIoControl(
                            m_DriverHandle,
                            IOCTL_HVTOUCH_SUBMIT_MOUSE_SCROLL,
                            &scrollPayload,
                            sizeof(scrollPayload),
                            nullptr,
                            0,
                            &bytesReturned,
                            nullptr
                        );
                        if (ok) {
                            injectedViaDriver = true;
                        } else if (GetLastError() == ERROR_INVALID_HANDLE) {
                            CloseDriverHandle();
                        }
                    }

                    if (!injectedViaDriver) {
                        InjectMouseScrollUserMode(scrollPayload);
                    }
                }
            }
            else if (packetType == HvPacketType_TouchReset) {
                if (hdr.PayloadLength > 0) {
                    if (scratchPayload.size() < hdr.PayloadLength) {
                        scratchPayload.resize(hdr.PayloadLength);
                    }
                    if (!m_Transport->RecvExact(scratchPayload.data(), hdr.PayloadLength)) {
                        break;
                    }
                }
                ReleaseAllTouchContacts();
            }
            else if (packetType == HvPacketType_Ping && hdr.PayloadLength == sizeof(HV_PING_PAYLOAD)) {
                HV_PING_PAYLOAD ping{};
                if (!m_Transport->RecvExact(&ping, sizeof(ping))) {
                    break;
                }
                LARGE_INTEGER qpc{};
                QueryPerformanceCounter(&qpc);
                ping.GuestTimestampQpc = static_cast<uint64_t>(qpc.QuadPart);
                m_Transport->SendPacket(HvPacketType_Pong, &ping, sizeof(ping));
            }
            else {
                // Drain unknown payload
                if (hdr.PayloadLength > 0) {
                    if (scratchPayload.size() < hdr.PayloadLength) {
                        scratchPayload.resize(hdr.PayloadLength);
                    }
                    if (!m_Transport->RecvExact(scratchPayload.data(), hdr.PayloadLength)) {
                        break;
                    }
                }
            }
        }

        // Socket disconnected: immediately zero out any active touch contacts so no finger gets stuck!
        if (m_HadActiveContacts) {
            std::printf("[HvGuestBridge][Touch] Host disconnected during active touch; releasing all contacts.\n");
        }
        ReleaseAllTouchContacts();
        m_Transport->Disconnect();

        if (m_Running.load(std::memory_order_acquire)) {
            Sleep(250);
        }
    }
}
