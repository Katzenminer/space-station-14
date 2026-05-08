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
    [Dependency] private readonly SpriteSystem _sprite = default!;

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
                Position = new MapCoordinates(Vector2.Zero, mapId),
                Zoom = Vector2.One
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

        for (_currentAngleIndex = 0; _currentAngleIndex < Angles.Length; _currentAngleIndex++)
        {
            for (_currentDistanceIndex = 0; _currentDistanceIndex < Distances.Length; _currentDistanceIndex++)
            {
                if (!EntityManager.EntityExists(entity))
                    return;

                var angle = Angles[_currentAngleIndex];
                var distance = Distances[_currentDistanceIndex];

                var position = _transform.GetWorldPosition(entity);
                var cameraOffset = new Vector2(
                    (float)(Math.Cos(angle) * distance),
                    (float)(Math.Sin(angle) * distance)
                );

                eye.Position = new MapCoordinates(position + cameraOffset, mapId);
                eye.Rotation = angle + Math.PI;

                await Task.Delay(150);

                if (!EntityManager.EntityExists(entity))
                    return;

                var screenSize = _clyde.ScreenSize;

                var spriteBounds = GetEntityScreenBounds(entity, screenSize);
                if (!spriteBounds.HasValue)
                {
                    log.Info($"Skipped sample: sprite bounds null (angle={Angles[_currentAngleIndex].Degrees:F0}deg, dist={distance:F0})");
                    continue;
                }

                var image = await TakeScreenshot();
                if (image == null)
                {
                    log.Info($"Skipped sample: screenshot null (angle={Angles[_currentAngleIndex].Degrees:F0}deg, dist={distance:F0})");
                    continue;
                }

                var bounds = spriteBounds.Value;
                var imgWidth = image.Width;
                var imgHeight = image.Height;
                var centerX = (bounds.Left + bounds.Right) / 2f / imgWidth;
                var centerY = (bounds.Top + bounds.Bottom) / 2f / imgHeight;
                var width = (bounds.Right - bounds.Left) / imgWidth;
                var height = (bounds.Bottom - bounds.Top) / imgHeight;

                centerX = Math.Clamp(centerX, 0.001f, 0.999f);
                centerY = Math.Clamp(centerY, 0.001f, 0.999f);
                width = Math.Clamp(width, 0.001f, 0.999f);
                height = Math.Clamp(height, 0.001f, 0.999f);

                log.Info($"Saved sample {_currentSample}: angle={Angles[_currentAngleIndex].Degrees:F0}deg, dist={distance:F0}, box=({bounds.Left:F0},{bounds.Top:F0},{bounds.Right:F0},{bounds.Bottom:F0}), img={imgWidth}x{imgHeight}, yolo={width:F6}x{height:F6}");

                var fileName = $"sample_{_currentSample:D6}";
                _currentSample++;

                await SaveImage(image, fileName);
                await SaveYoloLabel(fileName, _classId, centerX, centerY, width, height);
            }
        }
    }

    private Box2? GetEntityScreenBounds(EntityUid entity, Vector2 screenSize)
    {
        if (!TryComp(entity, out SpriteComponent? sprite) || !TryComp(entity, out TransformComponent? xform))
            return null;

        if (!sprite.Visible)
            return null;

        // Get the local bounding box. This properly unions all visible layers
        // accounting for offsets, per-layer scales, and the sprite's own scale.
        var localBounds = _sprite.GetLocalBounds((entity, sprite));

        // Reject degenerate bounds (no visible sprite content)
        if (localBounds.Width < 0.001f || localBounds.Height < 0.001f)
            return null;

        var worldPos = _transform.GetWorldPosition(xform);
        var worldRot = _transform.GetWorldRotation(xform);
        var eyeRot = _eyeMan.CurrentEye.Rotation;

        // Compute sprite's effective world-space rotation and offset.
        var composition = sprite.NoRotation
            ? sprite.Rotation - eyeRot
            : sprite.Rotation + worldRot;

        var effectiveOffset = sprite.Offset == Vector2.Zero
            ? Vector2.Zero
            : sprite.NoRotation
                ? (-eyeRot).RotateVec(sprite.Offset)
                : worldRot.RotateVec(sprite.Offset);

        var center = worldPos + effectiveOffset;

        // Transform the four local corners to world space, then to screen space.
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;

        foreach (var local in new[] { localBounds.TopLeft, localBounds.TopRight, localBounds.BottomRight, localBounds.BottomLeft })
        {
            var worldCorner = center + composition.RotateVec(local);
            var screen = _eyeMan.WorldToScreen(worldCorner);
            minX = Math.Min(minX, screen.X);
            minY = Math.Min(minY, screen.Y);
            maxX = Math.Max(maxX, screen.X);
            maxY = Math.Max(maxY, screen.Y);
        }

        minX = Math.Clamp(minX, 0, screenSize.X);
        minY = Math.Clamp(minY, 0, screenSize.Y);
        maxX = Math.Clamp(maxX, 0, screenSize.X);
        maxY = Math.Clamp(maxY, 0, screenSize.Y);

        if (maxX - minX < 1 || maxY - minY < 1)
            return null;

        return new Box2(minX, minY, maxX, maxY);
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
