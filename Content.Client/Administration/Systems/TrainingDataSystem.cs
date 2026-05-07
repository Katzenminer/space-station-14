using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Content.Shared.Administration;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client.Administration.Systems;

internal sealed class TrainingDataSystem : EntitySystem
{
    [Dependency] private readonly IClyde _clyde = default!;
    [Dependency] private readonly IResourceManager _resMan = default!;
    [Dependency] private readonly IEyeManager _eyeMan = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private static readonly ResPath TrainingDataPath = new("/TrainingData");

    private readonly Queue<(NetEntity Entity, MapId Map, int ClassId)> _pendingEntities = new();
    private bool _isCapturing;

    private int _currentSample;
    private int _currentAngleIndex;
    private int _currentDistanceIndex;
    private int _classId;

    private static readonly Angle[] Angles =
    {
        Angle.Zero,
        new(Math.PI / 4),
        new(Math.PI / 2),
        new(3 * Math.PI / 4),
        Math.PI,
        new(5 * Math.PI / 4),
        new(3 * Math.PI / 2),
        new(7 * Math.PI / 4)
    };

    private static readonly float[] Distances = { 3f, 5f, 7f, 10f, 15f };

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RequestTrainingDataCaptureEvent>(OnTrainingDataCaptureRequested);
    }

    private void OnTrainingDataCaptureRequested(RequestTrainingDataCaptureEvent ev)
    {
        var log = Logger.GetSawmill("trainingdata");
        log.Info($"Received request for entity {ev.Entity} on map {ev.MapId}");
        _pendingEntities.Enqueue((ev.Entity, new MapId(ev.MapId), ev.ClassId));

        if (!_isCapturing)
        {
            _isCapturing = true;
            _currentSample = 0;
            _resMan.UserData.CreateDir(TrainingDataPath);
            _resMan.UserData.CreateDir(TrainingDataPath / "images");
            _resMan.UserData.CreateDir(TrainingDataPath / "labels");
            _ = ProcessNextEntity();
        }
    }

    private async Task ProcessNextEntity()
    {
        var log = Logger.GetSawmill("trainingdata");
        log.Info($"ProcessNextEntity started. Pending: {_pendingEntities.Count}");
        while (_pendingEntities.Count > 0)
        {
            var (netEntity, mapId, classId) = _pendingEntities.Dequeue();
            log.Info($"Dequeued entity {netEntity}, map {mapId}, class {classId}");

            var entity = GetEntity(netEntity);
            log.Info($"GetEntity returned: {entity}, exists: {EntityManager.EntityExists(entity)}");
            if (!EntityManager.EntityExists(entity) || !TryComp<TransformComponent>(entity, out var xform) || !xform.Running)
            {
                log.Warning($"Entity {entity} does not exist or is not running, skipping");
                continue;
            }

            _classId = classId;
            _currentAngleIndex = 0;
            _currentDistanceIndex = 0;

            var eye = new FixedEye
            {
                Position = new MapCoordinates(Vector2.Zero, mapId)
            };

            var oldEye = _eyeMan.CurrentEye;
            _eyeMan.CurrentEye = eye;

            await CaptureAllAngles(entity, mapId, eye, xform);

            _eyeMan.CurrentEye = oldEye;

            if (EntityManager.EntityExists(entity))
                RaiseNetworkEvent(new RequestDeleteTrainingDataEntityEvent(EntityManager.GetNetEntity(entity)));

            log.Info($"Completed capture for entity {entity}. Total samples: {_currentSample}");
        }

        _isCapturing = false;
    }

    private async Task CaptureAllAngles(EntityUid entity, MapId mapId, FixedEye eye, TransformComponent xform)
    {
        var log = Logger.GetSawmill("trainingdata");
        var position = _transform.GetWorldPosition(entity);
        log.Info($"Starting capture for entity {entity} at position {position} on map {mapId}");

        for (_currentAngleIndex = 0; _currentAngleIndex < Angles.Length; _currentAngleIndex++)
        {
            for (_currentDistanceIndex = 0; _currentDistanceIndex < Distances.Length; _currentDistanceIndex++)
            {
                var angle = Angles[_currentAngleIndex];
                var distance = Distances[_currentDistanceIndex];

                var cameraOffset = new Vector2(
                    (float)(Math.Cos(angle) * distance),
                    (float)(Math.Sin(angle) * distance)
                );

                eye.Position = new MapCoordinates(position + cameraOffset, mapId);
                eye.Rotation = angle + Math.PI;

                await Task.Delay(150);

                if (!EntityManager.EntityExists(entity))
                    return;

                var screenPos = _eyeMan.WorldToScreen(position);
                var screenSize = _clyde.ScreenSize;

                var spriteBounds = GetEntityScreenBounds(entity, screenPos, screenSize);
                if (!spriteBounds.HasValue)
                {
                    log.Info($"Skipped sample: sprite bounds null (angle={Angles[_currentAngleIndex].Degrees:F0}deg, dist={distance:F0})");
                    continue;
                }

                var bounds = spriteBounds.Value;
                var centerX = (bounds.Left + bounds.Right) / 2f / screenSize.X;
                var centerY = (bounds.Top + bounds.Bottom) / 2f / screenSize.Y;
                var width = (bounds.Right - bounds.Left) / screenSize.X;
                var height = (bounds.Bottom - bounds.Top) / screenSize.Y;

                centerX = Math.Clamp(centerX, 0.001f, 0.999f);
                centerY = Math.Clamp(centerY, 0.001f, 0.999f);
                width = Math.Clamp(width, 0.001f, 0.999f);
                height = Math.Clamp(height, 0.001f, 0.999f);

                var image = await TakeScreenshot();
                if (image == null)
                {
                    log.Info($"Skipped sample: screenshot null (angle={Angles[_currentAngleIndex].Degrees:F0}deg, dist={distance:F0})");
                    continue;
                }

                var fileName = $"sample_{_currentSample:D6}";
                _currentSample++;

                await SaveImage(image, fileName);
                await SaveYoloLabel(fileName, _classId, centerX, centerY, width, height);

                log.Info($"Saved sample {_currentSample}: angle={Angles[_currentAngleIndex].Degrees:F0}deg, dist={distance:F0}");
            }
        }
    }

    private Box2? GetEntityScreenBounds(EntityUid entity, Vector2 centerScreen, Vector2 screenSize)
    {
        if (!TryComp(entity, out SpriteComponent? sprite))
            return null;

        var spriteSize = GetSpritePixelSize(sprite);
        if (spriteSize.Equals(Vector2i.Zero))
            return null;

        var scale = sprite.Scale;
        var scaledWidth = spriteSize.X * scale.X;
        var scaledHeight = spriteSize.Y * scale.Y;

        var halfWidth = scaledWidth / 2f;
        var halfHeight = scaledHeight / 2f;

        var screenX = centerScreen.X - halfWidth;
        var screenY = centerScreen.Y - halfHeight;

        var minX = Math.Clamp(screenX, 0, screenSize.X);
        var minY = Math.Clamp(screenY, 0, screenSize.Y);
        var maxX = Math.Clamp(screenX + scaledWidth, 0, screenSize.X);
        var maxY = Math.Clamp(screenY + scaledHeight, 0, screenSize.Y);

        if (maxX - minX < 1 || maxY - minY < 1)
            return null;

        return new Box2(minX, minY, maxX, maxY);
    }

    private Vector2i GetSpritePixelSize(SpriteComponent sprite)
    {
        var size = Vector2i.Zero;
        foreach (var layer in sprite.AllLayers)
        {
            if (!layer.Visible)
                continue;
            size = Vector2i.ComponentMax(size, layer.PixelSize);
        }
        return size;
    }

    private async Task<Image<Rgb24>?> TakeScreenshot()
    {
        var tcs = new TaskCompletionSource<Image<Rgb24>?>();

        try
        {
            _clyde.Screenshot(ScreenshotType.Final, img =>
            {
                tcs.SetResult(img);
            });
        }
        catch (Exception e)
        {
            Logger.GetSawmill("trainingdata").Error($"Failed to take screenshot: {e}");
            tcs.SetResult(null);
        }

        return await tcs.Task;
    }

    private async Task SaveImage<T>(Image<T> image, string fileName) where T : unmanaged, IPixel<T>
    {
        var path = TrainingDataPath / "images" / $"{fileName}.png";

        await using var file = _resMan.UserData.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await Task.Run(() => image.SaveAsPng(file));
    }

    private async Task SaveYoloLabel(string fileName, int classId, float centerX, float centerY, float width, float height)
    {
        var path = TrainingDataPath / "labels" / $"{fileName}.txt";
        var content = $"{classId} {centerX:F6} {centerY:F6} {width:F6} {height:F6}";

        await using var file = _resMan.UserData.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(file);
        await writer.WriteAsync(content);
    }
}
