// ============================================================================
// IHvTransport.cs — Host Transport Abstraction Interface
// Project: VMStream — Host Console Client (HvConsole.Host)
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using HvConsole.Host.Protocol;

namespace HvConsole.Host.Transport;

public readonly struct HvReceivedPacket
{
    public HvPacketHeader Header { get; init; }
    public byte[] Payload { get; init; }
}

public interface IHvTransport : IDisposable
{
    Guid TargetVmId { get; }
    Guid ServiceId { get; }
    bool IsConnected { get; }
    bool IsUsingTcpFallback { get; }
    double LastRttMilliseconds { get; }

    event Action<bool>? ConnectionStateChanged;
    event Action<HvReceivedPacket>? PacketReceived;

    void Start();
    void Stop();

    bool SendStruct<T>(HvPacketType packetType, in T payload) where T : unmanaged;
    bool SendHeaderAndBytes(HvPacketType packetType, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> body);
}
