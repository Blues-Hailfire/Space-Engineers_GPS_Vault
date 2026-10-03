using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace GpsSyncPlugin
{
    /// <summary>
    /// Optional WeaponCore (CoreSystems) integration. Entirely passive when
    /// WeaponCore isn't loaded — the handshake never gets a reply, Ready stays
    /// false, and every read returns null/empty.
    ///
    /// Only read-only endpoints are used, and only ones whose data WeaponCore
    /// fills in on a client too (its AI loop and projectile sim run on every
    /// machine, and focus is synced from the server): your focus target, your
    /// construct's DPS, missiles locked on you, missiles in flight, your
    /// grid's threat list / in-range flags, and a grid's optimal DPS. Every
    /// WeaponCore setter is server-only and is never called.
    ///
    /// Handshake mirrors WeaponCore's own CoreSystemsApiBase client: send
    /// "ApiEndpointRequest" on its channel; it answers with a read-only
    /// dictionary of endpoint delegates.
    /// </summary>
    public static class WeaponCoreBridge
    {
        private const long Channel = 67549756549;
        private const int InitRetryFrames = 600; // ~10s at 60 UPS
        private const int MaxMissilesReported = 100;

        private static Func<MyEntity, int, MyEntity> getAiFocus;
        private static Func<MyEntity, float> getConstructEffectiveDps;
        private static Func<MyEntity, float> getOptimalDps;
        private static Func<MyEntity, MyTuple<bool, int, int>> getProjectilesLockedOn;
        private static Action<MyEntity, ICollection<Vector3D>> getProjectilesLockedOnPos;
        private static Action<ICollection<MyTuple<ulong, Vector3D, int, long>>> getAllSmartProjectiles;
        private static Action<MyEntity, ICollection<MyTuple<MyEntity, float>>> getSortedThreats;
        private static Func<MyEntity, MyTuple<bool, bool>> isInRange;

        private static object registeredSession;
        private static int framesSinceInit;
        private static bool callFailed;

        public static bool Ready => getAiFocus != null && !callFailed;

        /// <summary>Call every Update() (game thread) — same pattern as SpectrumBridge.Update.</summary>
        public static void Update()
        {
            var utilities = MyAPIGateway.Utilities;
            var session = MyAPIGateway.Session;
            if (utilities == null || session == null)
                return;

            if (!ReferenceEquals(session, registeredSession))
            {
                Unload();
                utilities.RegisterMessageHandler(Channel, OnMessage);
                registeredSession = session;
                framesSinceInit = InitRetryFrames;
            }

            if (getAiFocus != null)
                return;

            if (++framesSinceInit >= InitRetryFrames)
            {
                framesSinceInit = 0;
                utilities.SendModMessage(Channel, "ApiEndpointRequest");
            }
        }

        public static void Unload()
        {
            if (registeredSession != null)
                MyAPIGateway.Utilities?.UnregisterMessageHandler(Channel, OnMessage);
            registeredSession = null;
            getAiFocus = null;
            getConstructEffectiveDps = null;
            getOptimalDps = null;
            getProjectilesLockedOn = null;
            getProjectilesLockedOnPos = null;
            getAllSmartProjectiles = null;
            getSortedThreats = null;
            isInRange = null;
        }

        private static void OnMessage(object obj)
        {
            // Our own "ApiEndpointRequest" string echoes back on this channel.
            if (!(obj is IReadOnlyDictionary<string, Delegate> d))
                return;

            getAiFocus = Get<Func<MyEntity, int, MyEntity>>(d, "GetAiFocusBase");
            getConstructEffectiveDps = Get<Func<MyEntity, float>>(d, "GetConstructEffectiveDpsBase");
            getOptimalDps = Get<Func<MyEntity, float>>(d, "GetOptimalDpsBase");
            getProjectilesLockedOn = Get<Func<MyEntity, MyTuple<bool, int, int>>>(d, "GetProjectilesLockedOnBase");
            getProjectilesLockedOnPos = Get<Action<MyEntity, ICollection<Vector3D>>>(d, "GetProjectilesLockedOnPos");
            getAllSmartProjectiles = Get<Action<ICollection<MyTuple<ulong, Vector3D, int, long>>>>(d, "GetAllSmartProjectiles");
            getSortedThreats = Get<Action<MyEntity, ICollection<MyTuple<MyEntity, float>>>>(d, "GetSortedThreatsBase");
            isInRange = Get<Func<MyEntity, MyTuple<bool, bool>>>(d, "IsInRangeBase");

            if (getAiFocus != null)
                Log.Instance?.Info("WeaponCore detected — syncing focus target, DPS, missiles and threats.");
        }

        private static T Get<T>(IReadOnlyDictionary<string, Delegate> d, string name) where T : class
        {
            return d.TryGetValue(name, out var del) ? del as T : null;
        }

        // Any WeaponCore call can throw if its internals change between
        // versions — disable the whole bridge on the first failure rather
        // than spamming the log (or worse) every tick.
        private static T Guard<T>(Func<T> call, T fallback)
        {
            if (!Ready)
                return fallback;
            try
            {
                return call();
            }
            catch (Exception e)
            {
                callFailed = true;
                Log.Instance?.Error($"WeaponCore call failed, disabling WeaponCore sync: {e.Message}");
                return fallback;
            }
        }

        /// <summary>
        /// The top-level grid the local player is piloting/controlling, or
        /// null on foot — every "your ship" read keys off this. Game thread.
        /// </summary>
        public static MyEntity GetControlledGrid()
        {
            var controlled = MyAPIGateway.Session?.Player?.Controller?.ControlledEntity?.Entity;
            var grid = (controlled as IMyCubeBlock)?.CubeGrid ?? controlled as IMyCubeGrid;
            return grid?.GetTopMostParent() as MyEntity;
        }

        /// <summary>
        /// WeaponCore's focus target for your ship — its replacement for the
        /// vanilla target lock, which stays unused in WeaponCore worlds.
        /// Null on foot, with no focus, or without WeaponCore. Game thread.
        /// </summary>
        public static MyEntity GetFocusTarget()
        {
            var grid = GetControlledGrid();
            if (grid == null)
                return null;
            return Guard(() => getAiFocus(grid, 0), null);
        }

        /// <summary>
        /// Your ship's live combat picture, or null without WeaponCore.
        /// Ship-specific fields are null on foot; missiles-in-flight don't
        /// depend on a ship. Game thread.
        /// </summary>
        public static CombatStateDto ReadCombatState()
        {
            if (!Ready)
                return null;

            var state = new CombatStateDto();
            var grid = GetControlledGrid();
            if (grid != null)
            {
                if (getConstructEffectiveDps != null)
                    state.ship_dps = Guard(() => getConstructEffectiveDps(grid), 0f);
                if (getOptimalDps != null)
                    state.ship_peak_dps = Guard(() => getOptimalDps(grid), 0f);

                if (getProjectilesLockedOn != null)
                {
                    var locked = Guard(() => getProjectilesLockedOn(grid), default(MyTuple<bool, int, int>));
                    state.incoming_missiles = locked.Item1 ? locked.Item2 : 0;
                }
                if (state.incoming_missiles > 0 && getProjectilesLockedOnPos != null)
                {
                    var positions = new List<Vector3D>();
                    Guard(() => { getProjectilesLockedOnPos(grid, positions); return true; }, false);
                    foreach (var p in positions)
                        state.incoming_positions.Add(new[] { p.X, p.Y, p.Z });
                }

                if (isInRange != null)
                {
                    var range = Guard(() => isInRange(grid), default(MyTuple<bool, bool>));
                    state.in_range_priority = range.Item1;
                    state.in_range_other = range.Item2;
                }
            }

            if (getAllSmartProjectiles != null)
            {
                var projectiles = new List<MyTuple<ulong, Vector3D, int, long>>();
                Guard(() => { getAllSmartProjectiles(projectiles); return true; }, false);
                var factions = MyAPIGateway.Session.Factions;
                foreach (var p in projectiles)
                {
                    if (state.missiles.Count >= MaxMissilesReported)
                        break;
                    state.missiles.Add(new MissileDto
                    {
                        // ulong id — exceeds JS's safe integer range, so a string.
                        id = p.Item1.ToString(),
                        x = p.Item2.X,
                        y = p.Item2.Y,
                        z = p.Item2.Z,
                        faction_tag = p.Item4 != 0 ? factions.TryGetFactionById(p.Item4)?.Tag : null,
                    });
                }
            }

            return state;
        }

        /// <summary>
        /// Your grid's WeaponCore threat list, highest first: each entry's
        /// top-level entity and its offense rating. Only grids loaded on this
        /// client, and only if your ship carries WeaponCore weapons. Game thread.
        /// </summary>
        public static List<MyTuple<MyEntity, float>> ReadThreats()
        {
            var result = new List<MyTuple<MyEntity, float>>();
            var grid = GetControlledGrid();
            if (grid == null || getSortedThreats == null)
                return result;
            Guard(() => { getSortedThreats(grid, result); return true; }, false);
            return result;
        }

        /// <summary>
        /// Folds the threat list into hostile signals: sets `threat` on any
        /// signal already reported (by antenna or Spectrum), and returns a new
        /// signal for each threat that wasn't — a real, locally-loaded enemy
        /// grid the other sources missed. New signals are unfuzzed; the Pulsar
        /// caller fuzzes neutral/enemy ones like every other source. Game thread.
        /// </summary>
        public static List<HostileSignalDto> MergeThreats(List<HostileSignalDto> existing, HashSet<long> alreadyReported)
        {
            var added = new List<HostileSignalDto>();
            var threats = ReadThreats();
            if (threats.Count == 0)
                return added;

            var byId = new Dictionary<long, HostileSignalDto>();
            foreach (var s in existing)
                byId[s.signal_id] = s;

            var localIdentityId = MyAPIGateway.Session.Player.IdentityId;
            foreach (var t in threats)
            {
                var entity = t.Item1?.GetTopMostParent() as MyEntity;
                if (entity == null || entity.MarkedForClose)
                    continue;

                if (byId.TryGetValue(entity.EntityId, out var known))
                {
                    known.threat = t.Item2;
                    continue;
                }
                if (!alreadyReported.Add(entity.EntityId))
                    continue;

                var grid = entity as IMyCubeGrid;
                var ownerId = grid?.BigOwners != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0L;
                var relation = MyIDModule.GetRelationPlayerBlock(ownerId, localIdentityId, MyOwnershipShareModeEnum.None);
                GameStateReader.ClassifyRelation(relation, out var category, out var color);

                var pos = entity.PositionComp.GetPosition();
                var velocity = entity.Physics?.LinearVelocity ?? Vector3.Zero;
                var name = string.IsNullOrWhiteSpace(entity.DisplayName) ? $"Threat {entity.EntityId % 10000}" : entity.DisplayName;

                added.Add(new HostileSignalDto
                {
                    signal_id = entity.EntityId,
                    name = name.Length > 32 ? name.Substring(0, 32) : name,
                    color = color,
                    category = category,
                    x = pos.X,
                    y = pos.Y,
                    z = pos.Z,
                    vx = velocity.X,
                    vy = velocity.Y,
                    vz = velocity.Z,
                    is_static = grid?.IsStatic ?? false,
                    threat = t.Item2,
                });
            }

            return added;
        }
    }
}
