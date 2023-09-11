using System.Runtime.CompilerServices;
using System.Text;
using Backdash.Core;
using Backdash.Data;

namespace Backdash.Input;

[InlineArray(Capacity), Serializable]
public struct GameInputBuffer
{
    byte element0;

    public const int Capacity = Max.InputBytes * Max.InputPlayers;

    public GameInputBuffer(ReadOnlySpan<byte> bits) => bits.CopyTo(this);

    public override readonly string ToString() =>
        Mem.GetBitString(this, splitAt: Max.InputBytes);

    public readonly bool Equals(GameInputBuffer other) =>
        Mem.SpanEqual<byte>(this, other, truncate: true);

    public static Span<byte> ForPlayer(ref GameInputBuffer buffer, int inputSize, int playerIndex)
    {
        var byteIndex = playerIndex * inputSize;
        return buffer[byteIndex..(byteIndex + inputSize)];
    }

    internal static Span<byte> ForPlayer(ref GameInputBuffer buffer, int playerIndex) =>
        ForPlayer(ref buffer, Max.InputBytes, playerIndex);
}

struct GameInput : IEquatable<GameInput>
{
    public Frame Frame { get; set; } = Frame.Null;

    public GameInputBuffer Buffer;

    public int Size { get; set; }

    public static GameInput CreateEmpty() => new();
    public static GameInput OfSize(int size) => new(size);

    public GameInput(in GameInputBuffer inputBuffer, int size)
    {
        ReadOnlySpan<byte> bits = inputBuffer;
        Tracer.Assert(bits.Length <= Max.InputBytes * Max.MsgPlayers);
        Tracer.Assert(bits.Length > 0);
        Size = size;
        Buffer = inputBuffer;
    }

    public GameInput(int size) : this(new(), size) { }

    public GameInput() : this(0) { }

    public GameInput(ReadOnlySpan<byte> bits) : this(new GameInputBuffer(bits), bits.Length) { }

    public readonly bool IsEmpty => Size is 0;
    public void IncrementFrame() => Frame = Frame.Next;
    public void ResetFrame() => Frame = Frame.Null;

#pragma warning disable IDE0251
    public void Erase() => Mem.Clear(Buffer);
#pragma warning restore IDE0251

    public void ForPlayer(int playerIndex, Span<byte> playerInput)
    {
        Tracer.Assert(playerInput.Length >= Size);
        Tracer.Assert(playerIndex <= Max.InputPlayers);

        GameInputBuffer
            .ForPlayer(ref Buffer, Size, playerIndex)
            .CopyTo(playerInput);
    }

    public override readonly string ToString()
    {
        StringBuilder builder = new();
        builder.Append($"{{ Frame: {Frame},");
        builder.Append($" Size: {Size}, Input: ");
        builder.Append(Buffer.ToString());
        builder.Append(" }");
        return builder.ToString();
    }

    public readonly bool Equals(in GameInput other, bool bitsOnly, ILogger? logger = null)
    {
        var sameBits = Buffer.Equals(other.Buffer);

        if (logger is { EnabledLevel: >= LogLevel.Trace })
        {
            if (!bitsOnly && Frame != other.Frame)
                logger.Trace($"frames don't match: {Frame}, {other.Frame}");

            if (Size != other.Size)
                logger.Trace($"sizes don't match: {Size}, {other.Size}");

            if (!sameBits)
                logger.Trace($"bits don't match");
        }


        return (bitsOnly || Frame == other.Frame)
               && Size == other.Size
               && sameBits;
    }

    public readonly bool Equals(GameInput other) => Equals(other, false);
    public override readonly bool Equals(object? obj) => obj is GameInput gi && Equals(gi);

    // ReSharper disable once BaseObjectGetHashCodeCallInGetHashCode
    public override readonly int GetHashCode() => base.GetHashCode();

    public static bool operator ==(GameInput a, GameInput b) => a.Equals(b);
    public static bool operator !=(GameInput a, GameInput b) => !(a == b);
}
