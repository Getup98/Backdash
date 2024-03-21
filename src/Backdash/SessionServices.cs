using Backdash.Core;
using Backdash.Network.Client;
using Backdash.Serialization;
using Backdash.Sync.Input;
using Backdash.Sync.State;
using Backdash.Sync.State.Stores;

namespace Backdash;

/// <summary>
/// Session dependencies.
/// </summary>
/// <typeparam name="TInput">Input type</typeparam>
/// <typeparam name="TGameState">Game state type</typeparam>
public sealed class SessionServices<TInput, TGameState>
    where TInput : struct
    where TGameState : IEquatable<TGameState>, new()
{
    /// <summary>
    /// Serializer for session input.
    /// </summary>
    public IBinarySerializer<TInput>? InputSerializer { get; set; }

    /// <summary>
    /// Checksum provider service for session state.
    /// </summary>
    public IChecksumProvider<TGameState>? ChecksumProvider { get; set; }

    /// <summary>
    /// Log writer service for session.
    /// </summary>
    public ILogWriter? LogWriter { get; set; }

    /// <summary>
    /// Binary state serializer for session.
    /// When set the default <see cref="IStateStore{TState}"/> will be <see cref="BinaryStateStore{TState}"/>
    /// </summary>
    public IBinarySerializer<TGameState>? StateSerializer { get; set; }

    /// <summary>
    /// Input generator service for session.
    /// </summary>
    public IInputGenerator<TInput>? InputGenerator { get; set; }

    /// <summary>
    /// State store service for session.
    /// </summary>
    public IStateStore<TGameState>? StateStore { get; set; }

    /// <summary>
    /// State store service for session.
    /// </summary>
    public IPeerSocketFactory? PeerSocketFactory { get; set; }

    /// <summary>
    /// Default random service
    /// </summary>
    public Random? Random { get; set; }
}
