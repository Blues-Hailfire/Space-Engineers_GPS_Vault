using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Voxels;
using VRageMath;

namespace GpsSyncPlugin
{
    /// <summary>
    /// Ore detector results, both directions: reads what the local player's
    /// ore detector currently shows (MyHud.OreMarkers) for upload, and applies
    /// the channel's accumulated deposits back into the in-game GPS list.
    /// </summary>
    public static class OreDeposits
    {
        // Prefix for the GPS entries ApplyRemoteOreDeposits creates. Excluded
        // from the vault GPS push (see GameStateReader.ReadGpsList) — ores have
        // their own table server-side, they aren't vault waypoints.
        public const string OreGpsPrefix = "[ORE] ";

        // A planet is a whole world — one point for its entire surface would
        // be useless for actually finding anything. Its ore markers are
        // clustered by location so a planet gets one point per general area,
        // each combining every ore type found there. An asteroid is small
        // enough that a single searchable point covering the whole rock is
        // exactly the point — but that point still carries every individual
        // vein's exact position (see OreDepositDto.locations) at this same
        // granularity, so "where exactly on the rock" isn't lost, just not
        // surfaced as its own separate GPS entry.
        private const double ClusterMeters = 100.0;

        /// <summary>
        /// Every ore-bearing voxel body currently registered with the HUD.
        /// Asteroids report exactly one point for the whole body, named
        /// "AST - <ores>" (e.g. "AST - Iron,Ice"), with every individual
        /// vein's clustered position preserved in .locations. Planets report
        /// one point per clustered area instead (see ClusterMeters), each
        /// named with the planet's real name (e.g. "Earth - Iron,Ice").
        /// Must run on the game thread.
        /// </summary>
        public static List<OreDepositDto> ReadOreDeposits()
        {
            var result = new List<OreDepositDto>();
            if (!GameStateReader.IsSessionReady)
                return result;

            // Planets: keyed by (voxel, spatial bucket) — merges every ore
            // type found in the same area into one point, so a planet ends
            // up with one point per area rather than per ore.
            var planetAreas = new Dictionary<string, (Vector3D sum, int count, HashSet<string> ores, string voxelName)>();
            // Asteroids: keyed by (voxel, ore, spatial bucket) — one fine
            // cluster per vein, kept separately so each vein's own position
            // survives in the rolled-up body's .locations (see below).
            var asteroidVeins = new Dictionary<string, (Vector3D sum, int count, string ore, string voxelName)>();

            foreach (var deposit in MyHud.OreMarkers)
            {
                var voxel = deposit.VoxelMap;
                if (voxel == null || voxel.Closed || voxel.MarkedForClose)
                    continue;

                // Same world-position math as MyEntityOreDeposit.Data.
                // ComputeWorldPosition, which is internal.
                var reference = voxel.PositionComp.GetPosition() - (Vector3D)voxel.StorageMin;
                var isPlanet = voxel is MyPlanet;

                foreach (var data in deposit.Materials)
                {
                    var ore = data.Material?.MinedOre;
                    if (string.IsNullOrEmpty(ore))
                        continue;

                    var local = data.AverageLocalPosition;
                    MyVoxelCoordSystems.LocalPositionToWorldPosition(reference, ref local, out var world);

                    var bucket = new Vector3I(
                        (int)Math.Floor(world.X / ClusterMeters),
                        (int)Math.Floor(world.Y / ClusterMeters),
                        (int)Math.Floor(world.Z / ClusterMeters));

                    if (isPlanet)
                    {
                        var areaKey = $"{voxel.StorageName}|{bucket.X},{bucket.Y},{bucket.Z}";
                        planetAreas.TryGetValue(areaKey, out var a);
                        var ores = a.ores ?? new HashSet<string>();
                        ores.Add(ore);
                        planetAreas[areaKey] = (a.sum + world, a.count + 1, ores, voxel.StorageName);
                    }
                    else
                    {
                        var veinKey = $"{voxel.StorageName}|{ore}|{bucket.X},{bucket.Y},{bucket.Z}";
                        asteroidVeins.TryGetValue(veinKey, out var v);
                        asteroidVeins[veinKey] = (v.sum + world, v.count + 1, ore, voxel.StorageName);
                    }
                }
            }

            foreach (var kv in planetAreas)
            {
                var (sum, count, ores, voxelName) = kv.Value;
                var avg = sum / count;
                var oreList = string.Join(",", ores.OrderBy(o => o, StringComparer.Ordinal));
                result.Add(new OreDepositDto
                {
                    deposit_id = kv.Key,
                    name = $"{voxelName} - {oreList}",
                    ores = oreList,
                    voxel_name = voxelName,
                    is_planet = true,
                    x = avg.X,
                    y = avg.Y,
                    z = avg.Z,
                });
            }

            // Roll every vein on the same asteroid up into one body-level
            // point, keeping each vein's own averaged position in .locations.
            var asteroidBodies = new Dictionary<string, (Vector3D sum, int count, HashSet<string> ores, List<OreLocationDto> locations)>();
            foreach (var vein in asteroidVeins.Values)
            {
                var avg = vein.sum / vein.count;
                asteroidBodies.TryGetValue(vein.voxelName, out var b);
                var ores = b.ores ?? new HashSet<string>();
                var locations = b.locations ?? new List<OreLocationDto>();
                ores.Add(vein.ore);
                locations.Add(new OreLocationDto { ore = vein.ore, x = avg.X, y = avg.Y, z = avg.Z });
                asteroidBodies[vein.voxelName] = (b.sum + avg, b.count + 1, ores, locations);
            }

            foreach (var kv in asteroidBodies)
            {
                var voxelName = kv.Key;
                var (sum, count, ores, locations) = kv.Value;
                var avg = sum / count;
                var oreList = string.Join(",", ores.OrderBy(o => o, StringComparer.Ordinal));
                result.Add(new OreDepositDto
                {
                    deposit_id = voxelName,
                    name = $"AST - {oreList}",
                    ores = oreList,
                    voxel_name = voxelName,
                    is_planet = false,
                    x = avg.X,
                    y = avg.Y,
                    z = avg.Z,
                    locations = locations,
                });
            }

            return result;
        }

        // deposit_ids this session has already added locally at least once —
        // same role as GameStateReader's knownVaultNames: without it, a
        // deposit the player deleted from their GPS list would be re-added
        // on the very next pull.
        private static readonly HashSet<string> knownDepositIds = new HashSet<string>();

        /// <summary>
        /// Adds each body's ore point the player doesn't have yet as a GPS
        /// entry, with Show on HUD OFF so a large ore list doesn't flood the
        /// HUD — reveal specific ones via the GPS screen or /vault search +
        /// /vault confirm. Must run on the game thread.
        /// </summary>
        public static void ApplyRemoteOreDeposits(List<OreDepositDto> deposits)
        {
            if (!GameStateReader.IsSessionReady || deposits == null)
                return;

            var identityId = MyAPIGateway.Session.Player.IdentityId;
            var localList = new List<IMyGps>();
            MyAPIGateway.Session.GPS.GetGpsList(identityId, localList);
            var existingNames = new HashSet<string>(localList.Where(g => g.Name != null).Select(g => g.Name));

            foreach (var d in deposits)
            {
                if (string.IsNullOrEmpty(d.deposit_id) || !knownDepositIds.Add(d.deposit_id))
                    continue;

                var name = GpsName(d);
                if (existingNames.Contains(name))
                    continue;

                var gps = MyAPIGateway.Session.GPS.Create(name, $"Ore detector: {d.ores} on {d.voxel_name}", new Vector3D(d.x, d.y, d.z), false);
                gps.GPSColor = new Color(0xB0, 0x8D, 0x57);
                MyAPIGateway.Session.GPS.AddGps(identityId, gps);
            }
        }

        // A cluster's display name (d.name) can collide with another
        // cluster's — two different asteroids with the same ore composition
        // both read "AST - Iron,Ice", and two areas of the same planet with
        // the same composition both read e.g. "Earth - Iron,Ice". A short
        // stable hash of the actual deposit_id (unique per cluster) keeps
        // the in-game GPS entry itself unique without changing what's shown
        // on the map.
        private static string GpsName(OreDepositDto d)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var ch in d.deposit_id)
                    hash = (hash ^ ch) * 16777619;
                return $"{OreGpsPrefix}{d.name} {hash % 0x10000:X4}";
            }
        }
    }
}
