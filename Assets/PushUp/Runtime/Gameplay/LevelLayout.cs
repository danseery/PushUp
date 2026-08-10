using System;
using System.Collections.Generic;
using System.Linq;
using PushUp.Core;
using UnityEngine;

namespace PushUp.Gameplay
{
    /// <summary>
    /// Immutable, allocation-free view of the authored level for the lifetime of a run.
    /// Rebuild it explicitly after editor-time hierarchy changes; gameplay code must not
    /// repeatedly search the hierarchy.
    /// </summary>
    public sealed class LevelLayoutSnapshot
    {
        public LevelLayoutSnapshot(PlayerSpawnPoint[] players, WorldSpawnPoint[] world, SpawnGroup[] groups,
            SummitGoal summit)
        {
            PlayerSpawns = players;
            WorldSpawns = world;
            Groups = groups;
            Summit = summit;
        }

        public PlayerSpawnPoint[] PlayerSpawns { get; }
        public WorldSpawnPoint[] WorldSpawns { get; }
        public SpawnGroup[] Groups { get; }
        public SummitGoal Summit { get; }
    }

    [DisallowMultipleComponent]
    public sealed class LevelLayout : MonoBehaviour
    {
        private LevelLayoutSnapshot _snapshot;

        public PlayerSpawnPoint[] PlayerSpawns => Snapshot.PlayerSpawns;
        public WorldSpawnPoint[] WorldSpawns => Snapshot.WorldSpawns;
        public SpawnGroup[] Groups => Snapshot.Groups;
        public SummitGoal Summit => Snapshot.Summit;
        public LevelLayoutSnapshot Snapshot => _snapshot ??= BuildSnapshot();

        private void Awake() => RefreshSnapshot();

        private void OnValidate() => _snapshot = null;

        public LevelLayoutSnapshot RefreshSnapshot() => _snapshot = BuildSnapshot();

        public bool ValidateLayout(out string[] errors)
        {
            List<string> results = new();
            // Validation is an explicit authoring/startup boundary, so refresh once here.
            // All subsequent runtime reads use this same snapshot without allocations.
            LevelLayoutSnapshot snapshot = RefreshSnapshot();
            PlayerSpawnPoint[] players = snapshot.PlayerSpawns;
            WorldSpawnPoint[] world = snapshot.WorldSpawns;
            SpawnGroup[] groups = snapshot.Groups;
            if (players.Length == 0)
                results.Add("Level requires at least one PlayerSpawnPoint.");
            for (int slot = 0; slot < PushUpConstants.MaxPlayers; slot++)
            {
                if (!players.Any(point => point.Slot == slot))
                    results.Add($"Level requires player spawn slot {slot} for four-player sessions.");
            }
            foreach (IGrouping<int, PlayerSpawnPoint> duplicate in players.GroupBy(point => point.Slot).Where(group => group.Count() > 1))
                results.Add($"Player slot {duplicate.Key} is assigned more than once.");
            foreach (PlayerSpawnPoint point in players)
            {
                ValidateDefinition(point.Definition, $"Player spawn '{point.name}'", results);
                if (point.Definition != null && point.Definition.Role != SpawnRole.Player)
                    results.Add($"Player spawn '{point.name}' must reference a Player definition.");
            }
            IEnumerable<(string Id, string Name)> markerIds = players.Select(point => (point.MarkerId, point.name))
                .Concat(world.Select(point => (point.MarkerId, point.name)));
            foreach ((string id, string name) in markerIds.Where(marker => string.IsNullOrWhiteSpace(marker.Id)))
                results.Add($"Spawn marker '{name}' has no stable marker ID.");
            foreach (IGrouping<string, (string Id, string Name)> duplicate in markerIds
                         .Where(marker => !string.IsNullOrWhiteSpace(marker.Id))
                         .GroupBy(marker => marker.Id).Where(group => group.Count() > 1))
                results.Add($"Spawn marker ID '{duplicate.Key}' is duplicated.");
            foreach (WorldSpawnPoint point in world)
            {
                ValidateDefinition(point.Definition, $"World spawn '{point.name}'", results);
                if (point.Definition != null && point.Definition.Role == SpawnRole.Player)
                    results.Add($"World spawn '{point.name}' cannot reference a Player definition.");
            }
            int boulders = world.Count(point => point.IsEnabled && point.Definition != null &&
                                                 point.Definition.Role == SpawnRole.PrimaryBoulder);
            if (boulders != 1)
                results.Add($"Level requires exactly one enabled Primary Boulder spawn; found {boulders}.");
            int summits = GetComponentsInChildren<SummitGoal>(true).Length;
            if (summits != 1)
                results.Add($"Level requires exactly one SummitGoal; found {summits}.");
            foreach (IGrouping<string, SpawnGroup> duplicate in groups.GroupBy(group => group.Id).Where(group => group.Count() > 1))
                results.Add($"Spawn group ID '{duplicate.Key}' is duplicated.");
            SpawnDefinition[] definitions = players.Select(point => point.Definition)
                .Concat(world.Select(point => point.Definition)).Where(definition => definition != null).Distinct().ToArray();
            foreach (IGrouping<string, SpawnDefinition> duplicate in definitions.GroupBy(definition => definition.Id)
                         .Where(group => group.Count() > 1))
                results.Add($"Spawn definition ID '{duplicate.Key}' is duplicated by multiple assets.");
            errors = results.ToArray();
            return errors.Length == 0;
        }

        private LevelLayoutSnapshot BuildSnapshot()
        {
            PlayerSpawnPoint[] players = GetComponentsInChildren<PlayerSpawnPoint>(true)
                .OrderBy(point => point.Slot).ThenBy(point => point.MarkerId, StringComparer.Ordinal).ToArray();
            WorldSpawnPoint[] world = GetComponentsInChildren<WorldSpawnPoint>(true)
                .OrderBy(point => point.GroupId, StringComparer.Ordinal)
                .ThenBy(point => point.MarkerId, StringComparer.Ordinal).ToArray();
            SpawnGroup[] groups = GetComponentsInChildren<SpawnGroup>(true)
                .OrderBy(group => group.Id, StringComparer.Ordinal).ToArray();
            SummitGoal summit = GetComponentsInChildren<SummitGoal>(true).FirstOrDefault();
            return new LevelLayoutSnapshot(players, world, groups, summit);
        }

        private static void ValidateDefinition(SpawnDefinition definition, string owner, List<string> errors)
        {
            if (definition == null)
            {
                errors.Add($"{owner} has no SpawnDefinition.");
                return;
            }
            if (!definition.IsValid(out string error))
                errors.Add($"{owner}: {error}");
        }
    }
}
