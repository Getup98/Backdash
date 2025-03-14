using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Backdash.Core;
using Backdash.Data;
using Backdash.Network;

namespace Backdash.Serialization;

/// <summary>
/// Binary span writer.
/// </summary>
[DebuggerDisplay("Written: {WrittenCount}")]
public readonly ref struct BinaryRawBufferWriter
{
    /// <summary>
    /// Initialize a new <see cref="BinaryRawBufferWriter"/> for <paramref name="buffer"/>
    /// </summary>
    /// <param name="buffer">Byte buffer to be written</param>
    /// <param name="offset">Write offset reference</param>
    /// <param name="endianness">Serialization endianness</param>
    public BinaryRawBufferWriter(
        scoped in Span<byte> buffer,
        ref int offset,
        Endianness? endianness = null
    )
    {
        this.buffer = buffer;
        this.offset = ref offset;
        Endianness = endianness ?? Platform.Endianness;
    }

    readonly ref int offset;
    readonly Span<byte> buffer;

    /// <summary>Gets or init the value to define which endianness should be used for serialization.</summary>
    public readonly Endianness Endianness;

    /// <summary>Total written byte count.</summary>
    public int WrittenCount => offset;

    /// <summary>Total buffer capacity in bytes.</summary>
    public int Capacity => buffer.Length;

    /// <summary>Available buffer space in bytes</summary>
    public int FreeCapacity => Capacity - WrittenCount;

    /// <summary>Returns a <see cref="Span{Byte}"/> for the current available buffer.</summary>
    public Span<byte> CurrentBuffer => buffer[offset..];

    /// <summary>Advance write pointer by <paramref name="count"/>.</summary>
    public void Advance(int count) => offset += count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void WriteSpan<T>(in ReadOnlySpan<T> data) where T : unmanaged => Write(MemoryMarshal.AsBytes(data));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    Span<T> AllocSpan<T>(in ReadOnlySpan<T> value) where T : unmanaged
    {
        var sizeBytes = Unsafe.SizeOf<T>() * value.Length;
        var result = MemoryMarshal.Cast<byte, T>(buffer.Slice(offset, sizeBytes));
        Advance(sizeBytes);
        return result;
    }

    /// <summary>Writes single <see cref="byte"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in byte value) => buffer[offset++] = value;

    /// <summary>Writes single <see cref="sbyte"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in sbyte value) => buffer[offset++] = unchecked((byte)value);

    /// <summary>Writes single <see cref="bool"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in bool value)
    {
        if (!BitConverter.TryWriteBytes(CurrentBuffer, value))
            throw new NetcodeException("Destination is too short");
        Advance(sizeof(bool));
    }

    /// <summary>Writes single <see cref="short"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in short value) => WriteNumber(in value);

    /// <summary>Writes single <see cref="ushort"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ushort value) => WriteNumber(in value);

    /// <summary>Writes single <see cref="int"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in int value) => WriteNumber(in value);

    /// <summary>Writes single <see cref="uint"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in uint value) => WriteNumber(in value);

    /// <summary>Writes single <see cref="char"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in char value) => Write((ushort)value);

    /// <summary>Writes single <see cref="long"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in long value) => WriteNumber(in value);

    /// <summary>Writes single <see cref="ulong"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ulong value) => WriteNumber(in value);

    /// <summary>Writes single <see cref="Int128"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in Int128 value) => WriteNumber(in value);

    /// <summary>Writes single <see cref="UInt128"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in UInt128 value) => WriteNumber(in value);

    /// <summary>Writes single <see cref="Half"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in Half value) => Write(BitConverter.HalfToInt16Bits(value));

    /// <summary>Writes single <see cref="float"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in float value) => Write(BitConverter.SingleToInt32Bits(value));

    /// <summary>Writes single <see cref="double"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in double value) => Write(BitConverter.DoubleToInt64Bits(value));

    /// <summary>Writes a span of <see cref="byte"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ReadOnlySpan<byte> value)
    {
        value.CopyTo(CurrentBuffer);
        Advance(value.Length);
    }

    /// <summary>Writes a span of <see cref="sbyte"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ReadOnlySpan<sbyte> value) => WriteSpan(in value);

    /// <summary>Writes a span of <see cref="bool"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ReadOnlySpan<bool> value) => WriteSpan(in value);

    /// <summary>Writes a span of <see cref="short"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ReadOnlySpan<short> value)
    {
        if (Endianness != Platform.Endianness)
            BinaryPrimitives.ReverseEndianness(value, AllocSpan(in value));
        else
            WriteSpan(in value);
    }

    /// <summary>Writes a span of <see cref="ushort"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ReadOnlySpan<ushort> value)
    {
        if (Endianness != Platform.Endianness)
            BinaryPrimitives.ReverseEndianness(value, AllocSpan(in value));
        else
            WriteSpan(in value);
    }

    /// <summary>Writes a span of <see cref="char"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ReadOnlySpan<char> value) => Write(MemoryMarshal.Cast<char, ushort>(value));

    /// <summary>Writes a span of <see cref="int"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ReadOnlySpan<int> value)
    {
        if (Endianness != Platform.Endianness)
            BinaryPrimitives.ReverseEndianness(value, AllocSpan(in value));
        else
            WriteSpan(in value);
    }

    /// <summary>Writes a span of <see cref="uint"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ReadOnlySpan<uint> value)
    {
        if (Endianness != Platform.Endianness)
            BinaryPrimitives.ReverseEndianness(value, AllocSpan(in value));
        else
            WriteSpan(in value);
    }

    /// <summary>Writes a span of <see cref="long"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ReadOnlySpan<long> value)
    {
        if (Endianness != Platform.Endianness)
            BinaryPrimitives.ReverseEndianness(value, AllocSpan(in value));
        else
            WriteSpan(in value);
    }

    /// <summary>Writes a span of <see cref="ulong"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ReadOnlySpan<ulong> value)
    {
        if (Endianness != Platform.Endianness)
            BinaryPrimitives.ReverseEndianness(value, AllocSpan(in value));
        else
            WriteSpan(in value);
    }

    /// <summary>Writes a span of <see cref="Int128"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ReadOnlySpan<Int128> value)
    {
        if (Endianness != Platform.Endianness)
            BinaryPrimitives.ReverseEndianness(value, AllocSpan(in value));
        else
            WriteSpan(in value);
    }

    /// <summary>Writes a span of <see cref="UInt128"/> <paramref name="value"/> into buffer.</summary>
    public void Write(in ReadOnlySpan<UInt128> value)
    {
        if (Endianness != Platform.Endianness)
            BinaryPrimitives.ReverseEndianness(value, AllocSpan(in value));
        else
            WriteSpan(in value);
    }


    /// <summary>Reinterprets the <paramref name="value"/> as <see cref="int"/> and writes it into buffer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(in Frame value) =>
        Write(in Unsafe.As<Frame, int>(ref Unsafe.AsRef(in value)));

    /// <summary>Writes an <see cref="string"/> <paramref name="value"/> into buffer as UTF8.</summary>
    public void WriteUtf8String(in ReadOnlySpan<char> value) =>
        Advance(System.Text.Encoding.UTF8.GetBytes(value, CurrentBuffer));

    /// <summary>Writes an unmanaged struct into buffer.</summary>
    public void WriteStruct<T>(in T value) where T : unmanaged
    {
        MemoryMarshal.Write(CurrentBuffer, in value);
        Advance(Unsafe.SizeOf<T>());
    }

    /// <summary>Writes an unmanaged struct span into buffer.</summary>
    public void WriteStruct<T>(ReadOnlySpan<T> values) where T : unmanaged => Write(MemoryMarshal.AsBytes(values));

    /// <summary>Writes an unmanaged struct span into buffer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteStruct<T>(in T[] values) where T : unmanaged => WriteStruct<T>(values.AsSpan());

    /// <summary>Writes a <see cref="IBinaryInteger{T}"/> <paramref name="value"/> into buffer.</summary>
    /// <typeparam name="T">A numeric type that implements <see cref="IBinaryInteger{T}"/>.</typeparam>
    public void WriteNumber<T>(in T value) where T : unmanaged, IBinaryInteger<T>
    {
        ref var valueRef = ref Unsafe.AsRef(in value);
        int size;
        switch (Endianness)
        {
            case Endianness.LittleEndian:
                valueRef.TryWriteLittleEndian(CurrentBuffer, out size);
                break;
            case Endianness.BigEndian:
                valueRef.TryWriteBigEndian(CurrentBuffer, out size);
                break;
            default:
                return;
        }

        Advance(size);
    }
}
