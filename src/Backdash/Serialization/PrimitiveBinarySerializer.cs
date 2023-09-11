using System.Numerics;
using Backdash.Core;
using Backdash.Network;

namespace Backdash.Serialization;

sealed class PrimitiveBinarySerializer<T>(bool network)
    : IBinarySerializer<T> where T : unmanaged
{
    public int GetTypeSize() => Mem.SizeOf<T>();

    public T Deserialize(in ReadOnlySpan<byte> data)
    {
        var value = Mem.ReadUnaligned<T>(data);
        return network ? Endianness.ToHostOrder(value) : value;
    }

    public int SerializeScoped(scoped ref T data, Span<byte> buffer)
    {
        var reordered = network ? Endianness.ToNetworkOrder(data) : data;
        return Mem.WriteStruct(reordered, buffer);
    }

    public int Serialize(ref T data, Span<byte> buffer) =>
        SerializeScoped(ref data, buffer);
}

sealed class EnumSerializer<TEnum, TInt>(bool network) : IBinarySerializer<TEnum>
    where TEnum : unmanaged, Enum
    where TInt : unmanaged, IBinaryInteger<TInt>
{
    readonly PrimitiveBinarySerializer<TInt> valueBinarySerializer = new(network);

    public int GetTypeSize() => valueBinarySerializer.GetTypeSize();

    public TEnum Deserialize(in ReadOnlySpan<byte> data)
    {
        var underValue = valueBinarySerializer.Deserialize(in data);
        return Mem.IntegerAsEnum<TEnum, TInt>(underValue);
    }

    public int Serialize(ref TEnum data, Span<byte> buffer)
    {
        var underValue = Mem.EnumAsInteger<TEnum, TInt>(data);
        return valueBinarySerializer.SerializeScoped(ref underValue, buffer);
    }
}
