using System.Net.WebSockets;

sealed class ClientConn
{
    public required WebSocket Ws { get; init; }
    public required string ClientId { get; init; }
    public readonly SemaphoreSlim Slot = new(1, 1);
    public int QueueDepth;
}
