// ============================================================================
// HvTransport.cpp - Native Hyper-V Socket (AF_HYPERV) & TCP Fallback Transport
// Project: VMStream - Guest Service Bridge (HvGuestBridge.exe)
// ============================================================================
#include "HvTransport.h"

#include <cstring>
#include <vector>

#pragma comment(lib, "ws2_32.lib")

HvSocketTransport::HvSocketTransport(
    const GUID& serviceGuid,
    uint16_t tcpFallbackPort,
    const std::string& tcpFallbackHost
) : m_ServiceGuid(serviceGuid),
    m_TcpFallbackPort(tcpFallbackPort),
    m_TcpFallbackHost(tcpFallbackHost),
    m_Socket(INVALID_SOCKET),
    m_SendSequence(0)
{
}

HvSocketTransport::~HvSocketTransport()
{
    Disconnect();
}

bool HvSocketTransport::IsConnected() const
{
    return m_Socket.load(std::memory_order_acquire) != INVALID_SOCKET;
}

void HvSocketTransport::Disconnect()
{
    SOCKET s = m_Socket.exchange(INVALID_SOCKET, std::memory_order_acq_rel);
    if (s != INVALID_SOCKET) {
        shutdown(s, SD_BOTH);
        closesocket(s);
    }
}

bool HvSocketTransport::Connect(DWORD timeoutMs)
{
    Disconnect();

    if (TryConnectHvSocket(timeoutMs)) {
        return true;
    }

    if (!m_TcpFallbackHost.empty() && m_TcpFallbackPort != 0) {
        return TryConnectTcpFallback(timeoutMs);
    }

    return false;
}

bool HvSocketTransport::TryConnectHvSocket(DWORD timeoutMs)
{
    SOCKET sock = WSASocketW(
        AF_HYPERV,
        SOCK_STREAM,
        HV_PROTOCOL_RAW,
        nullptr,
        0,
        WSA_FLAG_OVERLAPPED
    );
    if (sock == INVALID_SOCKET) {
        return false;
    }

    // Set non-blocking mode for bounded connect timeout
    u_long nonBlocking = 1;
    ioctlsocket(sock, FIONBIO, &nonBlocking);

    SOCKADDR_HV addr{};
    addr.Family = AF_HYPERV;
    addr.Reserved = 0;
    addr.VmId = HV_STREAM_GUID_PARENT;
    addr.ServiceId = m_ServiceGuid;

    int rc = connect(sock, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr));
    if (rc == SOCKET_ERROR) {
        int err = WSAGetLastError();
        if (err != WSAEWOULDBLOCK && err != WSAEINPROGRESS) {
            closesocket(sock);
            return false;
        }

        fd_set writeSet;
        fd_set errSet;
        FD_ZERO(&writeSet);
        FD_ZERO(&errSet);
        FD_SET(sock, &writeSet);
        FD_SET(sock, &errSet);

        timeval tv{};
        tv.tv_sec = static_cast<long>(timeoutMs / 1000);
        tv.tv_usec = static_cast<long>((timeoutMs % 1000) * 1000);

        int sel = select(0, nullptr, &writeSet, &errSet, &tv);
        if (sel <= 0 || FD_ISSET(sock, &errSet) || !FD_ISSET(sock, &writeSet)) {
            closesocket(sock);
            return false;
        }

        int soError = 0;
        int soLen = sizeof(soError);
        if (getsockopt(sock, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&soError), &soLen) == 0 && soError != 0) {
            closesocket(sock);
            return false;
        }
    }

    // Restore blocking I/O mode for deterministic frame streaming
    nonBlocking = 0;
    ioctlsocket(sock, FIONBIO, &nonBlocking);

    int sendBuf = 65536;
    int recvBuf = 65536;
    setsockopt(sock, SOL_SOCKET, SO_SNDBUF, reinterpret_cast<const char*>(&sendBuf), sizeof(sendBuf));
    setsockopt(sock, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&recvBuf), sizeof(recvBuf));

    m_Socket.store(sock, std::memory_order_release);
    return true;
}

bool HvSocketTransport::TryConnectTcpFallback(DWORD timeoutMs)
{
    SOCKET sock = WSASocketW(
        AF_INET,
        SOCK_STREAM,
        IPPROTO_TCP,
        nullptr,
        0,
        WSA_FLAG_OVERLAPPED
    );
    if (sock == INVALID_SOCKET) {
        return false;
    }

    BOOL noDelay = TRUE;
    setsockopt(sock, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&noDelay), sizeof(noDelay));

    u_long nonBlocking = 1;
    ioctlsocket(sock, FIONBIO, &nonBlocking);

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(m_TcpFallbackPort);
    if (inet_pton(AF_INET, m_TcpFallbackHost.c_str(), &addr.sin_addr) != 1) {
        closesocket(sock);
        return false;
    }

    int rc = connect(sock, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr));
    if (rc == SOCKET_ERROR) {
        int err = WSAGetLastError();
        if (err != WSAEWOULDBLOCK && err != WSAEINPROGRESS) {
            closesocket(sock);
            return false;
        }

        fd_set writeSet;
        fd_set errSet;
        FD_ZERO(&writeSet);
        FD_ZERO(&errSet);
        FD_SET(sock, &writeSet);
        FD_SET(sock, &errSet);

        timeval tv{};
        tv.tv_sec = static_cast<long>(timeoutMs / 1000);
        tv.tv_usec = static_cast<long>((timeoutMs % 1000) * 1000);

        int sel = select(0, nullptr, &writeSet, &errSet, &tv);
        if (sel <= 0 || FD_ISSET(sock, &errSet) || !FD_ISSET(sock, &writeSet)) {
            closesocket(sock);
            return false;
        }
    }

    nonBlocking = 0;
    ioctlsocket(sock, FIONBIO, &nonBlocking);

    int sendBuf = 65536;
    int recvBuf = 65536;
    setsockopt(sock, SOL_SOCKET, SO_SNDBUF, reinterpret_cast<const char*>(&sendBuf), sizeof(sendBuf));
    setsockopt(sock, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&recvBuf), sizeof(recvBuf));

    m_Socket.store(sock, std::memory_order_release);
    return true;
}

bool HvSocketTransport::SendExact(const void* data, size_t length)
{
    SOCKET sock = m_Socket.load(std::memory_order_acquire);
    if (sock == INVALID_SOCKET) {
        return false;
    }

    const char* ptr = static_cast<const char*>(data);
    size_t remaining = length;

    while (remaining > 0) {
        int sent = send(sock, ptr, static_cast<int>(remaining), 0);
        if (sent <= 0) {
            Disconnect();
            return false;
        }
        ptr += sent;
        remaining -= static_cast<size_t>(sent);
    }

    return true;
}

bool HvSocketTransport::RecvExact(void* buffer, size_t length)
{
    SOCKET sock = m_Socket.load(std::memory_order_acquire);
    if (sock == INVALID_SOCKET) {
        return false;
    }

    char* ptr = static_cast<char*>(buffer);
    size_t remaining = length;

    while (remaining > 0) {
        int received = recv(sock, ptr, static_cast<int>(remaining), 0);
        if (received <= 0) {
            Disconnect();
            return false;
        }
        ptr += received;
        remaining -= static_cast<size_t>(received);
    }

    return true;
}

bool HvSocketTransport::SendPacket(
    HV_PACKET_TYPE type,
    const void* payload,
    uint16_t payloadLength,
    const void* extraPayload,
    uint16_t extraLength
)
{
    std::lock_guard<std::mutex> lock(m_SendMutex);

    const uint32_t totalPayload = static_cast<uint32_t>(payloadLength) + static_cast<uint32_t>(extraLength);
    if (totalPayload > 0xFFFFu) {
        return false;
    }

    HV_PACKET_HEADER hdr{};
    hdr.Magic = HV_PACKET_MAGIC;
    hdr.Version = HV_PROTOCOL_VERSION;
    hdr.Type = static_cast<uint8_t>(type);
    hdr.PayloadLength = static_cast<uint16_t>(totalPayload);
    hdr.Sequence = ++m_SendSequence;

    const size_t totalPacketSize = sizeof(hdr) + totalPayload;
    uint8_t stackBuf[4096];
    std::vector<uint8_t> heapBuf;
    uint8_t* dst = stackBuf;
    if (totalPacketSize > sizeof(stackBuf)) {
        heapBuf.resize(totalPacketSize);
        dst = heapBuf.data();
    }

    std::memcpy(dst, &hdr, sizeof(hdr));
    if (payload != nullptr && payloadLength > 0) {
        std::memcpy(dst + sizeof(hdr), payload, payloadLength);
    }
    if (extraPayload != nullptr && extraLength > 0) {
        std::memcpy(dst + sizeof(hdr) + payloadLength, extraPayload, extraLength);
    }

    return SendExact(dst, totalPacketSize);
}
