using System.Text;
using Backdash.Serialization;

namespace Backdash.Tests.Utils.Network;

public readonly record struct StringValue(string Value)
{
    public static implicit operator string(StringValue value) => value.Value;
    public static implicit operator StringValue(string value) => new(value);
}

class StringBinarySerializer : IBinarySerializer<StringValue>
{
    public int Serialize(ref StringValue data, Span<byte> buffer) =>
        Encoding.UTF8.GetBytes(data.Value, buffer);

    public StringValue Deserialize(in ReadOnlySpan<byte> data) =>
        Encoding.UTF8.GetString(data);
}
