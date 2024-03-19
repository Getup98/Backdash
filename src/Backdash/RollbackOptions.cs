using Backdash.Core;
using Backdash.Data;
using Backdash.Network;
using Backdash.Network.Client;
using Backdash.Network.Protocol;
using Backdash.Sync;
using Backdash.Sync.State;

namespace Backdash;

/// <summary>
/// Configurations for sessions.
/// </summary>
///  <seealso cref="RollbackNetcode"/>
///  <seealso cref="IRollbackSession{TInput}"/>
///  <seealso cref="IRollbackSession{TInput,TGameState}"/>
public sealed class RollbackOptions
{
    /// <summary>
    /// Offset to be incremented to spectators <see cref="PlayerHandle.Number"/> when added to session.
    /// </summary>
    /// <seealso cref="PlayerType.Spectator"/>
    /// <seealso cref="IRollbackSession{TInput,TState}.AddPlayer"/>
    /// <inheritdoc cref="Default.SpectatorOffset"/>
    public int SpectatorOffset { get; init; } = Default.SpectatorOffset;

    /// <summary>
    /// Interval for time synchronization notifications.
    /// </summary>
    /// <seealso cref="TimeSync"/>
    /// <seealso cref="TimeSyncOptions"/>
    /// <inheritdoc cref="Default.RecommendationInterval"/>
    public int RecommendationInterval { get; init; } = Default.RecommendationInterval;

    /// <summary>
    /// Forces serialization byte order to network order <see cref="Endianness.BigEndian"/>.
    /// </summary>
    /// <seealso cref="Endianness"/>
    /// <value>Defaults to <see langword="true"/></value>
    public bool NetworkEndianness { get; init; } = true;

    /// <summary>
    /// Max length for player input queues.
    /// </summary>
    /// <inheritdoc cref="Default.InputQueueLength"/>
    public int InputQueueLength { get; init; } = Default.InputQueueLength;

    /// <summary>
    /// Max length for spectators input queues.
    /// </summary>
    /// <inheritdoc cref="Default.InputQueueLength"/>
    public int SpectatorInputBufferLength { get; init; } = Default.InputQueueLength;

    /// <summary>
    /// Max allowed prediction frames.
    /// </summary>
    /// <seealso cref="ResultCode.PredictionThreshold"/>
    /// <inheritdoc cref="Default.PredictionFrames"/>
    public int PredictionFrames { get; init; } = Default.PredictionFrames;

    /// <summary>
    /// Value to be incremented on <see cref="PredictionFrames"/> in state store <see cref="IStateStore{TState}.Initialize"/>
    /// </summary>
    /// <inheritdoc cref="Default.PredictionFramesOffset"/>
    /// <seealso cref="IStateStore{TState}"/>
    public int PredictionFramesOffset { get; init; } = Default.PredictionFramesOffset;

    /// <summary>
    /// Amount of frames to delay for local input
    /// </summary>
    /// <inheritdoc cref="Default.FrameDelay"/>
    public int FrameDelay { get; init; } = Default.FrameDelay;

    /// <summary>
    /// Config <see cref="UdpSocket"/> to use IPv6.
    /// </summary>
    /// <value>Defaults to <see langword="false"/></value>
    public bool UseIPv6 { get; init; }

    /// <summary>
    /// Base FPS used to estimate fairness (frame advantage) over peers.
    /// </summary>
    /// <inheritdoc cref="FrameSpan.DefaultFramesPerSecond"/>
    public short FramesPerSecond { get; init; } = FrameSpan.DefaultFramesPerSecond;

    /// <summary>Logging options. <seealso cref="LogOptions"/></summary>
    public LogOptions Log { get; init; } = new();

    /// <summary>Time synchronization options. <seealso cref="TimeSyncOptions"/></summary>
    public TimeSyncOptions TimeSync { get; init; } = new();

    /// <summary>Networking Protocol options. <seealso cref="ProtocolOptions"/></summary>
    public ProtocolOptions Protocol { get; init; } = new();
}
