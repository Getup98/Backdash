using System.Net;
using Backdash.Network.Client;

namespace Backdash.PingTest;

sealed class PingMessageHandler(Memory<byte>? buffer = null) : IUdpObserver<PingMessage>
{
    public static long TotalProcessed => processedCount;

    static long processedCount;

    public async ValueTask OnUdpMessage(
        IUdpClient<PingMessage> sender,
        PingMessage message,
        SocketAddress from,
        CancellationToken stoppingToken
    )
    {
        if (stoppingToken.IsCancellationRequested)
            return;

        Interlocked.Increment(ref processedCount);

        var reply = message switch
        {
            PingMessage.Ping => PingMessage.Pong,
            PingMessage.Pong => PingMessage.Ping,
            _ => throw new ArgumentOutOfRangeException(nameof(message), message, null),
        };

        try
        {
            if (buffer is null)
                await sender.SendTo(from, reply, stoppingToken);
            else
                await sender.SendTo(from, reply, buffer.Value, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // skip
        }
    }
}