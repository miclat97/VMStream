// ============================================================================
// AudioBridgeWorker.h - Guest Bidirectional Audio & KSSTATE_RUN Gate Bridge
// Project: VMStream - Guest Service Bridge (HvGuestBridge.exe)
// ============================================================================
#pragma once

#include "HvTransport.h"
#include <thread>
#include <atomic>
#include <string>

class AudioBridgeWorker {
public:
    explicit AudioBridgeWorker(const std::string& tcpFallbackHost = "");
    ~AudioBridgeWorker();

    void Start();
    void Stop();

private:
    void MainConnectionLoop();
    void RenderSenderLoop(HANDLE stopSessionEvent);
    void StateMonitorLoop(HANDLE stopSessionEvent);

    bool EnsureDriverHandleOpen();
    void CloseDriverHandle();

    bool IoctlSync(
        DWORD ioctlCode,
        const void* inBuf,
        DWORD inLen,
        void* outBuf,
        DWORD outLen,
        DWORD* bytesReturned,
        DWORD timeoutMs = INFINITE,
        HANDLE abortEvent = nullptr
    );

    std::string                    m_TcpFallbackHost;
    std::unique_ptr<IHvTransport>  m_Transport;
    HANDLE                         m_DriverHandle;
    std::atomic<bool>              m_Running;
    std::atomic<bool>              m_HostMicEnabled;
    std::atomic<bool>              m_GuestRenderActive;
    std::atomic<bool>              m_GuestCaptureActive;
    std::thread                    m_MainThread;
};
