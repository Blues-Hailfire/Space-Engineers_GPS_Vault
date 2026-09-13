using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace GpsSyncPlugin
{
    /// <summary>
    /// Thin wrapper around the official Space Engineers Mod API
    /// (Sandbox.ModAPI.MyAPIGateway). Client plugins are full-trust, so this
    /// is the same surface any in-game mod/script can already use — we don't
    /// touch game internals directly.
    /// </summary>
    public static class GameStateReader
    {
        public static bool IsSessionReady =>
            MyAPIGateway.Session?.Player?.Character != null;

        /// <summary>
        /// Planets in the current world. There's no Mod-API-safe "IMyPlanet"
        /// interface, but client plugins are full-trust (unlike PB scripts),
        /// so the concrete internal MyPlanet class is used directly to get
        /// its real radius — voxel maps in general (IMyVoxelBase, which also
        /// covers asteroids) only expose a bounding corner, not a center/size.
        /// </summary>
        public static List<PlanetDto> ReadPlanets()
        {
            var result = new List<PlanetDto>();
            if (!IsSessionReady)
                return result;

            var voxels = new List<IMyVoxelBase>();
            MyAPIGateway.Session.VoxelMaps.GetInstances(voxels, v => v is MyPlanet);

            foreach (var voxel in voxels)
            {
                if (!(voxel is MyPlanet planet))
                    continue;
                var pos = voxel.GetPosition();
                result.Add(new PlanetDto
                {
                    name = planet.StorageName,
                    x = pos.X,
                    y = pos.Y,
                    z = pos.Z,
                    radius = planet.AverageRadius,
                });
            }

            return result;
        }

        /// <summary>
        /// A signal counts as "hostile" for the map if it isn't ours or a
        /// friend's. MyIDModule.GetRelationPlayerBlock returns NoOwnership
        /// (not Enemies) whenever the broadcasting grid has no owner at all
        /// (ownerId 0) — which is exactly how a lot of vanilla pirate/
        /// encounter grids are set up, since they're never claimed by any
        /// player or NPC faction identity. Filtering strictly to Enemies
        /// silently drops every one of those, so NoOwnership/Neutral are
        /// treated as hostile too — matching what the player actually
        /// perceives as a threat, not just the subset with a real enemy
        /// faction owner.
        /// </summary>
        private static bool IsHostileRelation(MyRelationsBetweenPlayerAndBlock relation)
        {
            switch (relation)
            {
                case MyRelationsBetweenPlayerAndBlock.Enemies:
                case MyRelationsBetweenPlayerAndBlock.Neutral:
                case MyRelationsBetweenPlayerAndBlock.NoOwnership:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Every hostile signal currently visible on the local player's HUD —
        /// i.e. exactly what MyHud's own marker renderer would draw as a
        /// non-friendly contact (antenna/sensor detections already
        /// range-gated by the game itself). Reads MyHud.LocationMarkers
        /// directly rather than scanning all entities, so nothing is
        /// reported that the player couldn't already see in their own HUD.
        /// </summary>
        public static List<HostileSignalDto> ReadHostileSignals()
        {
            var result = new List<HostileSignalDto>();
            if (!IsSessionReady)
                return result;

            var localIdentityId = MyAPIGateway.Session.Player.IdentityId;

            foreach (var marker in MyHud.LocationMarkers.MarkerEntities)
            {
                var relation = MyIDModule.GetRelationPlayerBlock(marker.Owner, localIdentityId, marker.Share);
                if (!IsHostileRelation(relation))
                    continue;

                var entity = marker.Entity;
                var pos = marker.Position;
                var physics = entity?.Physics;
                var velocity = physics?.LinearVelocity ?? Vector3.Zero;
                var acceleration = physics?.LinearAcceleration ?? Vector3.Zero;

                // Pirate/NPC spawns commonly reuse identical grid names, so
                // signal_id (the game's EntityId) — not name — is what the
                // server actually keys on; this is just for display.
                var name = !string.IsNullOrWhiteSpace(entity?.DisplayName)
                    ? entity.DisplayName
                    : $"Unknown Signal {marker.EntityId % 10000}";
                if (name.Length > 32)
                    name = name.Substring(0, 32);

                result.Add(new HostileSignalDto
                {
                    signal_id = marker.EntityId,
                    name = name,
                    x = pos.X,
                    y = pos.Y,
                    z = pos.Z,
                    vx = velocity.X,
                    vy = velocity.Y,
                    vz = velocity.Z,
                    ax = acceleration.X,
                    ay = acceleration.Y,
                    az = acceleration.Z,
                });
            }

            return result;
        }

        // Prefix for the local-only "other active players" markers created by
        // /vault players ("/vp"). One-way, server-to-client only: excluded here
        // from the upward GPS push (never uploaded to the vault) and added via
        // AddLocalGps/RemoveLocalGps (never saved/synced by the game server
        // either) — see ChatCommands.HandlePlayers / ApplyVaultPlayerMarkers.
        public const string VaultPlayerPrefix = "[VP]";

        /// <summary>
        /// A GPS point is treated as private (never synced to the vault) if
        /// its name contains "[P]" or "[Private]" anywhere, case-insensitive.
        /// Purely a local naming convention the player opts into — nothing
        /// server-side needs to know about it, since it's filtered out before
        /// the point ever leaves the client.
        /// </summary>
        private static bool IsPrivateGpsName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            var lower = name.ToLowerInvariant();
            return lower.Contains("[p]") || lower.Contains("[private]");
        }

        public static List<GpsPointDto> ReadGpsList()
        {
            var result = new List<GpsPointDto>();
            if (!IsSessionReady)
                return result;

            var identityId = MyAPIGateway.Session.Player.IdentityId;
            var gpsList = new List<IMyGps>();
            MyAPIGateway.Session.GPS.GetGpsList(identityId, gpsList);

            foreach (var gps in gpsList)
            {
                if (gps.Name != null && gps.Name.StartsWith(VaultPlayerPrefix))
                    continue;
                if (IsPrivateGpsName(gps.Name))
                    continue;

                result.Add(new GpsPointDto
                {
                    name = gps.Name,
                    x = gps.Coords.X,
                    y = gps.Coords.Y,
                    z = gps.Coords.Z,
                    color = ToArgbHex(gps.GPSColor),
                });
            }

            return result;
        }

        /// <summary>
        /// Replaces the local "/vp" snapshot of other active players' GPS
        /// markers: removes whatever the previous /vp call added, then adds a
        /// fresh one per player. Purely local (AddLocalGps/RemoveLocalGps) —
        /// never synced to the actual game server, never pushed to the vault.
        /// </summary>
        public static void ApplyVaultPlayerMarkers(List<VaultPlayerDto> players)
        {
            if (!IsSessionReady || players == null)
                return;

            var identityId = MyAPIGateway.Session.Player.IdentityId;
            var localList = new List<IMyGps>();
            MyAPIGateway.Session.GPS.GetGpsList(identityId, localList);

            foreach (var gps in localList)
            {
                if (gps.Name != null && gps.Name.StartsWith(VaultPlayerPrefix))
                    MyAPIGateway.Session.GPS.RemoveLocalGps(gps);
            }

            var markerColor = new Color(0x71, 0xFF, 0x00);
            foreach (var player in players)
            {
                var coords = new Vector3D(player.x, player.y, player.z);
                var gps = MyAPIGateway.Session.GPS.Create($"{VaultPlayerPrefix}{player.name}", "", coords, true);
                gps.GPSColor = markerColor;
                MyAPIGateway.Session.GPS.AddLocalGps(gps);
            }
        }

        // Names of vault points this plugin instance has already seen locally at
        // least once (whether it added them itself or they already existed).
        // Without this, ApplyRemoteGpsList can't tell "never synced yet" apart
        // from "the player just deleted this" — both look like "missing
        // locally" — and would keep resurrecting anything the player deletes,
        // since the vault still has it until the *next* push reports it gone.
        // Session-lifetime only (not persisted): resets on plugin/game restart.
        private static readonly HashSet<string> knownVaultNames = new HashSet<string>();

        /// <summary>
        /// Merges the vault's GPS list (points added/edited in Discord, or by
        /// any other synced player) into the local player's in-game GPS list.
        /// Adds new points and updates changed ones via the network-synced
        /// AddGps/ModifyGps calls, so they persist and show up for the player
        /// like any other GPS entry. A vault point the player deletes locally
        /// is recognized (via knownVaultNames) and left deleted rather than
        /// re-added — the next GPS push then reports it missing, which is what
        /// makes the bot delete it from the vault too.
        /// </summary>
        public static void ApplyRemoteGpsList(List<GpsPointDto> remotePoints)
        {
            if (!IsSessionReady || remotePoints == null)
                return;

            var identityId = MyAPIGateway.Session.Player.IdentityId;
            var localList = new List<IMyGps>();
            MyAPIGateway.Session.GPS.GetGpsList(identityId, localList);
            var byName = localList
                .Where(g => !string.IsNullOrEmpty(g.Name))
                .GroupBy(g => g.Name)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var rp in remotePoints)
            {
                var coords = new Vector3D(rp.x, rp.y, rp.z);
                var color = FromArgbHex(rp.color);

                if (byName.TryGetValue(rp.name, out var existing))
                {
                    knownVaultNames.Add(rp.name);
                    bool coordsChanged = Vector3D.DistanceSquared(existing.Coords, coords) > 0.25;
                    bool colorChanged = existing.GPSColor.PackedValue != color.PackedValue;
                    if (coordsChanged || colorChanged)
                    {
                        existing.Coords = coords;
                        existing.GPSColor = color;
                        MyAPIGateway.Session.GPS.ModifyGps(identityId, existing);
                    }
                }
                else if (!knownVaultNames.Contains(rp.name))
                {
                    // Genuinely new to us — hidden from HUD by default, since the
                    // vault can hold far more points than are useful cluttering the
                    // HUD at once. Use the in-game "/vs" (vault search) and "/vc"
                    // (vault confirm) chat commands to reveal specific ones.
                    knownVaultNames.Add(rp.name);
                    var gps = MyAPIGateway.Session.GPS.Create(rp.name, "", coords, false);
                    gps.GPSColor = color;
                    MyAPIGateway.Session.GPS.AddGps(identityId, gps);
                }
                // else: known to us but currently missing locally — the player
                // deleted it. Leave it deleted; do not resurrect it here.
            }
        }

        private static Color FromArgbHex(string hex)
        {
            if (string.IsNullOrEmpty(hex))
                return Color.White;
            var c = hex.TrimStart('#');
            if (c.Length != 8)
                return Color.White;
            int a = Convert.ToInt32(c.Substring(0, 2), 16);
            int r = Convert.ToInt32(c.Substring(2, 2), 16);
            int g = Convert.ToInt32(c.Substring(4, 2), 16);
            int b = Convert.ToInt32(c.Substring(6, 2), 16);
            return new Color(r, g, b, a);
        }

        public static PositionSyncRequest ReadPlayerPosition()
        {
            if (!IsSessionReady)
                return null;

            var player = MyAPIGateway.Session.Player;
            var pos = player.Character.GetPosition();

            // Character.Physics reads ~0 velocity while seated/piloting — the
            // character isn't independently simulating, the grid is. Read from
            // whatever's actually controlled (the cockpit/grid when piloting,
            // the character itself when on foot) so both velocity and
            // acceleration reflect real movement either way.
            var controlledEntity = player.Controller?.ControlledEntity?.Entity ?? player.Character;
            var physics = controlledEntity?.Physics;
            var velocity = physics?.LinearVelocity ?? Vector3.Zero;
            var acceleration = physics?.LinearAcceleration ?? Vector3.Zero;

            return new PositionSyncRequest
            {
                player_name = player.DisplayName,
                x = pos.X,
                y = pos.Y,
                z = pos.Z,
                vx = velocity.X,
                vy = velocity.Y,
                vz = velocity.Z,
                ax = acceleration.X,
                ay = acceleration.Y,
                az = acceleration.Z,
            };
        }

        private static string ToArgbHex(Color c) =>
            $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
