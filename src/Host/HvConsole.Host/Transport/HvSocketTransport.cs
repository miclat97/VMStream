// ============================================================================
// HvSocketTransport.cs - Native AF_HYPERV Host Listener & TCP Fallback
// Project: VMStream - Host Console Client (HvConsole.Host)
// Binds SOCKADDR_HV specifically to VmId = TargetVmId (never HV_GUID_WILDCARD)
// to support multiple simultaneous isolated VM console sessions.
// ============================================================================
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HvConsole.Host.Protocol;

namespace HvConsole.Host.Transport;

public sealed class HvSocketTransport : IHvTransport
{
    private const short AF_HYPERV = 34;
    private const int SOCK_STREAM = 1;
    private const int HV_PROTOCOL_RAW = 1;
    private const int SOL_SOCKET = 0xFFFF;
    private const int SO_SNDBUF = 0x1001;
    private const int SO_RCVBUF = 0x1002;
    private static readonly IntPtr INVALID_SOCKET = new(-1);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SOCKADDR_HV
    {
        public short Family;      // AF_HYPERV (34)
        public ushort Reserved;   // 0
        public Guid VmId;         // Target VM GUID (strictly non-wildcard)
        public Guid ServiceId;    // Service GUID
    }

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSAStartup(short wVersionRequested, out WSADATA lpWSAData);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern IntPtr socket(int af, int type, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int bind(IntPtr s, ref SOCKADDR_HV addr, int namelen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int listen(IntPtr s, int backlog);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern IntPtr accept(IntPtr s, IntPtr addr, IntPtr addrlen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern unsafe int send(IntPtr s, byte* buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern unsafe int recv(IntPtr s, byte* buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int setsockopt(IntPtr s, int level, int optname, ref int optval, int optlen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int shutdown(IntPtr s, int how);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int closesocket(IntPtr s);

    [DllImport("ws2_32.dll")]
    private static extern int WSAGetLastError();

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WSADATA
    {
        public short wVersion;
        public short wHighVersion;
        public fixed byte szDescription[257];
        public fixed byte szSystemStatus[129];
        public ushort iMaxSockets;
        public ushort iMaxUdpDg;
        public IntPtr lpVendorInfo;
    }

    private readonly int _tcpFallbackPort;
    private readonly bool _forceTcpFallback;
    private readonly object _sendLock = new();

    private IntPtr _listenHvSocket = INVALID_SOCKET;
    private IntPtr _clientHvSocket = INVALID_SOCKET;
    private TcpListener? _tcpListener;
    private Socket? _tcpClientSocket;

    private Thread? _acceptAndReceiveThread;
    private volatile bool _running;
    private volatile bool _isConnected;
    private volatile bool _usingTcpFallback;
    private uint _sequence;
    private double _lastRttMs;

    public Guid TargetVmId { get; }
    public Guid ServiceId { get; }
    public bool IsConnected => _isConnected;
    public bool IsUsingTcpFallback => _usingTcpFallback;
    public double LastRttMilliseconds => Volatile.Read(ref _lastRttMs);

    public event Action<bool>? ConnectionStateChanged;
    public event Action<HvReceivedPacket>? PacketReceived;

    static HvSocketTransport()
    {
        WSAStartup(0x0202, out _);
    }

    public HvSocketTransport(Guid targetVmId, Guid serviceId, int tcpFallbackPort, bool forceTcpFallback = false)
    {
        if (targetVmId == Guid.Empty)
        {
            throw new ArgumentException(
                "TargetVmId must be a specific Hyper-V VM GUID, never HV_GUID_WILDCARD (Guid.Empty).",
                nameof(targetVmId));
        }

        TargetVmId = targetVmId;
        ServiceId = serviceId;
        _tcpFallbackPort = tcpFallbackPort;
        _forceTcpFallback = forceTcpFallback;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        _acceptAndReceiveThread = new Thread(WorkerLoop)
        {
            Name = $"HvSocket-{ServiceId.ToString()[..8]}",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _acceptAndReceiveThread.Start();
    }

    public void Stop()
    {
        _running = false;
        CloseActiveClient();
        CloseListeners();

        if (_acceptAndReceiveThread is { IsAlive: true } &&
            Thread.CurrentThread != _acceptAndReceiveThread)
        {
            _acceptAndReceiveThread.Join(1500);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void WorkerLoop()
    {
        bool hvBound = !_forceTcpFallback && TryBindHvSocketListener();
        if (!hvBound)
        {
            _usingTcpFallback = true;
            TryStartTcpFallbackListener();
        }

        long lastPingTick = Environment.TickCount64;

        while (_running)
        {
            try
            {
                if (!_usingTcpFallback)
                {
                    IntPtr accepted = accept(_listenHvSocket, IntPtr.Zero, IntPtr.Zero);
                    if (accepted == INVALID_SOCKET || !_running)
                    {
                        if (_running) Thread.Sleep(200);
                        continue;
                    }

                    int bufSize = 65536;
                    setsockopt(accepted, SOL_SOCKET, SO_SNDBUF, ref bufSize, sizeof(int));
                    setsockopt(accepted, SOL_SOCKET, SO_RCVBUF, ref bufSize, sizeof(int));

                    _clientHvSocket = accepted;
                    SetConnectedState(true);
                    RunReceiveLoop(ref lastPingTick);
                }
                else if (_tcpListener != null)
                {
                    Socket accepted = _tcpListener.AcceptSocket();
                    accepted.NoDelay = true;
                    _tcpClientSocket = accepted;
                    SetConnectedState(true);
                    RunReceiveLoop(ref lastPingTick);
                }
                else
                {
                    Thread.Sleep(500);
                }
            }
            catch
            {
                if (_running) Thread.Sleep(250);
            }
            finally
            {
                CloseActiveClient();
                SetConnectedState(false);
            }
        }
    }

    private bool TryBindHvSocketListener()
    {
        IntPtr sock = socket(AF_HYPERV, SOCK_STREAM, HV_PROTOCOL_RAW);
        if (sock == INVALID_SOCKET)
        {
            return false;
        }

        var addr = new SOCKADDR_HV
        {
            Family = AF_HYPERV,
            Reserved = 0,
            VmId = TargetVmId, // Isolated to this exact VM GUID
            ServiceId = ServiceId
        };

        if (bind(sock, ref addr, Marshal.SizeOf<SOCKADDR_HV>()) != 0)
        {
            int err = WSAGetLastError();
            Debug.WriteLine($"[HvSocketTransport] bind(AF_HYPERV) failed with WSA error {err}. Run Register-HvSocketServices.ps1 to configure SDDL.");
            closesocket(sock);
            return false;
        }

        if (listen(sock, 2) != 0)
        {
            closesocket(sock);
            return false;
        }

        _listenHvSocket = sock;
        _usingTcpFallback = false;
        return true;
    }

    private void TryStartTcpFallbackListener()
    {
        try
        {
            _tcpListener = new TcpListener(IPAddress.Any, _tcpFallbackPort);
            _tcpListener.Start(2);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HvSocketTransport] TCP fallback listener failed on port {_tcpFallbackPort}: {ex.Message}");
            _tcpListener = null;
        }
    }

    private unsafe void RunReceiveLoop(ref long lastPingTick)
    {
        HvPacketHeader header = default;
        byte* headerPtr = (byte*)&header;

        while (_running && _isConnected)
        {
            if (!ReceiveExact(new Span<byte>(headerPtr, HvPacketHeader.Size)))
            {
                break;
            }

            if (header.Magic != HvProtocolConstants.PacketMagic ||
                header.Version != HvProtocolConstants.ProtocolVersion)
            {
                break;
            }

            byte[] payload = header.PayloadLength > 0
                ? new byte[header.PayloadLength]
                : Array.Empty<byte>();

            if (header.PayloadLength > 0 && !ReceiveExact(payload.AsSpan()))
            {
                break;
            }

            var type = (HvPacketType)header.Type;
            if (type == HvPacketType.Pong && payload.Length == HvPingPayload.Size)
            {
                fixed (byte* p = payload)
                {
                    var pong = *(HvPingPayload*)p;
                    ulong nowTicks = (ulong)Stopwatch.GetTimestamp();
                    if (nowTicks >= pong.HostTimestampQpc)
                    {
                        double rttMs = (nowTicks - pong.HostTimestampQpc) * 1000.0 / Stopwatch.Frequency;
                        Volatile.Write(ref _lastRttMs, rttMs);
                    }
                }
                continue;
            }

            if (type == HvPacketType.Ping && payload.Length == HvPingPayload.Size)
            {
                fixed (byte* p = payload)
                {
                    var ping = *(HvPingPayload*)p;
                    SendStruct(HvPacketType.Pong, in ping);
                }
                continue;
            }

            PacketReceived?.Invoke(new HvReceivedPacket
            {
                Header = header,
                Payload = payload
            });

            long nowMs = Environment.TickCount64;
            if (nowMs - lastPingTick >= 2000)
            {
                lastPingTick = nowMs;
                var pingPayload = new HvPingPayload
                {
                    HostTimestampQpc = (ulong)Stopwatch.GetTimestamp(),
                    GuestTimestampQpc = 0
                };
                SendStruct(HvPacketType.Ping, in pingPayload);
            }
        }
    }

    public unsafe bool SendStruct<T>(HvPacketType packetType, in T payload) where T : unmanaged
    {
        if (!_isConnected) return false;

        int payloadSize = sizeof(T);
        Span<byte> packetBuffer = stackalloc byte[HvPacketHeader.Size + payloadSize];

        ref var hdr = ref Unsafe.As<byte, HvPacketHeader>(ref packetBuffer[0]);
        hdr.Magic = HvProtocolConstants.PacketMagic;
        hdr.Version = HvProtocolConstants.ProtocolVersion;
        hdr.Type = (byte)packetType;
        hdr.PayloadLength = (ushort)payloadSize;

        Unsafe.WriteUnaligned(ref packetBuffer[HvPacketHeader.Size], payload);

        lock (_sendLock)
        {
            hdr.Sequence = ++_sequence;
            return SendExact(packetBuffer);
        }
    }

    public bool SendHeaderAndBytes(HvPacketType packetType, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> body)
    {
        if (!_isConnected) return false;

        int totalPayload = prefix.Length + body.Length;
        int totalPacket = HvPacketHeader.Size + totalPayload;

        byte[]? rented = null;
        Span<byte> buffer = totalPacket <= 4096
            ? stackalloc byte[totalPacket]
            : (rented = new byte[totalPacket]);

        ref var hdr = ref Unsafe.As<byte, HvPacketHeader>(ref buffer[0]);
        hdr.Magic = HvProtocolConstants.PacketMagic;
        hdr.Version = HvProtocolConstants.ProtocolVersion;
        hdr.Type = (byte)packetType;
        hdr.PayloadLength = (ushort)totalPayload;

        prefix.CopyTo(buffer.Slice(HvPacketHeader.Size, prefix.Length));
        body.CopyTo(buffer.Slice(HvPacketHeader.Size + prefix.Length, body.Length));

        lock (_sendLock)
        {
            hdr.Sequence = ++_sequence;
            return SendExact(buffer);
        }
    }

    private unsafe bool SendExact(ReadOnlySpan<byte> data)
    {
        if (!_usingTcpFallback)
        {
            IntPtr sock = _clientHvSocket;
            if (sock == INVALID_SOCKET) return false;

            fixed (byte* basePtr = data)
            {
                int offset = 0;
                int remaining = data.Length;
                while (remaining > 0)
                {
                    int sent = send(sock, basePtr + offset, remaining, 0);
                    if (sent <= 0)
                    {
                        return false;
                    }
                    offset += sent;
                    remaining -= sent;
                }
            }
            return true;
        }
        else
        {
            Socket? tcp = _tcpClientSocket;
            if (tcp == null) return false;
            try
            {
                int offset = 0;
                while (offset < data.Length)
                {
                    int sent = tcp.Send(data.Slice(offset), SocketFlags.None);
                    if (sent <= 0) return false;
                    offset += sent;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private unsafe bool ReceiveExact(Span<byte> buffer)
    {
        if (!_usingTcpFallback)
        {
            IntPtr sock = _clientHvSocket;
            if (sock == INVALID_SOCKET) return false;

            fixed (byte* basePtr = buffer)
            {
                int offset = 0;
                int remaining = buffer.Length;
                while (remaining > 0)
                {
                    int read = recv(sock, basePtr + offset, remaining, 0);
                    if (read <= 0)
                    {
                        return false;
                    }
                    offset += read;
                    remaining -= read;
                }
            }
            return true;
        }
        else
        {
            Socket? tcp = _tcpClientSocket;
            if (tcp == null) return false;
            try
            {
                int offset = 0;
                while (offset < buffer.Length)
                {
                    int read = tcp.Receive(buffer.Slice(offset), SocketFlags.None);
                    if (read <= 0) return false;
                    offset += read;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private void SetConnectedState(bool connected)
    {
        if (_isConnected != connected)
        {
            _isConnected = connected;
            ConnectionStateChanged?.Invoke(connected);
        }
    }

    private void CloseActiveClient()
    {
        IntPtr hvClient = Interlocked.Exchange(ref _clientHvSocket, INVALID_SOCKET);
        if (hvClient != INVALID_SOCKET)
        {
            shutdown(hvClient, 2);
            closesocket(hvClient);
        }

        Socket? tcpClient = Interlocked.Exchange(ref _tcpClientSocket, null);
        if (tcpClient != null)
        {
            try { tcpClient.Shutdown(SocketShutdown.Both); } catch { }
            try { tcpClient.Close(); } catch { }
        }
    }

    private void CloseListeners()
    {
        IntPtr hvListen = Interlocked.Exchange(ref _listenHvSocket, INVALID_SOCKET);
        if (hvListen != INVALID_SOCKET)
        {
            closesocket(hvListen);
        }

        TcpListener? tcpListen = Interlocked.Exchange(ref _tcpListener, null);
        if (tcpListen != null)
        {
            try { tcpListen.Stop(); } catch { }
        }
    }
}
