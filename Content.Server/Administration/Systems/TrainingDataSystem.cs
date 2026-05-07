using Content.Shared.Administration;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

namespace Content.Server.Administration.Systems;

public sealed class TrainingDataSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RequestDeleteTrainingDataEntityEvent>(OnDeleteRequested);
    }

    private void OnDeleteRequested(RequestDeleteTrainingDataEntityEvent ev, EntitySessionEventArgs args)
    {
        if (TryGetEntity(ev.Entity, out var uid))
            Del(uid);
    }

    public void SendTrainingDataRequest(NetEntity entity, int mapId, int classId, int sampleIndex, int totalSamples, INetChannel channel)
    {
        var ev = new RequestTrainingDataCaptureEvent(entity, mapId, classId, sampleIndex, totalSamples);
        RaiseNetworkEvent(ev, channel);
    }
}
