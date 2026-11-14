// ============================================================================
// HvTransport.h — Transport Abstraction (AF_HYPERV Primary + TCP Fallback)
// Project: VMStream — Guest Service Bridge (HvGuestBridge.exe)
// ============================================================================
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

// Include <hvsocket.h> if available in SDK, or define compatible SOCKADDR_HV layout
#if __has_include(<hvsocket.h>)
    #include <hvsocket.h>
#else
    #ifndef AF_HYPERV
        #define AF_HYPERV 34
    #endif
    #ifndef HV_PROTOCOL_RAW
        #define HV_PROTOCOL_RAW 1
    #endif
    typedef struct _SOCKADDR_HV {
        ADDRESS_FAMILY Family;
        USHORT         Reserved;
        GUID           VmId;
        GUID           ServiceId;
    } SOCKADDR_HV, *PSOCKADDR_HV;
#endif

#include <atomic>
#include <mutex>
#include <string>
#include <memory>
#include "../../Shared/HvProtocol.h"

// ============================================================================
// IHvTransport — Clean Transport Abstraction Interface
// ============================================================================
class IHvTransport {
public:
    virtual ~IHvTransport() = default;

    virtual bool Connect(DWORD timeoutMs = 3000) = 0;
    virtual void Disconnect() = 0;
    virtual bool IsConnected() const = 0;

    virtual bool SendExact(const void* data, size_t length) = 0;
    virtual bool RecvExact(void* buffer, size_t length) = 0;

    virtual bool SendPacket(
        HV_PACKET_TYPE type,
        const void* payload,
        uint16_t payloadLength,
        const void* extraPayload = nullptr,
        uint16_t extraLength = 0
    ) = 0;
};

// ============================================================================
// HvSocketTransport — Native Hyper-V Socket (AF_HYPERV / HV_PROTOCOL_RAW)
// Connects from Guest VM to HV_GUID_PARENT with sub-millisecond VMBus latency
// ============================================================================
class HvSocketTransport final : public IHvTransport {
public:
    HvSocketTransport(
        const GUID& serviceGuid,
        uint16_t tcpFallbackPort = 0,
        const std::string& tcpFallbackHost = ""
    );
    ~HvSocketTransport() override;

    bool Connect(DWORD timeoutMs = 3000) override;
    void Disconnect() override;
    bool IsConnected() const override;

    bool SendExact(const void* data, size_t length) override;
    bool RecvExact(void* buffer, size_t length) override;

    bool SendPacket(
        HV_PACKET_TYPE type,
        const void* payload,
        uint16_t payloadLength,
        const void* extraPayload = nullptr,
        uint16_t extraLength = 0
    ) override;

private:
    bool TryConnectHvSocket(DWORD timeoutMs);
    bool TryConnectTcpFallback(DWORD timeoutMs);

    GUID                  m_ServiceGuid;
    uint16_t              m_TcpFallbackPort;
    std::string           m_TcpFallbackHost;
    std::atomic<SOCKET>   m_Socket;
    std::atomic<uint32_t> m_SendSequence;
    std::mutex            m_SendMutex;
};
