using System.IO;
using System.Linq;
using System.Numerics;
using Content.Server.Administration;
using Content.Server.Administration.Systems;
using Content.Server.Humanoid;
using Content.Shared.Administration;
using Content.Shared.Body;
using Content.Shared.Clothing;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Station;
using Robust.Shared.Console;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using YamlDotNet.RepresentationModel;

namespace Content.Server.Administration.Commands;

[AdminCommand(AdminFlags.Debug)]
public sealed class GenTrainingDataCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IMapManager _mapMan = default!;
    [Dependency] private readonly IPrototypeManager _protoMan = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ISerializationManager _serMan = default!;

    public string Command => "gentrainingdata";
    public string Description => "Spawns a character from profile export with random loadout on a clean grid for training data capture.";
    public string Help => "Usage: gentrainingdata <profile.yml> [count] [classId]\n  profile.yml: Path to character profile export YAML\n  count: Number of samples to generate (default: 10)\n  classId: YOLO class ID for this character (default: 0)";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var player = shell.Player;
        if (player == null)
        {
            shell.WriteLine("Only players can use this command.");
            return;
        }

        // Handle case where command name is accidentally included in args
        var pathIndex = 0;
        if (args.Length > 0 && (args[0] == Command || args[0] == "/" + Command))
            pathIndex = 1;

        if (args.Length <= pathIndex)
        {
            shell.WriteLine(Help);
            return;
        }

        var filePath = args[pathIndex];
        if (!File.Exists(filePath))
        {
            shell.WriteError($"File not found: {filePath}");
            return;
        }

        var count = args.Length > pathIndex + 1 && int.TryParse(args[pathIndex + 1], out var c) ? c : 10;
        var classId = args.Length > pathIndex + 2 && int.TryParse(args[pathIndex + 2], out var cid) ? cid : 0;

        HumanoidCharacterProfile profile;
        try
        {
            var yamlStream = new YamlStream();
            using (var reader = new StreamReader(filePath))
            {
                yamlStream.Load(reader);
            }

            var root = yamlStream.Documents[0].RootNode.ToDataNode();
            var version = yamlStream.Documents[0].RootNode["version"].ToString();

            if (version == "1")
            {
                var export = _serMan.Read<HumanoidProfileExportV1>(root, notNullableOverride: true);
                profile = export.ToV2().Profile;
            }
            else if (version == "2")
            {
                var export = _serMan.Read<HumanoidProfileExportV2>(root, notNullableOverride: true);
                profile = export.Profile;
            }
            else
            {
                shell.WriteError($"Unknown profile version: {version}");
                return;
            }
        }
        catch (Exception e)
        {
            shell.WriteError($"Failed to parse profile: {e.Message}");
            return;
        }

        shell.WriteLine($"Loaded profile: {profile.Name} ({profile.Species})");

        var visualBody = _entMan.System<SharedVisualBodySystem>();
        var humanoidProfile = _entMan.System<HumanoidProfileSystem>();

        var playerUid = player.AttachedEntity.GetValueOrDefault();
        if (!_entMan.TryGetComponent<TransformComponent>(playerUid, out var playerXform))
        {
            shell.WriteError("Could not get player transform.");
            return;
        }

        var playerGridUid = playerXform.GridUid;
        if (!_entMan.EntityExists(playerGridUid))
        {
            shell.WriteError("Player is not on a grid.");
            return;
        }

        var playerGridPos = playerXform.LocalPosition;

        for (var i = 0; i < count; i++)
        {
            var spawnGridPos = playerGridPos + new Vector2(3f, 3f * (i + 1));
            var spawnCoords = new EntityCoordinates(playerGridUid.Value, spawnGridPos);

            var speciesProto = _protoMan.Index<SpeciesPrototype>(profile.Species);
            var character = _entMan.SpawnEntity(speciesProto.Prototype, spawnCoords);

            visualBody.ApplyProfileTo(character, profile);
            humanoidProfile.ApplyProfileTo(character, profile);

            ApplyRandomLoadout(character, profile);

            var netEnt = _entMan.GetNetEntity(character);

            var trainingData = _entMan.System<TrainingDataSystem>();
            trainingData.SendTrainingDataRequest(netEnt, (int)playerXform.MapID, classId, i, count, player.Channel);

            shell.WriteLine($"Spawned character for sample {i + 1}/{count}. Entity: {character}, Map: {playerXform.MapID}");
        }
    }

    private void ApplyRandomLoadout(EntityUid uid, HumanoidCharacterProfile profile)
    {
        var loadoutGroups = _protoMan.EnumeratePrototypes<RoleLoadoutPrototype>().ToList();
        if (loadoutGroups.Count == 0)
            return;

        var selectedLoadout = _random.Pick(loadoutGroups);
        var roleLoadout = new RoleLoadout(selectedLoadout.ID);
        roleLoadout.SetDefault(profile, null, _protoMan, true);

        var stationSpawning = _entMan.System<SharedStationSpawningSystem>();
        stationSpawning.EquipRoleLoadout(uid, roleLoadout, selectedLoadout);
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            return CompletionResult.FromHint("<profile.yml>");
        }

        if (args.Length == 2)
        {
            return CompletionResult.FromHint("<count>");
        }

        if (args.Length == 3)
        {
            return CompletionResult.FromHint("<classId>");
        }

        return CompletionResult.Empty;
    }
}
