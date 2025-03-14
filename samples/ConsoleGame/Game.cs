using System.Diagnostics;
using Backdash;
using Backdash.Data;
using Backdash.Serialization;
using Backdash.Serialization.Numerics;

namespace ConsoleGame;

public sealed class Game : INetcodeSessionHandler
{
    // Rollback Net-code session callbacks
    readonly INetcodeSession<GameInput> session;

    readonly CancellationTokenSource cancellation;

    // State that does not affect the game
    readonly NonGameState nonGameState;

    // Draw the game state on console
    readonly View view;

    // Actual game state
    GameState currentState = GameLogic.InitialState();

    public Game(INetcodeSession<GameInput> session, CancellationTokenSource cancellation)
    {
        view = new();
        this.session = session;
        this.cancellation = cancellation;
        var players = session.GetPlayers();
        nonGameState =
            players.Any(x => x.IsLocal())
                ? new()
                {
                    LocalPlayer = players.Single(x => x.IsLocal()),
                    RemotePlayer = players.Single(x => x.IsRemote()),
                    SessionInfo = session,
                }
                : new()
                {
                    LocalPlayer = null,
                    RemotePlayer = default,
                    SessionInfo = session,
                };
    }

    // Game Loop
    public void Update()
    {
        session.BeginFrame();

        if (nonGameState.IsRunning)
            UpdatePlayers();

        session.GetNetworkStatus(nonGameState.RemotePlayer, ref nonGameState.PeerNetworkStats);
        view.Draw(in currentState, nonGameState);
    }

    void UpdatePlayers()
    {
        if (nonGameState.LocalPlayer is { } localPlayer)
        {
            var localInput = GameLogic.ReadKeyboardInput(out var disconnectRequest);
            if (disconnectRequest)
                cancellation.Cancel();

            var result = session.AddLocalInput(localPlayer, localInput);
            if (result is not ResultCode.Ok)
            {
                Log($"UNABLE TO ADD INPUT: {result}");
                nonGameState.LastError = @$"{result} {DateTime.Now:mm\:ss\.fff}";
                return;
            }
        }

        var syncResult = session.SynchronizeInputs();
        if (syncResult is not ResultCode.Ok)
        {
            Log($"UNABLE SYNC INPUTS: {syncResult}");
            nonGameState.LastError = @$"{syncResult} {DateTime.Now:mm\:ss\.fff}";
            return;
        }

        var (input1, input2) = (session.GetInput(0), session.GetInput(1));
        GameLogic.AdvanceState(session.Random, ref currentState, input1, input2);
        session.AdvanceFrame();
    }

    static void Log(string message) =>
#pragma warning disable S6670
        Trace.WriteLine($"{DateTime.UtcNow:hh:mm:ss.zzz} GAME => {message}");
#pragma warning restore S6670

    // Session Callbacks
    public void OnSessionStart()
    {
        Log("GAME STARTED");
        nonGameState.IsRunning = true;
        nonGameState.RemotePlayerStatus = PlayerStatus.Running;
    }

    public void OnSessionClose()
    {
        Log("GAME CLOSED");
        nonGameState.IsRunning = false;
        nonGameState.RemotePlayerStatus = PlayerStatus.Disconnected;
    }

    public void SaveState(in Frame frame, ref readonly BinaryBufferWriter writer)
    {
        writer.Write(currentState.Position1);
        writer.Write(currentState.Position2);
        writer.Write(currentState.Score1);
        writer.Write(currentState.Score2);
        writer.Write(currentState.Target);
    }

    public void LoadState(in Frame frame, ref readonly BinaryBufferReader reader)
    {
        currentState.Position1 = reader.ReadVector2();
        currentState.Position2 = reader.ReadVector2();
        currentState.Score1 = reader.ReadInt32();
        currentState.Score2 = reader.ReadInt32();
        currentState.Target = reader.ReadVector2();
    }

    public void TimeSync(FrameSpan framesAhead)
    {
        Console.SetCursorPosition(1, 0);
        Console.WriteLine("> Syncing...");
        Thread.Sleep(framesAhead.Duration());
    }

    public void OnPeerEvent(PlayerHandle player, PeerEventInfo evt)
    {
        Log($"PEER EVENT: {evt} from {player}");
        if (player.IsSpectator())
            return;
        switch (evt.Type)
        {
            case PeerEvent.Connected:
                nonGameState.RemotePlayerStatus = PlayerStatus.Synchronizing;
                break;
            case PeerEvent.Synchronizing:
                nonGameState.SyncProgress =
                    evt.Synchronizing.CurrentStep / (float)evt.Synchronizing.TotalSteps;
                break;
            case PeerEvent.Synchronized:
                nonGameState.SyncProgress = 1;
                break;
            case PeerEvent.ConnectionInterrupted:
                nonGameState.RemotePlayerStatus = PlayerStatus.Waiting;
                nonGameState.LostConnectionTime = DateTime.UtcNow;
                nonGameState.DisconnectTimeout = evt.ConnectionInterrupted.DisconnectTimeout;
                break;
            case PeerEvent.ConnectionResumed:
                nonGameState.RemotePlayerStatus = PlayerStatus.Running;
                break;
            case PeerEvent.Disconnected:
                nonGameState.RemotePlayerStatus = PlayerStatus.Disconnected;
                nonGameState.IsRunning = false;
                break;
        }
    }

    public void AdvanceFrame()
    {
        session.SynchronizeInputs();
        var (input1, input2) = (session.GetInput(0), session.GetInput(1));
        GameLogic.AdvanceState(session.Random, ref currentState, input1, input2);
        session.AdvanceFrame();
    }
}
