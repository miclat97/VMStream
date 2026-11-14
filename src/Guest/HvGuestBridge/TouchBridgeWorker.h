// ============================================================================
// TouchBridgeWorker.h — Guest Multi-Touch AF_HYPERV to VHF Driver Bridge
// Project: VMStream — Guest Service Bridge (HvGuestBridge.exe)
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
    void ReleaseAllTouchContacts();

    std::string                    m_TcpFallbackHost;
    std::unique_ptr<IHvTransport>  m_Transport;
    HANDLE                         m_DriverHandle;
    std::atomic<bool>              m_Running;
    std::thread                    m_Thread;
    uint16_t                       m_LastScanTime;
    bool                           m_HadActiveContacts;
};
