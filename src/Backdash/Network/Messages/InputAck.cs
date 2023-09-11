using System.Runtime.InteropServices;
using Backdash.Data;
using Backdash.Serialization;
using Backdash.Serialization.Buffer;

namespace Backdash.Network.Messages;

[StructLayout(LayoutKind.Sequential)]
record struct InputAck : IBinarySerializable
{
    public Frame AckFrame;

    public readonly void Serialize(NetworkBufferWriter writer) => writer.Write(AckFrame.Number);

    public void Deserialize(NetworkBufferReader reader) =>
        AckFrame = new(reader.ReadInt());
}
