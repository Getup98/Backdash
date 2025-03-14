using System.Net;
using Backdash.Benchmarks.Network;
using Backdash.Core;

#pragma warning disable CS0649, AsyncFixer01, AsyncFixer02
// ReSharper disable AccessToDisposedClosure
namespace Backdash.Benchmarks.Cases;

[InProcess]
[RPlotExporter]
[MemoryDiagnoser, ExceptionDiagnoser]
[RankColumn, IterationsColumn]
public class UdpClientBenchmark
{
    [Params(1000, 50_000)]
    public int N;

    [Benchmark]
    public async Task SendTest() => await Start(N);

    public async Task Start(int numberOfSpins, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        using var pinger = Factory.CreateUdpClient(9000, out var pingerObservers);
        using var ponger = Factory.CreateUdpClient(9001, out var pongerObservers);

        PingMessageHandler pingerHandler = new("Pinger", pinger);
        PingMessageHandler pongerHandler = new("Ponger", ponger);

        pingerObservers.Add(pingerHandler);
        pongerObservers.Add(pongerHandler);

        using CancellationTokenSource tokenSource = new(timeout.Value);
        var ct = tokenSource.Token;

        void OnProcessed(long count)
        {
            if (count >= numberOfSpins)
                tokenSource.Cancel();
        }

        pingerHandler.OnProcessed += OnProcessed;

        IPEndPoint pongerEndpoint = new(IPAddress.Loopback, 9001);
        var pongerAddress = pongerEndpoint.Serialize();

        ThrowIf.Assert(pinger.TrySendTo(pongerAddress, PingMessage.Ping));

        await Task.WhenAll(
            pinger.Start(ct),
            ponger.Start(ct)
        ).ConfigureAwait(false);

        pingerHandler.OnProcessed -= OnProcessed;
        ThrowIf.Assert(pingerHandler.BadMessages is 0,
            $"** Pinger: {pingerHandler.BadMessages} bad messages");
        ThrowIf.Assert(pingerHandler.ProcessedCount >= numberOfSpins,
            $"** Pinger incomplete (Expected: >= {numberOfSpins}, Received: {pingerHandler.ProcessedCount})");
    }
}
