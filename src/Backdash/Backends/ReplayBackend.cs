using System.Diagnostics;
using Backdash.Core;
using Backdash.Data;
using Backdash.Network;
using Backdash.Synchronizing.Input.Confirmed;
using Backdash.Synchronizing.Random;

namespace Backdash.Backends;

sealed class ReplayBackend<TInput, TGameState> : IRollbackSession<TInput, TGameState>
    where TInput : unmanaged
    where TGameState : notnull, new()
{
    readonly Logger logger;
    readonly PlayerHandle[] fakePlayers;
    IRollbackHandler<TGameState> callbacks;
    readonly IDeterministicRandom deterministicRandom;
    bool isSynchronizing = true;
    SynchronizedInput<TInput>[] syncInputBuffer = [];

    bool disposed;
    bool closed;

    readonly IReadOnlyList<ConfirmedInputs<TInput>> inputList;

    public ReplayBackend(
        int numberOfPlayers,
        IReadOnlyList<ConfirmedInputs<TInput>> inputList,
        BackendServices<TInput, TGameState> services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        this.inputList = inputList;
        logger = services.Logger;
        deterministicRandom = services.DeterministicRandom;
        NumberOfPlayers = numberOfPlayers;
        fakePlayers = Enumerable.Range(0, numberOfPlayers)
            .Select(x => new PlayerHandle(PlayerType.Remote, x + 1, x)).ToArray();

        callbacks = new EmptySessionHandler<TGameState>(logger);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Close();
        logger.Dispose();
    }

    public void Close()
    {
        if (closed) return;
        closed = true;
        logger.Write(LogLevel.Information, "Shutting down connections");
        callbacks.OnSessionClose();
    }

    public Frame CurrentFrame { get; private set; } = Frame.Zero;
    public FrameSpan RollbackFrames => FrameSpan.Zero;
    public FrameSpan FramesBehind => FrameSpan.Zero;
    public int NumberOfPlayers { get; private set; }
    public int NumberOfSpectators => 0;
    public IDeterministicRandom Random => deterministicRandom;
    public bool IsSpectating => true;
    public void DisconnectPlayer(in PlayerHandle player) { }
    public ResultCode AddLocalInput(PlayerHandle player, TInput localInput) => ResultCode.Ok;
    public IReadOnlyCollection<PlayerHandle> GetPlayers() => fakePlayers;
    public IReadOnlyCollection<PlayerHandle> GetSpectators() => [];

    public void BeginFrame() { }

    public void AdvanceFrame() => logger.Write(LogLevel.Debug, $"[End Frame {CurrentFrame}]");

    public PlayerConnectionStatus GetPlayerStatus(in PlayerHandle player) => PlayerConnectionStatus.Connected;
    public ResultCode AddPlayer(Player player) => ResultCode.NotSupported;

    public IReadOnlyList<ResultCode> AddPlayers(IReadOnlyList<Player> players) =>
        Enumerable.Repeat(ResultCode.NotSupported, players.Count).ToArray();

    public bool GetNetworkStatus(in PlayerHandle player, ref PeerNetworkStats info) => true;

    public void SetFrameDelay(PlayerHandle player, int delayInFrames) { }

    public void Start(CancellationToken stoppingToken = default)
    {
        callbacks.OnSessionStart();
        isSynchronizing = false;
    }

    public Task WaitToStop(CancellationToken stoppingToken = default) => Task.CompletedTask;

    public void SetHandler(IRollbackHandler<TGameState> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        callbacks = handler;
    }

    public ResultCode SynchronizeInputs()
    {
        if (isSynchronizing)
            return ResultCode.NotSynchronized;

        if (CurrentFrame.Number >= inputList.Count)
            return ResultCode.NotSynchronized;

        var confirmed = inputList[CurrentFrame.Number];

        if (confirmed.Count is 0 && CurrentFrame == Frame.Zero)
            return ResultCode.NotSynchronized;

        Trace.Assert(confirmed.Count > 0);
        NumberOfPlayers = confirmed.Count;

        if (syncInputBuffer.Length != NumberOfPlayers)
            Array.Resize(ref syncInputBuffer, NumberOfPlayers);

        for (var i = 0; i < NumberOfPlayers; i++)
            syncInputBuffer[i] = new(confirmed.Inputs[i], false);

        deterministicRandom.UpdateSeed(CurrentFrame.Number);
        CurrentFrame++;

        return ResultCode.Ok;
    }

    public ref readonly SynchronizedInput<TInput> GetInput(int index) =>
        ref syncInputBuffer[index];

    public ref readonly SynchronizedInput<TInput> GetInput(in PlayerHandle player) =>
        ref syncInputBuffer[player.Number - 1];
}
