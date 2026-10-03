using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace GpsSyncPlugin
{
    /// <summary>
    /// Serves planet terrain on demand as cube-sphere LOD tiles. The web map
    /// asks (via the server) for the tiles it needs to draw the terrain under
    /// its camera — coarse ones from orbit, fine ones near the ground — and
    /// this samples each one from the planet and sends it back.
    ///
    /// Tile scheme (mirrored exactly in templates/map.html): the planet is
    /// wrapped in a cube; face f in 0..5 is split into a 2^level x 2^level
    /// grid of tiles; tile (level, x, y) spans u,v in [-1,1] sub-ranges, and
    /// sample (i, j) of its 65 x 65 grid sits in direction
    /// normalize(N[f] + A[f]*u + B[f]*v). Each sample is the distance from the
    /// planet's center to its surface along that direction, stored as uint16
    /// scaled between the tile's own min and max.
    ///
    /// Distances come from MyPlanet.GetClosestSurfacePointGlobal, which
    /// projects onto the planet's own height data, not onto loaded voxel or
    /// collision geometry — so it works for any planet, anywhere, whether or
    /// not the player is near it. Planets are sampled a few thousand points at
    /// a time in Advance(), a small time slice per frame.
    /// </summary>
    public static class TerrainTiles
    {
        public const int Grid = 65;

        private static readonly double[][] FaceN =
        {
            new[] { 1.0, 0, 0 }, new[] { -1.0, 0, 0 }, new[] { 0, 1.0, 0 },
            new[] { 0, -1.0, 0 }, new[] { 0, 0, 1.0 }, new[] { 0, 0, -1.0 },
        };
        private static readonly double[][] FaceA =
        {
            new[] { 0, 0, -1.0 }, new[] { 0, 0, 1.0 }, new[] { 1.0, 0, 0 },
            new[] { 1.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { -1.0, 0, 0 },
        };
        private static readonly double[][] FaceB =
        {
            new[] { 0, 1.0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, 0, -1.0 },
            new[] { 0, 0, 1.0 }, new[] { 0, 1.0, 0 }, new[] { 0, 1.0, 0 },
        };

        private class Job
        {
            public TerrainRequestDto Request;
            public MyPlanet Planet;
            public Vector3D Center;
            public double ProbeRadius;
            public float[] Distances = new float[Grid * Grid];
            public int Next;
        }

        private static readonly ConcurrentQueue<TerrainRequestDto> pending = new ConcurrentQueue<TerrainRequestDto>();
        private static readonly ConcurrentQueue<TerrainTileDto> completed = new ConcurrentQueue<TerrainTileDto>();
        private static readonly HashSet<string> queuedKeys = new HashSet<string>(); // lock(queuedKeys)
        private static readonly Dictionary<string, MyPlanet> planetCache = new Dictionary<string, MyPlanet>();
        private static DateTime lastPlanetRefreshUtc = DateTime.MinValue;
        private static Job current;

        /// <summary>Number of requests queued or in progress (approximate).</summary>
        public static int Backlog => pending.Count + (current != null ? 1 : 0) + completed.Count;

        /// <summary>Adds requests from the server (any thread). Duplicates are ignored.</summary>
        public static void Enqueue(IEnumerable<TerrainRequestDto> requests)
        {
            foreach (var r in requests)
            {
                if (r == null || string.IsNullOrEmpty(r.planet) || r.face < 0 || r.face > 5 || r.level < 0 || r.level > 20)
                    continue;
                var key = Key(r);
                lock (queuedKeys)
                {
                    if (!queuedKeys.Add(key))
                        continue;
                }
                pending.Enqueue(r);
            }
        }

        /// <summary>Up to <paramref name="max"/> finished tiles waiting to upload (any thread).</summary>
        public static List<TerrainTileDto> TakeCompleted(int max)
        {
            var list = new List<TerrainTileDto>();
            while (list.Count < max && completed.TryDequeue(out var tile))
                list.Add(tile);
            return list;
        }

        private static string Key(TerrainRequestDto r) => $"{r.planet}|{r.face}.{r.level}.{r.x}.{r.y}";

        /// <summary>
        /// Samples queued tiles for up to <paramref name="budgetMs"/> ms.
        /// Must run on the game thread — called from Plugin.Update().
        /// </summary>
        public static void Advance(double budgetMs)
        {
            if (!GameStateReader.IsSessionReady)
                return;

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < budgetMs)
            {
                if (current == null)
                {
                    if (!pending.TryDequeue(out var request))
                        return;
                    current = StartJob(request);
                    if (current == null)
                    {
                        Release(request);
                        continue; // unknown planet etc. — drop it
                    }
                }

                var job = current;
                int total = Grid * Grid;
                try
                {
                    var req = job.Request;
                    double size = 1.0 / (1 << req.level);
                    double u0 = -1 + 2.0 * req.x * size, u1 = -1 + 2.0 * (req.x + 1) * size;
                    double v0 = -1 + 2.0 * req.y * size, v1 = -1 + 2.0 * (req.y + 1) * size;
                    var n = FaceN[req.face]; var a = FaceA[req.face]; var b = FaceB[req.face];

                    while (job.Next < total)
                    {
                        int i = job.Next % Grid, j = job.Next / Grid;
                        double u = u0 + (u1 - u0) * i / (Grid - 1);
                        double v = v0 + (v1 - v0) * j / (Grid - 1);
                        var dir = new Vector3D(n[0] + a[0] * u + b[0] * v, n[1] + a[1] * u + b[1] * v, n[2] + a[2] * u + b[2] * v);
                        dir.Normalize();

                        var probe = job.Center + dir * job.ProbeRadius;
                        var surface = job.Planet.GetClosestSurfacePointGlobal(ref probe);
                        double dist = (surface - job.Center).Length();
                        job.Distances[job.Next] = (dist > 0 && !double.IsNaN(dist)) ? (float)dist : -1f;
                        job.Next++;

                        if ((job.Next & 255) == 0 && sw.Elapsed.TotalMilliseconds >= budgetMs)
                            break;
                    }
                }
                catch (Exception e)
                {
                    Log.Instance?.Warn($"TerrainTiles: sampling {job.Request.planet} failed: {e.Message}");
                    Release(job.Request);
                    current = null;
                    continue;
                }

                if (job.Next >= total)
                {
                    current = null;
                    Finish(job);
                }
            }
        }

        private static void Release(TerrainRequestDto r)
        {
            lock (queuedKeys)
                queuedKeys.Remove(Key(r));
        }

        private static Job StartJob(TerrainRequestDto request)
        {
            var planet = FindPlanet(request.planet);
            if (planet == null || planet.Closed || planet.MarkedForClose || planet.AverageRadius <= 0)
                return null;

            return new Job
            {
                Request = request,
                Planet = planet,
                Center = planet.PositionComp.GetPosition(),
                ProbeRadius = planet.AverageRadius,
            };
        }

        private static MyPlanet FindPlanet(string name)
        {
            if (planetCache.TryGetValue(name, out var cached) && cached != null && !cached.Closed && !cached.MarkedForClose)
                return cached;

            // Not cached (or stale): rescan the world's planets, at most about once a second.
            var now = DateTime.UtcNow;
            if ((now - lastPlanetRefreshUtc).TotalSeconds < 1 && !planetCache.ContainsKey(name))
                return null;
            lastPlanetRefreshUtc = now;

            planetCache.Clear();
            var voxels = new List<IMyVoxelBase>();
            MyAPIGateway.Session.VoxelMaps.GetInstances(voxels, v => v is MyPlanet);
            foreach (var v in voxels)
            {
                var p = v as MyPlanet;
                if (p != null)
                    planetCache[p.StorageName] = p;
            }

            return planetCache.TryGetValue(name, out var found) ? found : null;
        }

        private static void Finish(Job job)
        {
            var d = job.Distances;
            float min = float.MaxValue, max = 0;
            int valid = 0;
            double sum = 0;
            for (int i = 0; i < d.Length; i++)
            {
                if (d[i] < 0) continue;
                valid++;
                sum += d[i];
                if (d[i] < min) min = d[i];
                if (d[i] > max) max = d[i];
            }

            if (valid == 0)
            {
                Release(job.Request);
                return;
            }

            float mean = (float)(sum / valid);
            double span = Math.Max(max - min, 0.001);
            var bytes = new byte[d.Length * 2];
            for (int i = 0; i < d.Length; i++)
            {
                float v = d[i] < 0 ? mean : d[i];
                int q = (int)Math.Round((v - min) / span * 65535.0);
                if (q < 0) q = 0;
                if (q > 65535) q = 65535;
                bytes[i * 2] = (byte)(q & 0xFF);
                bytes[i * 2 + 1] = (byte)((q >> 8) & 0xFF);
            }

            var r = job.Request;
            completed.Enqueue(new TerrainTileDto
            {
                planet = r.planet,
                face = r.face,
                level = r.level,
                x = r.x,
                y = r.y,
                vmin = min,
                vmax = max,
                data_b64 = Convert.ToBase64String(bytes),
            });
            Release(r);
        }
    }
}
