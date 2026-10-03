using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
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
        /// Whether the local player generated any keyboard/mouse/joystick
        /// input this tick — used to drive the AFK timer (see
        /// Plugin.UpdateAfkState). Must be polled every frame (Update()); a
        /// tick missed here is a tick of idle time silently lost, which is
        /// fine for a multi-minute timeout but would matter for anything
        /// tighter. Held movement keys (e.g. WASD) count via IsAnyKeyPress —
        /// no need for IsAnyNewKeyPress, since a player flying in a straight
        /// line while holding a key is not AFK.
        /// </summary>
        public static bool HasInputActivity()
        {
            var input = MyAPIGateway.Input;
            if (input == null)
                return false;

            return input.IsAnyKeyPress()
                || input.IsAnyMouseOrJoystickPressed()
                || input.GetMouseXForGamePlay() != 0
                || input.GetMouseYForGamePlay() != 0
                || input.DeltaMouseScrollWheelValue() != 0;
        }

        /// <summary>
        /// Stable identifier for the current world/server, used to key
        /// per-world sync on/off state (see Config.SyncEnabled). Combines the
        /// connected dedicated/listen server's Steam ID (stable across client
        /// restarts as long as the server keeps the same identity) with a
        /// local identifier: CurrentPath (the save folder — doesn't change if
        /// the player just renames the world in its settings) when available,
        /// falling back to Session.Name otherwise (e.g. a dedicated-server
        /// client, which has no local save folder for the world it joined —
        /// harmless, since ServerId alone already makes that branch unique).
        /// Null before a session is loaded, which the caller treats as "no
        /// world to bind to" (sync off).
        /// </summary>
        public static string CurrentWorldKey
        {
            get
            {
                if (MyAPIGateway.Session == null)
                    return null;

                var multiplayer = MyAPIGateway.Multiplayer;
                var serverPart = (multiplayer != null && multiplayer.MultiplayerActive)
                    ? multiplayer.ServerId.ToString()
                    : "SP";
                var localPart = MyAPIGateway.Session.CurrentPath;
                if (string.IsNullOrEmpty(localPart))
                    localPart = MyAPIGateway.Session.Name;
                return $"{serverPart}|{localPart}";
            }
        }

        /// <summary>
        /// Human-readable label for the current world/server — for a
        /// dedicated/listen server, its advertised HostName (via the internal
        /// MyMultiplayerBase — not on the public IMyMultiplayer ModAPI
        /// interface, but client plugins are full-trust and MyMultiplayerBase
        /// itself is a public class, so this cast is legal even though the
        /// concrete MyMultiplayerClient type behind it isn't); otherwise the
        /// world/session name. There's no ModAPI-exposed way to read the
        /// server's actual network address (IP:port) — HostName is the
        /// closest thing to a "server name" a player would recognize. Null
        /// before a session is loaded, same as CurrentWorldKey.
        /// </summary>
        public static string CurrentWorldDisplayName
        {
            get
            {
                if (MyAPIGateway.Session == null)
                    return null;

                var multiplayer = MyAPIGateway.Multiplayer;
                if (multiplayer != null && multiplayer.MultiplayerActive && !multiplayer.IsServer)
                {
                    var hostName = (multiplayer as Sandbox.Engine.Multiplayer.MyMultiplayerBase)?.HostName;
                    if (!string.IsNullOrWhiteSpace(hostName))
                        return hostName;
                }
                return string.IsNullOrWhiteSpace(MyAPIGateway.Session.Name) ? "(unnamed world)" : MyAPIGateway.Session.Name;
            }
        }

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
        /// Asteroids in the current world — every voxel map that ISN'T a
        /// planet. Unlike MyPlanet, there's no precise physical radius for an
        /// irregular asteroid, so this reports a bounding-sphere approximation:
        /// the average of the voxel storage's three half-extents (SizeInMetresHalf)
        /// — a decent "typical size" middle ground between an oblong asteroid's
        /// longest axis (which would overstate a wireframe sphere drawn around
        /// its short axes) and its shortest.
        /// </summary>
        public static List<AsteroidDto> ReadAsteroids()
        {
            var result = new List<AsteroidDto>();
            if (!IsSessionReady)
                return result;

            var voxels = new List<IMyVoxelBase>();
            MyAPIGateway.Session.VoxelMaps.GetInstances(voxels, v => v is MyVoxelMap);

            foreach (var voxel in voxels)
            {
                if (!(voxel is MyVoxelBase asteroid))
                    continue;
                var pos = voxel.GetPosition();
                var half = asteroid.SizeInMetresHalf;
                result.Add(new AsteroidDto
                {
                    name = asteroid.StorageName,
                    x = pos.X,
                    y = pos.Y,
                    z = pos.Z,
                    radius = (half.X + half.Y + half.Z) / 3.0,
                });
            }

            return result;
        }

        /// <summary>
        /// Map marker color/category per relation, matching the web map's
        /// legend — including Owner now (the player's own grids), reported as
        /// "owned" so the server can decide whether to show it: admins toggle
        /// Hostile/Neutral/Allied/Owned on/off and pick their colors per
        /// channel (see sync_api.py's /map/category_settings), which
        /// overrides the color sent here and can hide a whole category from
        /// the map entirely — Owned is off by default server-side. The colors
        /// below are just the client's own default/fallback, used until that
        /// per-channel override is fetched.
        ///
        /// The server also uses `category` (not just color) to decide how
        /// long a stale signal lingers before disappearing — "unidentified"
        /// signals expire much faster than the rest, since
        /// MyIDModule.GetRelationPlayerBlock returns NoOwnership — not
        /// Enemies — whenever the broadcasting grid has no owner at all
        /// (ownerId 0), which is exactly how a lot of vanilla pirate/
        /// encounter grids are set up (never claimed by any player or NPC
        /// faction identity), so this bucket is much noisier than a real
        /// enemy-faction contact. "unidentified" is also the one category the
        /// admin toggle/color settings don't apply to — it keeps this fixed
        /// color and its own short, fixed expiry regardless.
        /// </summary>
        internal static void ClassifyRelation(MyRelationsBetweenPlayerAndBlock relation, out string category, out string color)
        {
            switch (relation)
            {
                case MyRelationsBetweenPlayerAndBlock.Owner:
                    category = "owned"; color = "#FF3080FF"; break;
                case MyRelationsBetweenPlayerAndBlock.NoOwnership:
                    category = "unidentified"; color = "#FFEDAA38"; break;
                case MyRelationsBetweenPlayerAndBlock.Neutral:
                    category = "neutral"; color = "#FFFFCF00"; break;
                case MyRelationsBetweenPlayerAndBlock.FactionShare:
                case MyRelationsBetweenPlayerAndBlock.Friends:
                    category = "allied"; color = "#FF00FF0E"; break;
                case MyRelationsBetweenPlayerAndBlock.Enemies:
                default:
                    category = "enemy"; color = "#FFFF3030"; break;
            }
        }

        /// Every signal (any relation, including the player's own — see
        /// ClassifyRelation) currently visible on the local player's HUD via
        /// antenna/sensor detection, already range-gated by the game itself.
        /// Reads MyHud.LocationMarkers directly rather than scanning all
        /// entities, so nothing is reported that the player couldn't already
        /// see in their own HUD.
        ///
        /// A grid's antenna reports itself via a MyHudEntityParams whose
        /// Entity/Owner/Share are the ANTENNA BLOCK's, not the ship's — a
        /// block commonly has no DisplayName (hence "Unknown Signal") and no
        /// standalone Physics (only top-level entities like grids have real
        /// velocity), and on any grid the player has terminal access to
        /// (their own ship, or one shared with them), the game emits one
        /// such marker PER visible terminal block, each with that block's
        /// own individual ownership — so a single unowned/unclaimed block
        /// anywhere on the player's own ship could otherwise show up as its
        /// own separate "hostile" signal, and a ship with many terminal
        /// blocks could flood the map with near-duplicate markers. This
        /// resolves each marker up to its actual grid, dedupes to one signal
        /// per grid, and reads name/owner/velocity from the grid itself.
        /// </summary>
        public static List<HostileSignalDto> ReadHostileSignals()
        {
            var result = new List<HostileSignalDto>();
            if (!IsSessionReady)
                return result;

            var localIdentityId = MyAPIGateway.Session.Player.IdentityId;
            var playerPos = MyAPIGateway.Session.Player.Character.GetPosition();
            var seenGridIds = new HashSet<long>();
            var reportedSignalIds = new HashSet<long>();

            foreach (var marker in MyHud.LocationMarkers.MarkerEntities)
            {
                var grid = (marker.Entity as IMyCubeBlock)?.CubeGrid ?? marker.Entity as IMyCubeGrid;

                var ownerId = marker.Owner;
                var signalId = marker.EntityId;
                if (grid != null)
                {
                    signalId = grid.EntityId;
                    if (!seenGridIds.Add(signalId))
                        continue; // already reported this ship via another of its blocks

                    if (grid.BigOwners != null && grid.BigOwners.Count > 0)
                        ownerId = grid.BigOwners[0];
                }

                var relation = MyIDModule.GetRelationPlayerBlock(ownerId, localIdentityId, marker.Share);
                ClassifyRelation(relation, out var category, out var color);

                var pos = marker.Position;
                var physics = grid?.Physics ?? marker.Entity?.Physics;
                var velocity = physics?.LinearVelocity ?? Vector3.Zero;
                var acceleration = physics?.LinearAcceleration ?? Vector3.Zero;
                var angular = physics?.AngularVelocity ?? Vector3.Zero;

                // marker.Text is exactly what's drawn on the player's own HUD
                // for this contact (faction tag + ship name, when the
                // broadcasting antenna has "Show ship name" on) — prefer it
                // over the grid's raw DisplayName so the map matches what's
                // actually visible in-game. Pirate/NPC spawns commonly reuse
                // identical names regardless, so signal_id (not name) is
                // what the server keys on; this is purely for display.
                var name = marker.Text != null ? marker.Text.ToString().Trim() : null;
                if (string.IsNullOrWhiteSpace(name))
                    name = grid != null && !string.IsNullOrWhiteSpace(grid.DisplayName)
                        ? grid.DisplayName
                        : $"Unknown Signal {signalId % 10000}";
                if (name.Length > 32)
                    name = name.Substring(0, 32);

                reportedSignalIds.Add(signalId);

                double? uncertaintyRadius = null;
                if (category == "neutral" || category == "enemy")
                {
                    pos = FuzzSignalPosition(signalId, pos, Vector3D.Distance(pos, playerPos), out var bucketRadius);
                    uncertaintyRadius = bucketRadius;
                }

                // After the fuzz, so gravity (direction/strength reveals
                // altitude over a planet) can't be used to back out the
                // contact's true position.
                var gravity = ReadGravityAt(pos);

                result.Add(new HostileSignalDto
                {
                    signal_id = signalId,
                    name = name,
                    color = color,
                    category = category,
                    x = pos.X,
                    y = pos.Y,
                    z = pos.Z,
                    vx = velocity.X,
                    vy = velocity.Y,
                    vz = velocity.Z,
                    ax = acceleration.X,
                    ay = acceleration.Y,
                    az = acceleration.Z,
                    gx = gravity.X,
                    gy = gravity.Y,
                    gz = gravity.Z,
                    wx = angular.X,
                    wy = angular.Y,
                    wz = angular.Z,
                    radius = uncertaintyRadius,
                    is_static = grid?.IsStatic ?? false,
                });
            }

            // Signals from optional mods get the same neutral/enemy fuzz as
            // antenna contacts above.
            void AddFuzzed(HostileSignalDto s)
            {
                if (s.category == "neutral" || s.category == "enemy")
                {
                    var truePos = new Vector3D(s.x, s.y, s.z);
                    var fuzzed = FuzzSignalPosition(s.signal_id, truePos, Vector3D.Distance(truePos, playerPos), out var bucketRadius);
                    s.x = fuzzed.X;
                    s.y = fuzzed.Y;
                    s.z = fuzzed.Z;
                    s.radius = bucketRadius;
                }
                result.Add(s);
            }

            // Spectrum mod detections, if that mod is loaded — no-op otherwise.
            // Antenna contacts win on overlap (they carry a real name/relation).
            foreach (var s in SpectrumBridge.ReadSignals(reportedSignalIds))
                AddFuzzed(s);

            // WeaponCore threat list, if that mod is loaded — tags signals
            // already reported with a threat rating, and adds any threat the
            // other sources missed.
            foreach (var s in WeaponCoreBridge.MergeThreats(result, reportedSignalIds))
                AddFuzzed(s);

            // Drop fuzz state for signals no longer visible, so the cache
            // doesn't grow unbounded over a long session full of transient
            // contacts.
            if (signalFuzzByBucket.Count > 0)
            {
                // The current weapon-lock target keeps its fuzz too even when it
                // isn't a reported signal, or it'd be re-rolled every sync.
                var stale = signalFuzzByBucket.Keys
                    .Where(k => !reportedSignalIds.Contains(k.SignalId) && k.SignalId != lockFuzzId)
                    .ToList();
                foreach (var key in stale)
                    signalFuzzByBucket.Remove(key);
            }

            return result;
        }

        // Per (signal, radius bucket) offset, as a vector of length 0..1 to be
        // scaled by that bucket's radius. See FuzzSignalPosition.
        private static readonly Dictionary<(long SignalId, int Bucket), Vector3D> signalFuzzByBucket =
            new Dictionary<(long SignalId, int Bucket), Vector3D>();
        private static readonly Random fuzzRandom = new Random();
        private static long lockFuzzId;

        /// <summary>
        /// Reports a neutral/enemy signal at a random point within an
        /// uncertainty radius of its true position, instead of the truth.
        /// <paramref name="bucketRadius"/> is that radius, rounded up to a
        /// power-of-two bucket (see SignalUncertaintyBucket) - the only radius
        /// ever exposed.
        ///
        /// The offset is rolled once per (signal_id, bucket) and cached:
        /// - Within a bucket, both the offset and the reported radius are
        ///   constant, so repeated reports neither average out to the truth
        ///   nor leak it through a changing radius (with an exact radius and a
        ///   fixed offset fraction, two reports of a stationary contact at
        ///   different distances would solve for its true position).
        /// - Crossing into another bucket rolls an unrelated offset; coming
        ///   back reuses the old bucket's one, so oscillating across a
        ///   boundary can't farm fresh samples to average.
        /// </summary>
        private static Vector3D FuzzSignalPosition(long signalId, Vector3D truePos, double distanceMeters, out double bucketRadius)
        {
            var bucket = SignalUncertaintyBucket(distanceMeters);
            bucketRadius = Math.Pow(2, bucket);

            var key = (signalId, bucket);
            if (!signalFuzzByBucket.TryGetValue(key, out var offset))
            {
                // Uniform direction on the unit sphere, times a uniform
                // fraction of the radius.
                var theta = fuzzRandom.NextDouble() * Math.PI * 2;
                var z = fuzzRandom.NextDouble() * 2 - 1;
                var r = Math.Sqrt(Math.Max(0, 1 - z * z));
                offset = new Vector3D(r * Math.Cos(theta), r * Math.Sin(theta), z) * fuzzRandom.NextDouble();
                signalFuzzByBucket[key] = offset;
            }

            return truePos + offset * bucketRadius;
        }

        /// <summary>
        /// Uncertainty bucket for a contact at this true distance: the
        /// exponent of the smallest power of two (meters, min 1m) at or above
        /// SignalUncertaintyRadius, so a bucket's radius is never more than
        /// 2x the unrounded one.
        /// </summary>
        private static int SignalUncertaintyBucket(double distanceMeters)
        {
            var radius = SignalUncertaintyRadius(distanceMeters);
            return radius <= 1.0 ? 0 : (int)Math.Ceiling(Math.Log(radius, 2));
        }

        /// <summary>
        /// Unrounded max offset (meters) for a neutral/enemy signal, scaling
        /// with true distance from the player: 10m at 1km, 1km at 10km, 10km
        /// at 100km, 100km at 1000km (d^2/1e5 hits the first two anchors
        /// exactly and coincides with d/10 at 10km, so it hands off to the
        /// linear rule with no discontinuity). Only ever reported rounded up
        /// to its bucket - see SignalUncertaintyBucket.
        /// </summary>
        private static double SignalUncertaintyRadius(double distanceMeters)
        {
            return distanceMeters <= 10000.0
                ? distanceMeters * distanceMeters / 1e5
                : distanceMeters / 10.0;
        }

        /// <summary>
        /// Fires whenever the local player's own GPS list changes in-game
        /// (added, edited, or removed) — including changes this same client
        /// just made, since MyGpsCollection only raises these events once the
        /// game has actually applied the change locally. Session.GPS's public
        /// ModAPI surface (IMyGpsCollection) exposes no events, but client
        /// plugins are full-trust and MyAPIGateway.Session.GPS is always the
        /// concrete Sandbox.Game.Multiplayer.MyGpsCollection instance
        /// underneath (see MySession.GPS => Static.Gpss), so it's cast
        /// directly to reach ListChanged/GpsChanged/GpsAdded. This is what
        /// lets sync be event-driven for pushes instead of only polling.
        /// </summary>
        public static event Action LocalGpsChanged;

        /// <summary>
        /// Raised (on the game thread) when a non-planet voxel map — an
        /// asteroid — is added to the world (streamed in as the player
        /// approaches), and once when the subscription is first established
        /// for a session to cover asteroids that were already loaded. Lets
        /// asteroid sync be event-driven instead of polling.
        /// </summary>
        public static event Action AsteroidsChanged;

        private static object subscribedAsteroidSession;

        /// <summary>
        /// Subscribes to entity-added events for the current session, once
        /// per session. Cheap no-op otherwise — called from Plugin.Update()
        /// like EnsureGpsSubscription.
        /// </summary>
        public static void EnsureAsteroidSubscription()
        {
            var session = MyAPIGateway.Session;
            var entities = MyAPIGateway.Entities;
            if (session == null || entities == null || ReferenceEquals(session, subscribedAsteroidSession))
                return;

            entities.OnEntityAdd -= OnEntityAdded; // no-op if not subscribed; avoids doubling up across sessions
            entities.OnEntityAdd += OnEntityAdded;
            subscribedAsteroidSession = session;
            AsteroidsChanged?.Invoke();
        }

        public static void ShutdownAsteroidSubscription()
        {
            var entities = MyAPIGateway.Entities;
            if (entities != null)
                entities.OnEntityAdd -= OnEntityAdded;
            subscribedAsteroidSession = null;
        }

        private static void OnEntityAdded(IMyEntity entity)
        {
            if (entity is MyVoxelMap)
                AsteroidsChanged?.Invoke();
        }

        // The MyGpsCollection instance currently subscribed to. A new one is
        // created per session (MySession.Static.Gpss), so EnsureGpsSubscription
        // must be able to detect "session changed" and re-subscribe rather than
        // assuming Init() ran exactly once per collection instance.
        private static MyGpsCollection subscribedGpsCollection;

        /// <summary>
        /// (Re)subscribes to the current session's GPS-change events if not
        /// already subscribed to this exact instance. Cheap no-op otherwise —
        /// safe to call every plugin Update() tick. Must run on the game
        /// thread (called from Plugin.Update()).
        /// </summary>
        public static void EnsureGpsSubscription()
        {
            var current = MyAPIGateway.Session?.GPS as MyGpsCollection;
            if (current == null || current == subscribedGpsCollection)
                return;

            if (subscribedGpsCollection != null)
                Unsubscribe(subscribedGpsCollection);

            current.ListChanged += OnGpsCollectionChanged;
            current.GpsChanged += OnGpsCollectionChanged;
            current.GpsAdded += OnGpsCollectionChanged;
            subscribedGpsCollection = current;
        }

        /// <summary>Undoes EnsureGpsSubscription — called from Plugin.Dispose().</summary>
        public static void ShutdownGpsSubscription()
        {
            if (subscribedGpsCollection == null)
                return;
            Unsubscribe(subscribedGpsCollection);
            subscribedGpsCollection = null;
        }

        private static void Unsubscribe(MyGpsCollection collection)
        {
            collection.ListChanged -= OnGpsCollectionChanged;
            collection.GpsChanged -= OnGpsCollectionChanged;
            collection.GpsAdded -= OnGpsCollectionChanged;
        }

        // GpsChanged/GpsAdded pass (identityId, hash); ListChanged passes just
        // (identityId). Only the identityId is needed here — to ignore changes
        // to other players' GPS lists that happen to live in the same session
        // collection — so one handler covers both event shapes.
        private static void OnGpsCollectionChanged(long identityId, int hash) => NotifyIfLocalPlayer(identityId);
        private static void OnGpsCollectionChanged(long identityId) => NotifyIfLocalPlayer(identityId);

        private static void NotifyIfLocalPlayer(long identityId)
        {
            if (!IsSessionReady || identityId != MyAPIGateway.Session.Player.IdentityId)
                return;
            LocalGpsChanged?.Invoke();
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
                if (gps.Name != null && gps.Name.StartsWith(OreDeposits.OreGpsPrefix))
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

        /// <summary>
        /// Total gravity (natural + artificial) at a world position, m/s^2.
        /// Zero if the physics API isn't available.
        /// </summary>
        private static Vector3 ReadGravityAt(Vector3D position)
        {
            try
            {
                var natural = MyAPIGateway.Physics.CalculateNaturalGravityAt(position, out float interference);
                var artificial = MyAPIGateway.Physics.CalculateArtificialGravityAt(position, interference);
                return natural + artificial;
            }
            catch
            {
                return Vector3.Zero;
            }
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
            var angular = physics?.AngularVelocity ?? Vector3.Zero;
            var gravity = ReadGravityAt(pos);

            var request = new PositionSyncRequest
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
                gx = gravity.X,
                gy = gravity.Y,
                gz = gravity.Z,
                wx = angular.X,
                wy = angular.Y,
                wz = angular.Z,
            };

            request.combat = WeaponCoreBridge.ReadCombatState();
            ReadWeaponLock(request);
            return request;
        }

        /// <summary>
        /// Fills in the weapon-lock fields on an already-built
        /// PositionSyncRequest, if the local player currently has a lock —
        /// MyTargetLockingComponent only exists/does anything while wielding
        /// or piloting a lock-capable weapon (e.g. the rocket launcher), so
        /// this is a no-op (weapon_locked stays false) the rest of the time.
        /// This is about the player's OWN lock on something else — nothing
        /// tracks or exposes whether someone else has locked onto the player.
        /// </summary>
        private static void ReadWeaponLock(PositionSyncRequest request)
        {
            VRage.Game.Entity.MyEntity target = null;
            double lockDistance = 0;

            var lockComp = (MyAPIGateway.Session.Player.Character as MyCharacter)?.TargetLockingComp;
            if (lockComp != null && lockComp.IsTargetLocked && lockComp.TargetEntity != null)
            {
                target = lockComp.TargetEntity;
                lockDistance = lockComp.DistanceToLockedTarget;
            }
            else
            {
                // WeaponCore worlds don't use the vanilla lock at all — its
                // focus target is the equivalent. Null without WeaponCore.
                target = WeaponCoreBridge.GetFocusTarget();
                if (target != null)
                    lockDistance = Vector3D.Distance(target.PositionComp.GetPosition(), MyAPIGateway.Session.Player.GetPosition());
            }
            lockFuzzId = 0;
            if (target == null)
                return;

            var targetPos = target.PositionComp.GetPosition();

            // Same fuzz as a neutral/enemy contact reported via
            // ReadHostileSignals — a weapon lock must not leak the exact
            // position the HUD signal itself is hiding. Keyed by the same
            // entity id as that grid's hostile-signal fuzz, so locking onto a
            // contact reports a position consistent with its map marker
            // rather than a second, independently-fuzzed one.
            var grid = (target as IMyCubeBlock)?.CubeGrid ?? target as IMyCubeGrid;
            var ownerId = grid?.BigOwners != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0L;
            var relation = MyIDModule.GetRelationPlayerBlock(ownerId, MyAPIGateway.Session.Player.IdentityId, MyOwnershipShareModeEnum.None);
            if (relation == MyRelationsBetweenPlayerAndBlock.Neutral || relation == MyRelationsBetweenPlayerAndBlock.Enemies)
            {
                var distance = Vector3D.Distance(targetPos, MyAPIGateway.Session.Player.Character.GetPosition());
                lockFuzzId = grid?.EntityId ?? target.EntityId;
                targetPos = FuzzSignalPosition(lockFuzzId, targetPos, distance, out _);
                // The true distance would let repeated reports from a moving
                // player triangulate the real position.
                lockDistance = Vector3D.Distance(targetPos, MyAPIGateway.Session.Player.Character.GetPosition());
            }

            request.weapon_locked = true;
            request.lock_target_name = string.IsNullOrWhiteSpace(target.DisplayName) ? "Unknown Target" : target.DisplayName;
            request.lock_target_x = targetPos.X;
            request.lock_target_y = targetPos.Y;
            request.lock_target_z = targetPos.Z;
            request.lock_distance = lockDistance;
        }

        private static string ToArgbHex(Color c) =>
            $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
