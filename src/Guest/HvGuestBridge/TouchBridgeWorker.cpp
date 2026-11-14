// ============================================================================
// TouchBridgeWorker.cpp - Guest Multi-Touch AF_HYPERV to VHF Driver Bridge
// Project: VMStream - Guest Service Bridge (HvGuestBridge.exe)
// ============================================================================
#include "TouchBridgeWorker.h"
#include <vector>
#include <cstdio>

TouchBridgeWorker::TouchBridgeWorker(const std::string& tcpFallbackHost)
    : m_TcpFallbackHost(tcpFallbackHost),
      m_Transport(std::make_unique<HvSocketTransport>(
          HV_STREAM_TOUCH_SERVICE_GUID,
          HV_STREAM_TCP_PORT_TOUCH,
          tcpFallbackHost
      )),
      m_DriverHandle(INVALID_HANDLE_VALUE),
      m_Running(false),
      m_LastScanTime(0),
      m_HadActiveContacts(false)
{
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

    return (m_DriverHandle != INVALID_HANDLE_VALUE);
}

void TouchBridgeWorker::CloseDriverHandle()
{
    if (m_DriverHandle != INVALID_HANDLE_VALUE) {
        CloseHandle(m_DriverHandle);
        m_DriverHandle = INVALID_HANDLE_VALUE;
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
    if (!EnsureDriverHandleOpen()) {
        return;
    }

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
