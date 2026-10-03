using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRage.Voxels;
using VRageMath;

namespace GpsSyncPlugin
{
    /// <summary>
    /// Builds an asteroid's real surface as a triangle mesh straight from its
    /// voxel storage (no raycasting, so nothing but the asteroid's own voxel
    /// data can ever end up in the shape). The storage's content is read once
    /// at a coarse LOD into a grid, and an isosurface is extracted from it
    /// with naive surface nets. Unlike the old one-distance-per-ray "skin"
    /// around a single center point, a real mesh represents any shape:
    /// concavities, overhangs, tunnels and hollow interiors all come through.
    /// Rotated voxel maps are handled by mapping grid positions to world
    /// space through the entity's world matrix, the same way the game does
    /// (see MyVoxelCoordSystems).
    /// </summary>
    public static class AsteroidShape
    {
        // Voxel content at/above this reads as solid (game's iso level is 127).
        private const float SolidThreshold = 127.5f;

        // Cells per axis of the coarse content read. The LOD is the finest
        // one that fits under this, so a big asteroid gets coarser cells
        // instead of an ever-larger grid/mesh.
        private const int MaxGridCellsPerAxis = 128;

        // A mesh over this many vertices is redone one LOD coarser — keeps
        // the payload (and the web page embedding every asteroid) small, and
        // the vertex count within 16-bit indices.
        private const int MaxVertices = 60000;

        private const int RetryCooldownSeconds = 30;

        // Which asteroids already had their shape built+sent this pass.
        // ResetSyncedTracking() requests a fresh pass (see resyncRequested),
        // applied at the start of the next ReadNextShapes call on the game
        // thread — these collections are only ever touched from there.
        private static readonly HashSet<string> synced = new HashSet<string>();
        private static readonly Dictionary<string, DateTime> retryAfter = new Dictionary<string, DateTime>();
        private static volatile bool resyncRequested;

        // A big asteroid's build (a couple million cells) can cost a few
        // hundred ms on the game thread - after any build, wait ~10x as long
        // before the next so meshing never eats more than about a tenth of
        // the time.
        private static DateTime nextBuildAllowedUtc = DateTime.MinValue;

        public static void ResetSyncedTracking()
        {
            resyncRequested = true;
        }

        /// <summary>
        /// Builds and returns the shape of the nearest asteroid (to the
        /// player) that hasn't been sent this pass — one asteroid per call,
        /// so the game-thread cost (a storage read plus surface extraction,
        /// typically a few to a few tens of ms) is spread across ticks.
        /// Empty when everything is already synced. Must run on the game
        /// thread.
        /// </summary>
        public static List<AsteroidShapeDto> ReadNextShapes()
        {
            var result = new List<AsteroidShapeDto>();
            if (!GameStateReader.IsSessionReady)
                return result;

            if (resyncRequested)
            {
                resyncRequested = false;
                synced.Clear();
            }

            if (DateTime.UtcNow < nextBuildAllowedUtc)
                return result;

            var voxel = NextUnsynced();
            if (voxel == null)
                return result;

            var timer = Stopwatch.StartNew();
            AsteroidShapeDto dto = null;
            try
            {
                dto = BuildMeshShape(voxel);
            }
            catch (Exception e)
            {
                Log.Instance?.Warn($"AsteroidShape: {voxel.StorageName} mesh build failed: {e.Message}");
            }
            timer.Stop();
            nextBuildAllowedUtc = DateTime.UtcNow.AddMilliseconds(timer.ElapsedMilliseconds * 10);

            if (dto == null)
            {
                // Empty/unreadable storage — try again after the cooldown.
                retryAfter[voxel.StorageName] = DateTime.UtcNow.AddSeconds(RetryCooldownSeconds);
                return result;
            }

            Log.Instance?.Info(
                $"AsteroidShape: {voxel.StorageName} mesh {dto.vertex_count} verts / {dto.index_count / 3} tris " +
                $"in {timer.ElapsedMilliseconds}ms");
            synced.Add(voxel.StorageName);
            result.Add(dto);
            return result;
        }

        private static IMyVoxelBase NextUnsynced()
        {
            var voxels = new List<IMyVoxelBase>();
            MyAPIGateway.Session.VoxelMaps.GetInstances(voxels, v => v is MyVoxelMap);

            // Nearest to the player first, so the asteroid you're looking at
            // is meshed before the ones on the far side of the map.
            var player = MyAPIGateway.Session.Player?.Character;
            if (player != null)
            {
                var origin = player.GetPosition();
                voxels.Sort((a, b) => Vector3D.DistanceSquared(a.WorldAABB.Center, origin)
                    .CompareTo(Vector3D.DistanceSquared(b.WorldAABB.Center, origin)));
            }

            var now = DateTime.UtcNow;
            foreach (var voxel in voxels)
            {
                if (voxel == null || voxel.Closed || voxel.MarkedForClose || voxel.Storage == null)
                    continue;
                if (synced.Contains(voxel.StorageName))
                    continue;
                if (retryAfter.TryGetValue(voxel.StorageName, out var retryAt) && retryAt > now)
                    continue;
                return voxel;
            }

            return null;
        }

        private static AsteroidShapeDto BuildMeshShape(IMyVoxelBase voxel)
        {
            var storage = voxel.Storage;
            var size = storage.Size;
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0)
                return null;

            int maxDim = Math.Max(size.X, Math.Max(size.Y, size.Z));
            int lod = 0;
            while (((maxDim + (1 << lod) - 1) >> lod) > MaxGridCellsPerAxis)
                lod++;

            // The game maps voxel coordinate v to world as
            // Translation + rotate(v - Half); a coarse cell's sample point
            // sits (2^lod - 1) / 2 voxels into the cell.
            var world = voxel.WorldMatrix;
            var half = new Vector3D(size.X, size.Y, size.Z) * 0.5;

            for (int attempt = 0; attempt < 3; attempt++, lod++)
            {
                double cellSize = 1 << lod;
                double cellOffset = (cellSize - 1) * 0.5;

                var cells = new Vector3I(
                    Math.Max(1, (size.X + (1 << lod) - 1) >> lod),
                    Math.Max(1, (size.Y + (1 << lod) - 1) >> lod),
                    Math.Max(1, (size.Z + (1 << lod) - 1) >> lod));

                var data = new MyStorageData(MyStorageDataTypeFlags.Content);
                data.Resize(cells);
                storage.ReadRange(data, MyStorageDataTypeFlags.Content, lod, Vector3I.Zero, cells - Vector3I.One);

                var gridVerts = new List<Vector3D>();
                var tris = new List<int>();
                if (!ExtractSurface(data, cells, gridVerts, tris, MaxVertices))
                    continue; // too many vertices — redo one LOD coarser

                if (gridVerts.Count == 0 || tris.Count == 0)
                {
                    Log.Instance?.Info($"AsteroidShape: {voxel.StorageName} no surface (size={size} lod={lod}) — empty?");
                    return null;
                }

                // Grid positions -> world, then re-express relative to the
                // mesh's own bounding-box center so they quantize well.
                var worldVerts = new Vector3D[gridVerts.Count];
                var min = new Vector3D(double.MaxValue);
                var max = new Vector3D(double.MinValue);
                for (int i = 0; i < worldVerts.Length; i++)
                {
                    var g = gridVerts[i];
                    var local = new Vector3D(g.X * cellSize + cellOffset, g.Y * cellSize + cellOffset, g.Z * cellSize + cellOffset);
                    var w = world.Translation + Vector3D.TransformNormal(local - half, world);
                    worldVerts[i] = w;
                    min = Vector3D.Min(min, w);
                    max = Vector3D.Max(max, w);
                }

                var center = (min + max) * 0.5;
                double extent = 0;
                for (int i = 0; i < worldVerts.Length; i++)
                {
                    var d = worldVerts[i] - center;
                    extent = Math.Max(extent, Math.Max(Math.Abs(d.X), Math.Max(Math.Abs(d.Y), Math.Abs(d.Z))));
                }
                if (extent <= 0)
                    return null;

                double scale = extent / 32767.0;
                var vbytes = new byte[worldVerts.Length * 6];
                for (int i = 0; i < worldVerts.Length; i++)
                {
                    var d = worldVerts[i] - center;
                    WriteInt16(vbytes, i * 6, Quantize(d.X, scale));
                    WriteInt16(vbytes, i * 6 + 2, Quantize(d.Y, scale));
                    WriteInt16(vbytes, i * 6 + 4, Quantize(d.Z, scale));
                }

                var ibytes = new byte[tris.Count * 2];
                for (int i = 0; i < tris.Count; i++)
                {
                    ibytes[i * 2] = (byte)(tris[i] & 0xFF);
                    ibytes[i * 2 + 1] = (byte)((tris[i] >> 8) & 0xFF);
                }

                return new AsteroidShapeDto
                {
                    voxel_name = voxel.StorageName,
                    format = "mesh",
                    center_x = center.X,
                    center_y = center.Y,
                    center_z = center.Z,
                    mesh_scale = (float)scale,
                    vertex_count = worldVerts.Length,
                    index_count = tris.Count,
                    vertices_b64 = Convert.ToBase64String(vbytes),
                    indices_b64 = Convert.ToBase64String(ibytes),
                };
            }

            Log.Instance?.Warn($"AsteroidShape: {voxel.StorageName} still over {MaxVertices} vertices after coarsening, skipping");
            return null;
        }

        private static short Quantize(double v, double scale)
        {
            double q = Math.Round(v / scale);
            if (q > 32767) q = 32767;
            if (q < -32767) q = -32767;
            return (short)q;
        }

        private static void WriteInt16(byte[] buf, int offset, short v)
        {
            buf[offset] = (byte)(v & 0xFF);
            buf[offset + 1] = (byte)((v >> 8) & 0xFF);
        }

        // Corner offsets of a cell and the 12 cube edges as corner-index
        // pairs, for surface nets.
        private static readonly int[,] CornerOffset =
        {
            { 0, 0, 0 }, { 1, 0, 0 }, { 0, 1, 0 }, { 1, 1, 0 },
            { 0, 0, 1 }, { 1, 0, 1 }, { 0, 1, 1 }, { 1, 1, 1 },
        };

        private static readonly int[,] CubeEdges =
        {
            { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 },
            { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 },
            { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 },
        };

        /// <summary>
        /// Naive surface nets over the content grid. The grid is padded with
        /// one empty cell on every side so the surface always closes. Output
        /// vertex positions are in cell-index space (fractional; cell (0,0,0)
        /// is 0,0,0). Returns false if the vertex count exceeded
        /// <paramref name="maxVertices"/>.
        /// </summary>
        private static bool ExtractSurface(MyStorageData data, Vector3I cells, List<Vector3D> verts, List<int> tris, int maxVertices)
        {
            int px = cells.X + 2, py = cells.Y + 2, pz = cells.Z + 2; // padded corner counts
            int cx = px - 1, cy = py - 1, cz = pz - 1;                // cell counts

            // Signed field: positive inside the rock. Padding is empty.
            var field = new float[px * py * pz];
            for (int k = 0; k < pz; k++)
                for (int j = 0; j < py; j++)
                    for (int i = 0; i < px; i++)
                    {
                        int x = i - 1, y = j - 1, z = k - 1;
                        bool inside = x >= 0 && y >= 0 && z >= 0 && x < cells.X && y < cells.Y && z < cells.Z;
                        field[(k * py + j) * px + i] = inside ? data.Content(x, y, z) - SolidThreshold : -SolidThreshold;
                    }

            var cellVertex = new int[cx * cy * cz];
            for (int i = 0; i < cellVertex.Length; i++)
                cellVertex[i] = -1;

            var cv = new float[8];
            for (int k = 0; k < cz; k++)
                for (int j = 0; j < cy; j++)
                    for (int i = 0; i < cx; i++)
                    {
                        int mask = 0;
                        for (int c = 0; c < 8; c++)
                        {
                            cv[c] = field[((k + CornerOffset[c, 2]) * py + (j + CornerOffset[c, 1])) * px + (i + CornerOffset[c, 0])];
                            if (cv[c] > 0) mask |= 1 << c;
                        }
                        if (mask == 0 || mask == 255)
                            continue;

                        double sx = 0, sy = 0, sz = 0;
                        int n = 0;
                        for (int e = 0; e < 12; e++)
                        {
                            int a = CubeEdges[e, 0], b = CubeEdges[e, 1];
                            if ((cv[a] > 0) == (cv[b] > 0))
                                continue;
                            double t = cv[a] / (cv[a] - cv[b]);
                            sx += CornerOffset[a, 0] + t * (CornerOffset[b, 0] - CornerOffset[a, 0]);
                            sy += CornerOffset[a, 1] + t * (CornerOffset[b, 1] - CornerOffset[a, 1]);
                            sz += CornerOffset[a, 2] + t * (CornerOffset[b, 2] - CornerOffset[a, 2]);
                            n++;
                        }

                        if (verts.Count >= maxVertices)
                            return false;
                        cellVertex[(k * cy + j) * cx + i] = verts.Count;
                        // Corner index c is sample index c - 1 (padding), so
                        // cell (i,j,k)'s local point lands at i + s - 1.
                        verts.Add(new Vector3D(i + sx / n - 1, j + sy / n - 1, k + sz / n - 1));
                    }

            // One quad per grid edge that crosses the surface, joining the
            // four cells that share the edge. Cyclic axis order keeps the
            // winding consistent across the three edge directions.
            var pos = new int[3];
            var counts = new[] { px, py, pz };
            for (int k = 0; k < pz; k++)
                for (int j = 0; j < py; j++)
                    for (int i = 0; i < px; i++)
                    {
                        pos[0] = i; pos[1] = j; pos[2] = k;
                        float v0 = field[(k * py + j) * px + i];
                        for (int axis = 0; axis < 3; axis++)
                        {
                            if (pos[axis] + 1 >= counts[axis])
                                continue;

                            int ni = i + (axis == 0 ? 1 : 0), nj = j + (axis == 1 ? 1 : 0), nk = k + (axis == 2 ? 1 : 0);
                            float v1 = field[(nk * py + nj) * px + ni];
                            if ((v0 > 0) == (v1 > 0))
                                continue;

                            int b = (axis + 1) % 3, c = (axis + 2) % 3;
                            int q0 = CellAt(cellVertex, cx, cy, cz, pos, axis, b, c, -1, -1);
                            int q1 = CellAt(cellVertex, cx, cy, cz, pos, axis, b, c, 0, -1);
                            int q2 = CellAt(cellVertex, cx, cy, cz, pos, axis, b, c, 0, 0);
                            int q3 = CellAt(cellVertex, cx, cy, cz, pos, axis, b, c, -1, 0);
                            if (q0 < 0 || q1 < 0 || q2 < 0 || q3 < 0)
                                continue;

                            if (v0 > 0)
                            {
                                tris.Add(q0); tris.Add(q1); tris.Add(q2);
                                tris.Add(q0); tris.Add(q2); tris.Add(q3);
                            }
                            else
                            {
                                tris.Add(q0); tris.Add(q2); tris.Add(q1);
                                tris.Add(q0); tris.Add(q3); tris.Add(q2);
                            }
                        }
                    }

            return true;
        }

        // Vertex index of the cell at `pos` with the two non-edge axes
        // (b, c) offset by (db, dc), or -1 if outside the cell grid / empty.
        private static int CellAt(int[] cellVertex, int cx, int cy, int cz, int[] pos, int axis, int b, int c, int db, int dc)
        {
            var cell = new int[3];
            cell[axis] = pos[axis];
            cell[b] = pos[b] + db;
            cell[c] = pos[c] + dc;
            if (cell[0] < 0 || cell[1] < 0 || cell[2] < 0 || cell[0] >= cx || cell[1] >= cy || cell[2] >= cz)
                return -1;
            return cellVertex[(cell[2] * cy + cell[1]) * cx + cell[0]];
        }
    }
}
