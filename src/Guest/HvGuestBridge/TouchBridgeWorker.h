// ============================================================================
// TouchBridgeWorker.h - Guest Multi-Touch AF_HYPERV to VHF / TouchInjection Bridge
// Project: VMStream - Guest Service Bridge (HvGuestBridge.exe)
// ============================================================================
#pragma once

#include "HvTransport.h"
#include <thread>
#include <atomic>
#include <string>

class TouchBridgeWorker {
public:
    explicit TouchBridgeWorker(const std::string& tcpFallbackHost = "");
    ~TouchBridgeWorker();

    void Start();
    void Stop();

private:
    void WorkerLoop();
    bool EnsureDriverHandleOpen();
    void CloseDriverHandle();
    bool EnsureUserModeTouchInjectionInitialized();
    void InjectFrameUserMode(const HV_TOUCH_INPUT_REPORT& report);
    void ReleaseAllTouchContacts();

    std::string                    m_TcpFallbackHost;
    std::unique_ptr<IHvTransport>  m_Transport;
    HANDLE                         m_DriverHandle;
    bool                           m_TouchInjectionInitialized;
    bool                           m_LoggedDriverFallbackNotice;
    bool                           m_ActiveContactIds[128];
    POINT                          m_LastContactPixels[128];
    std::atomic<bool>              m_Running;
    std::thread                    m_Thread;
    uint16_t                       m_LastScanTime;
    bool                           m_HadActiveContacts;
};
