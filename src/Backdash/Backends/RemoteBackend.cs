using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Backdash.Core;
using Backdash.Data;
using Backdash.Network;
using Backdash.Network.Client;
using Backdash.Network.Messages;
using Backdash.Network.Protocol;
using Backdash.Network.Protocol.Comm;
using Backdash.Serialization;
using Backdash.Synchronizing.Input;
using Backdash.Synchronizing.Input.Confirmed;
using Backdash.Synchronizing.Random;
using Backdash.Synchronizing.State;

namespace Backdash.Backends;

sealed class RemoteBackend<TInput> : INetcodeSession<TInput>, IProtocolNetworkEventHandler
    where TInput : unmanaged
{
    readonly NetcodeOptions options;
    readonly IBinarySerializer<TInput> inputSerializer;
    readonly IBinarySerializer<ConfirmedInputs<TInput>> inputGroupSerializer;
    readonly Logger logger;
    readonly IProtocolClient udp;
    readonly PeerObserverGroup<ProtocolMessage> peerObservers;
    readonly Synchronizer<TInput> synchronizer;
    readonly ConnectionsState localConnections;
    readonly IBackgroundJobManager backgroundJobManager;
    readonly ProtocolInputEventQueue<TInput> peerInputEventQueue;
    readonly IProtocolInputEventPublisher<ConfirmedInputs<TInput>> peerCombinedInputsEventPublisher;
    readonly PeerConnectionFactory peerConnectionFactory;
    readonly List<PeerConnection<ConfirmedInputs<TInput>>> spectators;
    readonly List<PeerConnection<TInput>?> endpoints;
    readonly HashSet<PlayerHandle> addedPlayers = [];
    readonly HashSet<PlayerHandle> addedSpectators = [];
    readonly IInputListener<TInput>? inputListener;
    readonly EqualityComparer<TInput> inputComparer;
    readonly EqualityComparer<ConfirmedInputs<TInput>> inputGroupComparer;
    readonly IDeterministicRandom<TInput> random;

    bool isSynchronizing = true;
    int nextRecommendedInterval;
    Frame nextSpectatorFrame = Frame.Zero;
    Frame nextListenerFrame = Frame.Zero;
    INetcodeSessionHandler callbacks;
    SynchronizedInput<TInput>[] syncInputBuffer = [];
    TInput[] inputBuffer = [];
    Task backgroundJobTask = Task.CompletedTask;
    readonly ushort syncNumber;
    bool disposed;
    bool closed;

    public RemoteBackend(
        int port,
        NetcodeOptions options,
        BackendServices<TInput> services
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FramesPerSecond);
        ThrowIf.ArgumentOutOfBounds(options.SpectatorOffset, min: Max.NumberOfPlayers);

        this.options = options;
        inputSerializer = services.InputSerializer;
        backgroundJobManager = services.JobManager;
        logger = services.Logger;
        inputListener = services.InputListener;
        random = services.DeterministicRandom;
        inputComparer = services.InputComparer;

        inputGroupComparer = ConfirmedInputComparer<TInput>.Create(services.InputComparer);
        syncNumber = services.Random.MagicNumber();
        peerInputEventQueue = new();
        peerCombinedInputsEventPublisher = new ProtocolCombinedInputsEventPublisher<TInput>(peerInputEventQueue);
        inputGroupSerializer = new ConfirmedInputsSerializer<TInput>(inputSerializer);
        localConnections = new(Max.NumberOfPlayers);
        endpoints = new(Max.NumberOfPlayers);
        spectators = [];
        peerObservers = new();
        callbacks = new EmptySessionHandler(logger);

        synchronizer = new(
            this.options,
            logger,
            addedPlayers,
            services.StateStore,
            services.ChecksumProvider,
            localConnections,
            inputComparer
        )
        {
            Callbacks = callbacks,
        };

        udp = services.ProtocolClientFactory.CreateProtocolClient(port, peerObservers);

        peerConnectionFactory = new(
            this,
            services.Random,
            logger,
            udp,
            this.options.Protocol,
            this.options.TimeSync,
            services.StateStore
        );

        backgroundJobManager.Register(udp);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Close();
        udp.Dispose();
        logger.Dispose();
        backgroundJobManager.Dispose();
        inputListener?.Dispose();
    }

    public void Close()
    {
        if (closed) return;
        closed = true;

        logger.Write(LogLevel.Information, "Shutting down connections");

        foreach (var endpoint in endpoints)
            endpoint?.Dispose();

        foreach (var spectator in spectators)
            spectator.Dispose();

        callbacks.OnSessionClose();
    }

    public INetcodeRandom Random => random;
    public Frame CurrentFrame => synchronizer.CurrentFrame;
    public FrameSpan RollbackFrames => synchronizer.RollbackFrames;
    public FrameSpan FramesBehind => synchronizer.FramesBehind;
    public SavedFrame GetCurrentSavedFrame() => synchronizer.GetLastSavedFrame();
    public int NumberOfPlayers => addedPlayers.Count;
    public int NumberOfSpectators => addedSpectators.Count;

    public SessionMode Mode => SessionMode.Remote;

    public IReadOnlyCollection<PlayerHandle> GetPlayers() => addedPlayers;
    public IReadOnlyCollection<PlayerHandle> GetSpectators() => addedSpectators;

    public void Start(CancellationToken stoppingToken = default) =>
        backgroundJobTask = backgroundJobManager.Start(stoppingToken);

    public Task WaitToStop(CancellationToken stoppingToken = default)
    {
        backgroundJobManager.Stop(TimeSpan.Zero);
        return backgroundJobTask.WaitAsync(stoppingToken);
    }

    public ResultCode AddPlayer(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return player switch
        {
            Spectator spectator => AddSpectator(spectator),
            RemotePlayer remote => AddRemotePlayer(remote),
            LocalPlayer local => AddLocalPlayer(local),
            _ => throw new ArgumentOutOfRangeException(nameof(player)),
        };
    }

    public ResultCode AddLocalInput(PlayerHandle player, in TInput localInput)
    {
        GameInput<TInput> input = new()
        {
            Data = localInput,
        };
        if (isSynchronizing)
            return ResultCode.NotSynchronized;

        if (player.Type is not PlayerType.Local)
            return ResultCode.InvalidPlayerHandle;

        if (!IsPlayerKnown(in player))
            return ResultCode.PlayerOutOfRange;

        if (synchronizer.InRollback)
            return ResultCode.InRollback;

        if (!synchronizer.AddLocalInput(in player, ref input))
            return ResultCode.PredictionThreshold;

        // Update the local connect status state to indicate that we've got a confirmed local frame for this player.
        // This must come first so it gets incorporated into the next packet we send.
        if (input.Frame.IsNull)
            return ResultCode.Ok;

        logger.Write(LogLevel.Trace,
            $"setting local connect status for local queue {player.InternalQueue} to {input.Frame}");
        localConnections[player].LastFrame = input.Frame;

        // Send the input to all the remote players.
        var sent = true;
        var eps = CollectionsMarshal.AsSpan(endpoints);
        ref var currEp = ref MemoryMarshal.GetReference(eps);
        ref var limitEp = ref Unsafe.Add(ref currEp, eps.Length);

        while (Unsafe.IsAddressLessThan(ref currEp, ref limitEp))
        {
            if (currEp is not null)
            {
                var result = currEp.SendInput(in input);

                if (result is not SendInputResult.Ok)
                {
                    sent = false;
                    logger.Write(LogLevel.Warning,
                        $"Unable to send input to queue {currEp.Player.InternalQueue}, {result}");
                }
            }

            currEp = ref Unsafe.Add(ref currEp, 1)!;
        }

        if (!sent)
            return ResultCode.InputDropped;

        return ResultCode.Ok;
    }

    bool IsPlayerKnown(in PlayerHandle player) =>
        player.InternalQueue >= 0
        && player.InternalQueue <
        player.Type switch
        {
            PlayerType.Remote => endpoints.Count,
            PlayerType.Spectator => spectators.Count,
            _ => int.MaxValue,
        };

    public bool GetNetworkStatus(in PlayerHandle player, ref PeerNetworkStats info)
    {
        if (!IsPlayerKnown(in player)) return false;
        if (isSynchronizing) return false;
        endpoints[player.InternalQueue]?.GetNetworkStats(ref info);
        return true;
    }

    public void SetHandler(INetcodeSessionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        callbacks = handler;
        synchronizer.Callbacks = handler;
    }

    public void SetFrameDelay(PlayerHandle player, int delayInFrames)
    {
        ThrowIf.ArgumentOutOfBounds(player.InternalQueue, 0, addedPlayers.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(delayInFrames);
        synchronizer.SetFrameDelay(player, delayInFrames);
    }

    ResultCode AddLocalPlayer(in LocalPlayer player)
    {
        if (addedPlayers.Count >= Max.NumberOfPlayers)
            return ResultCode.TooManyPlayers;

        PlayerHandle handle = new(player.Handle.Type, player.Handle.Number, addedPlayers.Count);
        if (!addedPlayers.Add(handle))
            return ResultCode.DuplicatedPlayer;

        player.Handle = handle;
        endpoints.Add(null);

        IncrementInputBufferSize();
        synchronizer.AddQueue(player.Handle);

        return ResultCode.Ok;
    }

    void IncrementInputBufferSize()
    {
        Array.Resize(ref syncInputBuffer, syncInputBuffer.Length + 1);
        Array.Resize(ref inputBuffer, syncInputBuffer.Length);
    }

    ResultCode AddRemotePlayer(RemotePlayer player)
    {
        if (addedPlayers.Count >= Max.NumberOfPlayers)
            return ResultCode.TooManyPlayers;

        PlayerHandle handle = new(player.Handle.Type, player.Handle.Number, addedPlayers.Count);
        if (!addedPlayers.Add(handle))
            return ResultCode.DuplicatedPlayer;

        player.Handle = handle;
        var endpoint = player.EndPoint;
        var protocol = peerConnectionFactory.Create(
            new(player.Handle, endpoint, localConnections, syncNumber),
            inputSerializer, peerInputEventQueue, inputComparer
        );

        peerObservers.Add(protocol.GetUdpObserver());
        endpoints.Add(protocol);
        IncrementInputBufferSize();
        synchronizer.AddQueue(player.Handle);
        logger.Write(LogLevel.Information, $"Adding {player.Handle} at {endpoint}");
        protocol.Synchronize();
        isSynchronizing = true;
        return ResultCode.Ok;
    }

    public IReadOnlyList<ResultCode> AddPlayers(IReadOnlyList<Player> players)
    {
        var result = new ResultCode[players.Count];
        for (var index = 0; index < players.Count; index++)
            result[index] = AddPlayer(players[index]);
        return result;
    }

    ResultCode AddSpectator(Spectator spectator)
    {
        if (spectators.Count >= Max.NumberOfSpectators)
            return ResultCode.TooManySpectators;

        // Currently, we can only add spectators before the game starts.
        if (!isSynchronizing)
            return ResultCode.AlreadySynchronized;

        var queue = spectators.Count;
        PlayerHandle spectatorHandle = new(PlayerType.Spectator, options.SpectatorOffset + queue, queue);
        if (!addedSpectators.Add(spectatorHandle))
            return ResultCode.DuplicatedPlayer;

        spectator.Handle = spectatorHandle;
        var protocol = peerConnectionFactory.Create(
            new(spectatorHandle, spectator.EndPoint, localConnections, syncNumber),
            inputGroupSerializer,
            peerCombinedInputsEventPublisher,
            inputGroupComparer
        );
        peerObservers.Add(protocol.GetUdpObserver());
        spectators.Add(protocol);
        logger.Write(LogLevel.Information, $"Adding {spectator.Handle} at {spectator.EndPoint}");
        protocol.Synchronize();

        return ResultCode.Ok;
    }

    public PlayerConnectionStatus GetPlayerStatus(in PlayerHandle player)
    {
        if (!IsPlayerKnown(in player)) return PlayerConnectionStatus.Unknown;
        if (player.IsLocal()) return PlayerConnectionStatus.Local;
        if (player.IsSpectator()) return spectators[player.InternalQueue].Status.ToPlayerStatus();
        var endpoint = endpoints[player.InternalQueue];
        if (endpoint?.IsRunning == true)
            return localConnections.IsConnected(in player)
                ? PlayerConnectionStatus.Connected
                : PlayerConnectionStatus.Disconnected;
        return endpoint?.Status.ToPlayerStatus() ?? PlayerConnectionStatus.Unknown;
    }

    public ref readonly SynchronizedInput<TInput> GetInput(int index) =>
        ref syncInputBuffer[index];

    public ref readonly SynchronizedInput<TInput> GetInput(in PlayerHandle player) =>
        ref syncInputBuffer[player.InternalQueue];

    public void GetInputs(Span<SynchronizedInput<TInput>> buffer) => syncInputBuffer.CopyTo(buffer);

    public void BeginFrame()
    {
        if (!isSynchronizing)
            logger.Write(LogLevel.Trace, $"[Begin Frame {synchronizer.CurrentFrame}]");

        DoSync();
    }

    public ResultCode SynchronizeInputs()
    {
        if (isSynchronizing)
            return ResultCode.NotSynchronized;
        synchronizer.SynchronizeInputs(syncInputBuffer, inputBuffer);
        random.UpdateSeed(CurrentFrame, inputBuffer);
        return ResultCode.Ok;
    }

    void CheckInitialSync()
    {
        if (!isSynchronizing) return;

        // Check to see if everyone is now synchronized.  If so,
        // go ahead and tell the client that we're ok to accept input.
        var endpointsSpan = CollectionsMarshal.AsSpan(endpoints);

        for (var i = 0; i < endpointsSpan.Length; i++)
            if (endpointsSpan[i] is { IsRunning: false } ep && localConnections.IsConnected(ep.Player))
                return;

        var spectatorsSpan = CollectionsMarshal.AsSpan(spectators);
        for (var i = 0; i < spectatorsSpan.Length; i++)
            if (spectatorsSpan[i] is { IsRunning: false, Status: not ProtocolStatus.Disconnected })
                return;

        for (var i = 0; i < endpointsSpan.Length; i++) endpointsSpan[i]?.Start();
        for (var i = 0; i < spectatorsSpan.Length; i++) spectatorsSpan[i].Start();

        isSynchronizing = false;
        callbacks.OnSessionStart();
    }

    public void AdvanceFrame()
    {
        logger.Write(LogLevel.Trace, $"[End Frame {synchronizer.CurrentFrame}]");
        synchronizer.IncrementFrame();
    }

    void ConsumeProtocolInputEvents()
    {
        while (peerInputEventQueue.TryConsume(out var gameInputEvent))
        {
            var (player, eventInput) = gameInputEvent;
            if (!player.IsRemote())
            {
                logger.Write(LogLevel.Warning, $"non-remote input received from {player}");
                continue;
            }

            if (localConnections[player].Disconnected)
                continue;

            var currentRemoteFrame = localConnections[player].LastFrame;
            var newRemoteFrame = eventInput.Frame;

            ThrowIf.Assert(currentRemoteFrame.IsNull || newRemoteFrame == currentRemoteFrame.Next());
            synchronizer.AddRemoteInput(in player, eventInput);
            // Notify the other endpoints which frame we received from a peer
            logger.Write(LogLevel.Trace, $"setting remote connect status frame {player} to {eventInput.Frame}");
            localConnections[player].LastFrame = eventInput.Frame;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    Span<PeerConnection<TInput>?> UpdateEndpoints()
    {
        var span = CollectionsMarshal.AsSpan(endpoints);
        ref var curr = ref MemoryMarshal.GetReference(span);
        ref var limit = ref Unsafe.Add(ref curr, span.Length);
        while (Unsafe.IsAddressLessThan(ref curr, ref limit))
        {
            curr?.Update();
            curr = ref Unsafe.Add(ref curr, 1)!;
        }

        return span;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    Span<PeerConnection<ConfirmedInputs<TInput>>> UpdateSpectators()
    {
        var span = CollectionsMarshal.AsSpan(spectators);
        ref var curr = ref MemoryMarshal.GetReference(span);
        ref var limit = ref Unsafe.Add(ref curr, span.Length);
        while (Unsafe.IsAddressLessThan(ref curr, ref limit))
        {
            curr.Update();
            curr = ref Unsafe.Add(ref curr, 1)!;
        }

        return span;
    }

    void DoSync()
    {
        backgroundJobManager.ThrowIfError();
        if (synchronizer.InRollback) return;
        ConsumeProtocolInputEvents();

        var eps = UpdateEndpoints();
        var specs = UpdateSpectators();

        if (isSynchronizing) return;

        synchronizer.CheckSimulation();

        // notify all of our endpoints of their local frame number for their next connection quality report
        int i;
        var currentFrame = synchronizer.CurrentFrame;
        for (i = 0; i < eps.Length; i++)
            eps[i]?.SetLocalFrameNumber(currentFrame, options.FramesPerSecond);

        var minConfirmedFrame = NumberOfPlayers <= 2 ? MinimumFrame2Players() : MinimumFrameNPlayers();
        ThrowIf.Assert(minConfirmedFrame != Frame.MaxValue);
        logger.Write(LogLevel.Trace, $"last confirmed frame in p2p backend is {minConfirmedFrame}");

        if (minConfirmedFrame >= Frame.Zero)
        {
            if (NumberOfSpectators > 0)
            {
                GameInput<ConfirmedInputs<TInput>> confirmed = new(nextSpectatorFrame);
                while (nextSpectatorFrame <= minConfirmedFrame)
                {
                    if (!synchronizer.GetConfirmedInputGroup(in nextSpectatorFrame, ref confirmed))
                        break;

                    logger.Write(LogLevel.Trace, $"pushing frame {nextSpectatorFrame} to spectators");
                    for (var s = 0; s < specs.Length; s++)
                        if (specs[s].IsRunning)
                            specs[s].SendInput(in confirmed);

                    nextSpectatorFrame++;
                }
            }

            if (inputListener is not null)
            {
                GameInput<ConfirmedInputs<TInput>> confirmed = new(nextListenerFrame);
                while (nextListenerFrame <= minConfirmedFrame)
                {
                    if (!synchronizer.GetConfirmedInputGroup(in nextListenerFrame, ref confirmed))
                        break;

                    logger.Write(LogLevel.Trace, $"pushing frame {nextListenerFrame} to listener");
                    inputListener.OnConfirmed(in confirmed.Frame, in confirmed.Data);

                    nextListenerFrame++;
                }
            }

            logger.Write(LogLevel.Trace, $"setting confirmed frame in sync to {minConfirmedFrame}");
            synchronizer.SetLastConfirmedFrame(minConfirmedFrame);
        }

        // send time sync notifications if now is the proper time
        if (currentFrame.Number <= nextRecommendedInterval)
            return;

        var interval = 0;
        for (i = 0; i < eps.Length; i++)
            if (eps[i] is { } endpoint)
                interval = Math.Max(interval, endpoint.GetRecommendFrameDelay());

        if (interval <= 0) return;
        callbacks.TimeSync(new(interval));
        nextRecommendedInterval = currentFrame.Number + options.RecommendationInterval;
    }

    void IProtocolNetworkEventHandler.OnNetworkEvent(in ProtocolEventInfo evt)
    {
        ref readonly var player = ref evt.Player;
        switch (evt.Type)
        {
            case ProtocolEvent.Connected:
                callbacks.OnPeerEvent(player, new(PeerEvent.Connected));
                break;
            case ProtocolEvent.Synchronizing:
                callbacks.OnPeerEvent(player, new(PeerEvent.Synchronizing)
                {
                    Synchronizing = new(evt.Synchronizing.CurrentStep, evt.Synchronizing.TotalSteps),
                });
                break;
            case ProtocolEvent.Synchronized:
                callbacks.OnPeerEvent(player, new(PeerEvent.Synchronized)
                {
                    Synchronized = new(evt.Synchronized.Ping),
                });
                CheckInitialSync();
                break;
            case ProtocolEvent.SyncFailure:
                if (player.IsSpectator())
                {
                    spectators[player.InternalQueue].Disconnect();
                    addedSpectators.Remove(player);
                    CheckInitialSync();
                }
                else
                    callbacks.OnPeerEvent(player, new(PeerEvent.SynchronizationFailure));

                break;
            case ProtocolEvent.NetworkInterrupted:
                callbacks.OnPeerEvent(player, new(PeerEvent.ConnectionInterrupted)
                {
                    ConnectionInterrupted = new(evt.NetworkInterrupted.DisconnectTimeout),
                });
                break;
            case ProtocolEvent.NetworkResumed:
                callbacks.OnPeerEvent(player, new(PeerEvent.ConnectionResumed));
                break;
            case ProtocolEvent.Disconnected:
                if (player.Type is PlayerType.Spectator)
                {
                    spectators[player.InternalQueue].Disconnect();
                    addedSpectators.Remove(player);
                }

                if (player.Type is PlayerType.Remote)
                    DisconnectPlayer(player);
                callbacks.OnPeerEvent(player, new(PeerEvent.Disconnected));
                break;
            default:
                logger.Write(LogLevel.Warning, $"Unknown protocol event {evt} from {player}");
                break;
        }
    }

    Frame MinimumFrame2Players()
    {
        // discard confirmed frames as appropriate
        Frame totalMinConfirmed = Frame.MaxValue;
        var eps = CollectionsMarshal.AsSpan(endpoints);
        for (var i = 0; i < eps.Length; i++)
        {
            var queueConnected = true;
            if (eps[i] is { IsRunning: true } endpoint)
                queueConnected = endpoint.GetPeerConnectStatus(i, out _);

            ref var localConn = ref localConnections[i];

            if (!localConn.Disconnected)
                totalMinConfirmed = Frame.Min(in localConnections[i].LastFrame, in totalMinConfirmed);

            logger.Write(LogLevel.Trace,
                $"Queue {i} => connected: {!localConn.Disconnected}; last received: {localConn.LastFrame}; min confirmed: {totalMinConfirmed}");

            if (!queueConnected && !localConn.Disconnected && eps[i] is { Player: var handler })
            {
                logger.Write(LogLevel.Information, $"disconnecting {i} by remote request");
                DisconnectPlayerQueue(in handler, in totalMinConfirmed);
            }

            logger.Write(LogLevel.Trace, $"Queue {i} => min confirmed = {totalMinConfirmed}");
        }

        return totalMinConfirmed;
    }

    Frame MinimumFrameNPlayers()
    {
        // discard confirmed frames as appropriate
        var totalMinConfirmed = Frame.MaxValue;
        var eps = CollectionsMarshal.AsSpan(endpoints);
        for (var queue = 0; queue < NumberOfPlayers; queue++)
        {
            var queueConnected = true;
            var queueMinConfirmed = Frame.MaxValue;
            logger.Write(LogLevel.Trace, $"considering queue {queue}");
            for (var i = 0; i < eps.Length; i++)
            {
                // we're going to do a lot of logic here in consideration of endpoint i.
                // keep accumulating the minimum confirmed point for all n*n packets and
                // throw away the rest.
                if (eps[i] is { IsRunning: true } endpoint)
                {
                    var connected = endpoint.GetPeerConnectStatus(queue, out var lastReceived);
                    queueConnected = queueConnected && connected;
                    queueMinConfirmed = Frame.Min(in lastReceived, in queueMinConfirmed);
                    logger.Write(LogLevel.Trace,
                        $"Queue {i} => connected: {connected}; last received: {lastReceived}; min confirmed: {queueMinConfirmed}");
                }
                else
                    logger.Write(LogLevel.Trace, $"Queue {i}: ignoring... not running.");
            }

            ref var localStatus = ref localConnections[queue];
            // merge in our local status only if we're still connected!
            if (!localStatus.Disconnected)
                queueMinConfirmed = Frame.Min(in localStatus.LastFrame, in queueMinConfirmed);
            logger.Write(LogLevel.Trace,
                $"[Endpoint {queue}]: connected = {!localStatus.Disconnected}; last received = {localStatus.LastFrame}, queue min confirmed = {queueMinConfirmed}");
            if (queueConnected)
                totalMinConfirmed = Frame.Min(in queueMinConfirmed, in totalMinConfirmed);
            else
            {
                // check to see if this disconnect notification is further back than we've been before.  If
                // so, we need to re-adjust.  This can happen when we detect our own disconnect at frame n
                // and later receive a disconnect notification for frame n-1.
                if ((!localStatus.Disconnected || localStatus.LastFrame > queueMinConfirmed)
                    && eps[queue] is { Player: var handle })
                {
                    logger.Write(LogLevel.Information, $"disconnecting queue {queue} by remote request");
                    DisconnectPlayerQueue(in handle, in queueMinConfirmed);
                }
            }

            logger.Write(LogLevel.Trace, $"[Endpoint {queue}] total min confirmed = {totalMinConfirmed}");
        }

        return totalMinConfirmed;
    }

    void DisconnectPlayerQueue(in PlayerHandle player, in Frame syncTo)
    {
        var frameCount = synchronizer.CurrentFrame;
        endpoints[player.InternalQueue]?.Disconnect();
        ref var connStatus = ref localConnections[player];
        logger.Write(LogLevel.Debug,
            $"Changing player {player} local connect status for last frame from {connStatus.LastFrame.Number} to {syncTo} on disconnect request (current: {frameCount})");
        connStatus.Disconnected = true;
        connStatus.LastFrame = syncTo;
        if (syncTo < frameCount && !syncTo.IsNull)
        {
            logger.Write(LogLevel.Information,
                $"adjusting simulation to account for the fact that {player} disconnected on frame {syncTo}");
            synchronizer.AdjustSimulation(in syncTo);
            logger.Write(LogLevel.Information, "finished adjusting simulation.");
        }

        callbacks.OnPeerEvent(player, new(PeerEvent.Disconnected));
        CheckInitialSync();
    }

    public bool LoadFrame(in Frame frame)
    {
        if (frame.IsNull || frame == CurrentFrame)
        {
            logger.Write(LogLevel.Trace, "Skipping NOP.");
            return true;
        }

        try
        {
            synchronizer.LoadFrame(in frame);
            return true;
        }
        catch (NetcodeException)
        {
            return false;
        }
    }

    public void DisconnectPlayer(in PlayerHandle player)
    {
        if (!IsPlayerKnown(in player)) return;
        if (localConnections[player].Disconnected) return;
        if (player.Type is not PlayerType.Remote) return;
        if (endpoints[player.InternalQueue] is null)
        {
            var currentFrame = synchronizer.CurrentFrame;
            logger.Write(LogLevel.Information,
                $"Disconnecting {player} at frame {localConnections[player].LastFrame} by user request.");
            for (int i = 0; i < endpoints.Count; i++)
                if (endpoints[i] is not null)
                    DisconnectPlayerQueue(new(PlayerType.Remote, i), currentFrame);
        }
        else
        {
            logger.Write(LogLevel.Information,
                $"Disconnecting {player} at frame {localConnections[player].LastFrame} by user request.");
            DisconnectPlayerQueue(player, localConnections[player].LastFrame);
        }
    }
}
