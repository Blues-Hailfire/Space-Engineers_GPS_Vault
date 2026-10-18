using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRageMath;

namespace GpsSyncPlugin
{
    /// <summary>
    /// Optional ore source: results from the Asteroid Scanner mod (Workshop
    /// 3580646786), which turns the ore detector into a long-range asteroid
    /// scanner with its own HUD instead of the vanilla MyHud.OreMarkers that
    /// OreDeposits.ReadOreDeposits reads. Entirely passive when the mod isn't
    /// loaded — its session component is never found and every read returns
    /// nothing.
    ///
    /// The mod's own scan API (AddScanCallback) is only registered on the
    /// server, so on a dedicated server a client never gets it. Instead this
    /// reads the mod's client-side state by reflection — the same per-asteroid
    /// records its HUD draws from (Session.Asteroids, every ScannableAsteroid
    /// within its 35 km generation radius) plus VoxelScanner.LatestDepositScan
    /// for a detailed scan's individual veins.
    ///
    /// Only asteroids the player has actually scanned are reported: the mod
    /// fills Ore1/Ore2 from its procedural generator for every asteroid in
    /// range, scanned or not, and only reveals them on the HUD after a scan.
    /// </summary>
    public static class AsteroidScannerBridge
    {
        private const string SessionTypeName = "AsteroidScanner.Data.Scripts.AsteroidScanner.Session";
        private const string VoxelScannerTypeName = "AsteroidScanner.Data.Scripts.AsteroidScanner.Scan.VoxelScanner";

        // Prefix for this bridge's deposit_ids. Kept apart from the vanilla
        // ore detector's (the asteroid's bare voxel name, see
        // OreDeposits.ReadOreDeposits) because upsert_ore_deposits replaces
        // the whole row: sharing a key would have each source overwrite the
        // other every ores tick. Both still share voxel_name, which is what
        // the map links an asteroid's ore points by.
        private const string DepositIdPrefix = "ascan:";

        // Matches the mod's own ScanType enum (None, Basic, Detailed).
        private const int ScanTypeNone = 0;
        private const int ScanTypeDetailed = 2;

        // The server keeps at most 500 locations per deposit.
        private const int MaxLocations = 500;

        private static readonly FieldInfo SessionComponentsField =
            typeof(MySession).GetField("m_sessionComponents", BindingFlags.Instance | BindingFlags.NonPublic);

        // Mod assemblies are never unloaded, so after rejoining a world the
        // previous session's Session type (and its stale static Instance) is
        // still around — the component is looked up in the live MySession's
        // own component list instead, and re-resolved whenever that changes.
        private const int ResolveRetryFrames = 600; // ~10s at 60 UPS

        private static object resolvedForSession;
        private static int framesSinceResolve;
        private static object scannerSession;
        private static PropertyInfo asteroidsProperty;
        private static FieldInfo generatorField;
        private static FieldInfo latestDepositScanField;
        private static bool readFailed;

        // Asteroid field accessors, resolved once from the first
        // ScannableAsteroid seen (the type lives in the mod assembly).
        private static Type asteroidType;
        private static FieldInfo fEntityId, fPosition, fRadius, fCellId, fScanType, fName, fOre1, fOre2, fScanData;
        private static FieldInfo fScanVolume, fScanOre;

        // Detailed-scan veins per asteroid EntityId. The mod only keeps the
        // most recent detailed scan's deposits (a single static, with no
        // record of which asteroid it was for), so each one is captured and
        // attributed as it appears, and outlives the next scan.
        private static readonly Dictionary<long, List<OreLocationDto>> veinsByAsteroid = new Dictionary<long, List<OreLocationDto>>();
        private static object lastDepositScan;

        // EntityId -> real voxel StorageName, resolved from the loaded voxel
        // map or, failing that, the mod's own generator (same
        // "Asteroid_{cell}_{index}_{seed}" name and EntityId hash as vanilla's
        // MyProceduralAsteroidCellGenerator, so it's the name the asteroid
        // will have once it streams in). Null when neither worked yet.
        private static readonly Dictionary<long, string> storageNames = new Dictionary<long, string>();

        // Storage names already pushed as asteroids this session, so the
        // asteroid rows for scanned-but-not-loaded rocks go up once.
        private static readonly HashSet<string> pushedAsteroids = new HashSet<string>();

        public static bool Ready => scannerSession != null && !readFailed;

        /// <summary>
        /// Call every Update() (game thread). Resolves the mod's session
        /// component when the session changes, and attributes any new detailed
        /// scan's veins to the asteroid they belong to.
        /// </summary>
        public static void Update()
        {
            var session = MySession.Static;
            if (session == null)
                return;

            if (!ReferenceEquals(session, resolvedForSession))
            {
                Reset();
                resolvedForSession = session;
                framesSinceResolve = ResolveRetryFrames; // look immediately
            }

            // Mod session components may register after MySession.Static is
            // set, so keep looking until found (a no-op without the mod
            // beyond this occasional scan).
            if (scannerSession == null && ++framesSinceResolve >= ResolveRetryFrames)
            {
                framesSinceResolve = 0;
                Resolve(session);
            }

            if (!Ready || !GameStateReader.IsSessionReady)
                return;

            try
            {
                CaptureLatestDepositScan();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        public static void Unload()
        {
            Reset();
            resolvedForSession = null;
        }

        private static void Reset()
        {
            scannerSession = null;
            // A rejoin loads a fresh copy of the mod assembly, so the old
            // ScannableAsteroid FieldInfos won't fit the new session's objects.
            asteroidType = null;
            readFailed = false;
            lastDepositScan = null;
            veinsByAsteroid.Clear();
            storageNames.Clear();
            pushedAsteroids.Clear();
        }

        private static void Resolve(MySession session)
        {
            if (!(SessionComponentsField?.GetValue(session) is IEnumerable components))
                return;

            foreach (var entry in components)
            {
                if (!(entry is KeyValuePair<Type, MySessionComponentBase> kv) || kv.Value == null)
                    continue;
                var type = kv.Value.GetType();
                if (type.FullName != SessionTypeName)
                    continue;

                asteroidsProperty = type.GetProperty("Asteroids", BindingFlags.Instance | BindingFlags.Public);
                generatorField = type.GetField("_generator", BindingFlags.Instance | BindingFlags.NonPublic);
                latestDepositScanField = type.Assembly.GetType(VoxelScannerTypeName)
                    ?.GetField("LatestDepositScan", BindingFlags.Static | BindingFlags.Public);
                if (asteroidsProperty == null)
                {
                    Log.Instance?.Warn("Asteroid Scanner found but its layout changed — scan results won't be synced.");
                    return;
                }

                scannerSession = kv.Value;
                Log.Instance?.Info("Asteroid Scanner detected — scanned asteroids will be synced as ore points.");
                return;
            }
        }

        private static void Fail(Exception e)
        {
            // Most likely the mod changed its internals. Stop trying rather
            // than logging this every tick.
            readFailed = true;
            Log.Instance?.Error($"Asteroid Scanner results couldn't be read, disabling its sync: {e.Message}");
        }

        /// <summary>
        /// One ore point per scanned asteroid, in OreDeposits'
        /// OreDepositDto shape. Basic scans carry the mod's up-to-two rare
        /// ores; detailed scans carry every rare ore's share of the rock's
        /// volume (in the name) and the individual veins (in .locations).
        /// Must run on the game thread.
        /// </summary>
        public static List<OreDepositDto> ReadScannedDeposits()
        {
            var result = new List<OreDepositDto>();
            if (!Ready || !GameStateReader.IsSessionReady)
                return result;

            try
            {
                foreach (var a in ScannedAsteroids())
                {
                    var entityId = (long)fEntityId.GetValue(a);
                    var position = (Vector3D)fPosition.GetValue(a);
                    var designation = fName.GetValue(a) as string ?? $"AST-{entityId % 100000}";
                    var storageName = StorageNameFor(a, entityId);

                    var ores = new List<string>();
                    string details = null;
                    var scanData = fScanData.GetValue(a);
                    var detailed = (int)fScanType.GetValue(a) == ScanTypeDetailed && scanData != null;
                    if (detailed)
                    {
                        var volume = (float)fScanVolume.GetValue(scanData);
                        var oreVolumes = OreVolumes(scanData);
                        foreach (var kv in oreVolumes.OrderByDescending(kv => kv.Value))
                            ores.Add(kv.Key);
                        if (volume > 0 && oreVolumes.Count > 0)
                            details = string.Join(",", oreVolumes
                                .OrderByDescending(kv => kv.Value)
                                .Select(kv => $"{kv.Key} {kv.Value / volume * 100:0.#}%"));
                    }
                    else
                    {
                        if (fOre1.GetValue(a) is string ore1)
                            ores.Add(ore1);
                        if (fOre2.GetValue(a) is string ore2)
                            ores.Add(ore2);
                    }

                    var oreList = string.Join(",", ores);
                    var name = $"AST {designation} - {details ?? (oreList.Length > 0 ? oreList : "No rare ore")}";
                    veinsByAsteroid.TryGetValue(entityId, out var veins);

                    result.Add(new OreDepositDto
                    {
                        deposit_id = DepositIdPrefix + entityId,
                        name = name.Length > 96 ? name.Substring(0, 96) : name,
                        ores = oreList,
                        voxel_name = storageName ?? "",
                        is_planet = false,
                        x = position.X,
                        y = position.Y,
                        z = position.Z,
                        locations = detailed ? veins : null,
                    });
                }
            }
            catch (Exception e)
            {
                Fail(e);
                result.Clear();
            }

            return result;
        }

        /// <summary>
        /// Asteroid rows for scanned asteroids that aren't streamed in on
        /// this client (so GameStateReader.ReadAsteroids can't see them),
        /// named with their real storage name so they're the same row the
        /// vanilla asteroid sync writes once someone gets close. Each goes up
        /// once per session. Must run on the game thread.
        /// </summary>
        public static List<AsteroidDto> ReadUnloadedScannedAsteroids()
        {
            var result = new List<AsteroidDto>();
            if (!Ready || !GameStateReader.IsSessionReady)
                return result;

            try
            {
                foreach (var a in ScannedAsteroids())
                {
                    var entityId = (long)fEntityId.GetValue(a);
                    if (MyEntities.GetEntityById(entityId) is MyVoxelBase)
                        continue; // vanilla asteroid sync already covers it
                    var storageName = StorageNameFor(a, entityId);
                    if (storageName == null || pushedAsteroids.Contains(storageName))
                        continue;

                    var position = (Vector3D)fPosition.GetValue(a);
                    result.Add(new AsteroidDto
                    {
                        name = storageName,
                        x = position.X,
                        y = position.Y,
                        z = position.Z,
                        // The mod's Radius is a third of the bounding box's
                        // half-extent (its HUD bracket size), not the rock's.
                        radius = (float)fRadius.GetValue(a) * 3,
                    });
                }
            }
            catch (Exception e)
            {
                Fail(e);
                result.Clear();
            }

            return result;
        }

        /// <summary>Marks asteroids as pushed once the server accepted them.</summary>
        public static void MarkAsteroidsPushed(IEnumerable<AsteroidDto> asteroids)
        {
            foreach (var a in asteroids)
                pushedAsteroids.Add(a.name);
        }

        private static IEnumerable<object> AllAsteroids()
        {
            if (!(asteroidsProperty.GetValue(scannerSession) is IDictionary cells))
                yield break;

            foreach (var cell in cells.Values)
            {
                if (!(cell is IEnumerable list))
                    continue;
                foreach (var a in list)
                {
                    if (a == null)
                        continue;
                    if (a.GetType() != asteroidType)
                        ResolveAsteroidFields(a.GetType());
                    yield return a;
                }
            }
        }

        private static List<object> ScannedAsteroids()
        {
            // Materialized: the mod's Generate() rebuilds these lists every
            // 120 frames, and callers may touch other game state meanwhile.
            return AllAsteroids().Where(a => (int)fScanType.GetValue(a) != ScanTypeNone).ToList();
        }

        private static void ResolveAsteroidFields(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            FieldInfo Get(string name) => type.GetField(name, flags)
                ?? throw new MissingFieldException(type.FullName, name);

            fEntityId = Get("EntityId");
            fPosition = Get("Position");
            fRadius = Get("Radius");
            fCellId = Get("CellId");
            fScanType = Get("ScanType");
            fName = Get("Name");
            fOre1 = Get("Ore1");
            fOre2 = Get("Ore2");
            fScanData = Get("ScanData");

            var scanDataType = fScanData.FieldType;
            fScanVolume = scanDataType.GetField("Volume", flags) ?? throw new MissingFieldException(scanDataType.FullName, "Volume");
            fScanOre = scanDataType.GetField("Ore", flags) ?? throw new MissingFieldException(scanDataType.FullName, "Ore");
            asteroidType = type;
        }

        // ScanData.Ore is a VRage SerializableDictionary<string, float>,
        // whose .Dictionary is the plain Dictionary underneath.
        private static Dictionary<string, float> OreVolumes(object scanData)
        {
            var ore = fScanOre.GetValue(scanData);
            var dict = ore?.GetType().GetProperty("Dictionary")?.GetValue(ore) as Dictionary<string, float>;
            return dict ?? new Dictionary<string, float>();
        }

        private static string StorageNameFor(object asteroid, long entityId)
        {
            if (storageNames.TryGetValue(entityId, out var cached) && cached != null)
                return cached;

            string name = null;
            if (MyEntities.GetEntityById(entityId) is MyVoxelBase voxel)
                name = voxel.StorageName;
            else
                name = StorageNameFromGenerator(entityId, (Vector3I)fCellId.GetValue(asteroid));

            storageNames[entityId] = name;
            return name;
        }

        // Asks the mod's ProceduralGenerator for the asteroid's seed record
        // (GetAsteroidById returns a nullable MyTuple<Asteroid, ...>) and
        // rebuilds the storage name from it. Null if anything's missing.
        private static string StorageNameFromGenerator(long entityId, Vector3I cellId)
        {
            var generator = generatorField?.GetValue(scannerSession);
            var method = generator?.GetType().GetMethod("GetAsteroidById", BindingFlags.Instance | BindingFlags.Public);
            if (method == null)
                return null;

            var tuple = method.Invoke(generator, new object[] { entityId, cellId });
            var seed = tuple?.GetType().GetField("Item1")?.GetValue(tuple);
            if (seed == null)
                return null;

            var seedType = seed.GetType();
            var cell = seedType.GetField("CellId")?.GetValue(seed);
            var index = seedType.GetField("Index")?.GetValue(seed);
            var objectSeed = seedType.GetField("Seed")?.GetValue(seed);
            if (!(cell is Vector3I c) || index == null || objectSeed == null)
                return null;
            return $"Asteroid_{c.X}_{c.Y}_{c.Z}_{index}_{objectSeed}";
        }

        // LatestDepositScan is filled from a background thread while a
        // detailed scan runs, and only names ores, not the asteroid. Each new
        // one is attributed to the nearest asteroid whose bounds contain the
        // veins' centroid — a detailed scan is only possible within 5 km of
        // a loaded rock, so the match is unambiguous in practice.
        private static void CaptureLatestDepositScan()
        {
            var scan = latestDepositScanField?.GetValue(null);
            if (scan == null || ReferenceEquals(scan, lastDepositScan))
                return;
            if (!(scan is Dictionary<string, List<BoundingSphereD>> deposits) || deposits.Count == 0)
                return;
            lastDepositScan = scan;

            var spheres = deposits.SelectMany(kv => kv.Value.Select(s => (ore: kv.Key, sphere: s))).ToList();
            var centroid = Vector3D.Zero;
            foreach (var s in spheres)
                centroid += s.sphere.Center;
            centroid /= spheres.Count;

            object best = null;
            var bestDistance = double.MaxValue;
            foreach (var a in AllAsteroids())
            {
                var distance = Vector3D.Distance((Vector3D)fPosition.GetValue(a), centroid);
                var halfExtent = (float)fRadius.GetValue(a) * 3;
                if (distance <= halfExtent && distance < bestDistance)
                {
                    best = a;
                    bestDistance = distance;
                }
            }
            if (best == null)
                return;

            // Biggest veins first, so the cap drops the specks.
            veinsByAsteroid[(long)fEntityId.GetValue(best)] = spheres
                .OrderByDescending(s => s.sphere.Radius)
                .Take(MaxLocations)
                .Select(s => new OreLocationDto { ore = s.ore, x = s.sphere.Center.X, y = s.sphere.Center.Y, z = s.sphere.Center.Z })
                .ToList();
        }
    }
}
