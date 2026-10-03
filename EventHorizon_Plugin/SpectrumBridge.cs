using System;
using System.Collections.Generic;
using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace GpsSyncPlugin
{
    /// <summary>
    /// Optional second signal source: detections from the Spectrum mod
    /// (Workshop 3580536771), which draws its own emissions-based HUD instead
    /// of the vanilla antenna markers GameStateReader.ReadHostileSignals reads.
    /// Entirely passive when Spectrum isn't loaded — the handshake below just
    /// never gets a reply, Ready stays false, and ReadSignals returns nothing.
    ///
    /// Handshake (mirrors Spectrum's own SpectrumApi client): send "init" on
    /// the mod message channel; Spectrum answers with a Dictionary of endpoint
    /// delegates, of which only GetClientDetections is used here.
    /// </summary>
    public static class SpectrumBridge
    {
        private const long Channel = 400790;
        private const int InitRetryFrames = 600; // ~10s at 60 UPS

        private static Func<byte[]> getClientDetections;
        private static object registeredSession;
        private static int framesSinceInit;
        private static bool deserializeFailed;

        public static bool Ready => getClientDetections != null && !deserializeFailed;

        /// <summary>
        /// Call every Update() (game thread). Re-handshakes whenever the
        /// session changes, and re-sends "init" periodically until Spectrum
        /// answers — it may finish loading after we first ask.
        /// </summary>
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
                framesSinceInit = InitRetryFrames; // ask immediately
            }

            if (getClientDetections != null)
                return;

            if (++framesSinceInit >= InitRetryFrames)
            {
                framesSinceInit = 0;
                utilities.SendModMessage(Channel, "init");
            }
        }

        public static void Unload()
        {
            if (registeredSession != null)
                MyAPIGateway.Utilities?.UnregisterMessageHandler(Channel, OnMessage);
            registeredSession = null;
            getClientDetections = null;
        }

        private static void OnMessage(object obj)
        {
            // Our own "init" (and any other client's) echoes back on this
            // channel too — only the endpoint dictionary matters.
            if (!(obj is Dictionary<string, Delegate> endpoints))
                return;
            if (endpoints.TryGetValue("GetClientDetections", out var d) && d is Func<byte[]> f)
            {
                getClientDetections = f;
                Log.Instance?.Info("Spectrum detected — its signals will be synced alongside antenna contacts.");
            }
        }

        /// <summary>
        /// Spectrum's current client-side detections as hostile signals,
        /// skipping any signal_id already in alreadyReported (a grid the
        /// vanilla antenna reading already covered — Spectrum's EmitterId is
        /// the grid's EntityId, same as signal_id there). Must run on the game
        /// thread. Positions are reported as-is; any Pulsar fuzzing is applied
        /// by the caller, exactly like for antenna contacts.
        /// </summary>
        public static List<HostileSignalDto> ReadSignals(HashSet<long> alreadyReported)
        {
            var result = new List<HostileSignalDto>();
            if (!Ready || !GameStateReader.IsSessionReady)
                return result;

            List<SpectrumDetection> detections;
            try
            {
                var bytes = getClientDetections();
                if (bytes == null)
                    return result;
                detections = MyAPIGateway.Utilities.SerializeFromBinary<List<SpectrumDetection>>(bytes);
            }
            catch (Exception e)
            {
                // Most likely Spectrum changed its DetectionData layout. Stop
                // trying rather than logging this every tick.
                deserializeFailed = true;
                Log.Instance?.Error($"Spectrum detections couldn't be read, disabling Spectrum sync: {e.Message}");
                return result;
            }
            if (detections == null)
                return result;

            var identityId = MyAPIGateway.Session.Player.IdentityId;
            foreach (var det in detections)
            {
                if (alreadyReported.Contains(det.EmitterId))
                    continue;
                alreadyReported.Add(det.EmitterId);

                var relation = det.SelfOwned ? MyRelationsBetweenPlayerAndBlock.Owner : RelationFor(det.FactionTag, identityId);
                GameStateReader.ClassifyRelation(relation, out var category, out var color);

                result.Add(new HostileSignalDto
                {
                    signal_id = det.EmitterId,
                    name = NameFor(det),
                    color = color,
                    category = category,
                    x = det.Position.X,
                    y = det.Position.Y,
                    z = det.Position.Z,
                    vx = det.Velocity.X,
                    vy = det.Velocity.Y,
                    vz = det.Velocity.Z,
                    is_static = IsStatic(det),
                });
            }

            return result;
        }

        // Spectrum only gives a faction tag, not an ownership relation, so
        // derive one the same way the game does for factions: faction-to-
        // faction relation if the player is in one, otherwise the player's
        // personal reputation with that faction (vanilla thresholds ±500).
        private static MyRelationsBetweenPlayerAndBlock RelationFor(string tag, long identityId)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return MyRelationsBetweenPlayerAndBlock.NoOwnership;

            var factions = MyAPIGateway.Session.Factions;
            var other = factions.TryGetFactionByTag(tag);
            if (other == null)
                return MyRelationsBetweenPlayerAndBlock.NoOwnership;

            var mine = factions.TryGetPlayerFaction(identityId);
            if (mine != null)
            {
                if (mine.FactionId == other.FactionId)
                    return MyRelationsBetweenPlayerAndBlock.FactionShare;
                switch (factions.GetRelationBetweenFactions(mine.FactionId, other.FactionId))
                {
                    case MyRelationsBetweenFactions.Enemies:
                        return MyRelationsBetweenPlayerAndBlock.Enemies;
                    case MyRelationsBetweenFactions.Neutral:
                        return MyRelationsBetweenPlayerAndBlock.Neutral;
                    default:
                        return MyRelationsBetweenPlayerAndBlock.Friends;
                }
            }

            var reputation = factions.GetReputationBetweenPlayerAndFaction(identityId, other.FactionId);
            if (reputation <= -500)
                return MyRelationsBetweenPlayerAndBlock.Enemies;
            if (reputation >= 500)
                return MyRelationsBetweenPlayerAndBlock.Friends;
            return MyRelationsBetweenPlayerAndBlock.Neutral;
        }

        private static string NameFor(SpectrumDetection det)
        {
            var name = det.DetailText?.Split('\n')[0].Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = !string.IsNullOrWhiteSpace(det.FactionTag)
                    ? $"{det.FactionTag} Signal"
                    : $"Unknown Signal {det.EmitterId % 10000}";
            return name.Length > 32 ? name.Substring(0, 32) : name;
        }

        // A detection can be far outside the client's streaming range, where
        // the grid entity doesn't exist locally — fall back to "not moving"
        // as the station heuristic then.
        private static bool IsStatic(SpectrumDetection det)
        {
            if (MyAPIGateway.Entities.GetEntityById(det.EmitterId) is IMyCubeGrid grid)
                return grid.IsStatic;
            return det.Velocity.LengthSquared() < 0.25;
        }

        // Wire-compatible copy of Spectrum's DetectionData (Api/SpectrumApi.cs)
        // — protobuf matches on the ProtoMember numbers, not names/types'
        // assembly, so this deserializes what Spectrum serializes.
        [ProtoContract]
        public struct SpectrumDetection
        {
            [ProtoMember(1)] public long EmitterId;
            [ProtoMember(2)] public Vector3D Position;
            [ProtoMember(3)] public Vector3D Velocity;
            [ProtoMember(4)] public float Strength;
            [ProtoMember(5)] public string FactionTag;
            [ProtoMember(6)] public Dictionary<string, float> EmissionTags;
            [ProtoMember(7)] public int DetectedAt;
            [ProtoMember(8)] public bool SelfOwned;
            [ProtoMember(9)] public string DetailText;
        }
    }
}
