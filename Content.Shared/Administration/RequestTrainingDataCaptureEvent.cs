using System;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared.Administration;

[Serializable, NetSerializable]
public sealed class RequestTrainingDataCaptureEvent : EntityEventArgs
{
    public NetEntity Entity { get; }
    public int MapId { get; }
    public int ClassId { get; }
    public int SampleIndex { get; }
    public int TotalSamples { get; }

    public RequestTrainingDataCaptureEvent(NetEntity entity, int mapId, int classId, int sampleIndex, int totalSamples)
    {
        Entity = entity;
        MapId = mapId;
        ClassId = classId;
        SampleIndex = sampleIndex;
        TotalSamples = totalSamples;
    }
}

[Serializable, NetSerializable]
public sealed class RequestDeleteTrainingDataEntityEvent : EntityEventArgs
{
    public NetEntity Entity { get; }
    public RequestDeleteTrainingDataEntityEvent(NetEntity entity) => Entity = entity;
}
