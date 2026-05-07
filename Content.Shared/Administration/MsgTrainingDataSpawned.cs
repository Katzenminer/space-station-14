using Lidgren.Network;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared.Administration;

public sealed class MsgTrainingDataSpawned : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;

    public NetEntity EntityId;
    public int MapId;
    public int ClassId;
    public int SampleIndex;
    public int TotalSamples;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        EntityId = buffer.ReadNetEntity();
        MapId = buffer.ReadVariableInt32();
        ClassId = buffer.ReadVariableInt32();
        SampleIndex = buffer.ReadVariableInt32();
        TotalSamples = buffer.ReadVariableInt32();
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write(EntityId);
        buffer.WriteVariableInt32(MapId);
        buffer.WriteVariableInt32(ClassId);
        buffer.WriteVariableInt32(SampleIndex);
        buffer.WriteVariableInt32(TotalSamples);
    }
}
