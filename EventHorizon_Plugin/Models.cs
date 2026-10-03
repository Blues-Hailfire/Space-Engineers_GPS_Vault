using System.Collections.Generic;

namespace GpsSyncPlugin
{
    public class GpsPointDto
    {
        public string name;
        public double x;
        public double y;
        public double z;
        public string color;
    }

    public class GpsSyncRequest
    {
        public List<GpsPointDto> points;
    }

    public class PositionSyncRequest
    {
        public string player_name;
        public double x;
        public double y;
        public double z;
        public string color;
        public double vx;
        public double vy;
        public double vz;
        public double ax;
        public double ay;
        public double az;

        // Total gravity (natural + artificial, m/s^2) at this position, and the
        // controlled grid's angular velocity (rad/s, world frame) - lets the
        // server's path prediction fall under gravity and follow a turning
        // ship instead of extrapolating a straight line.
        public double gx;
        public double gy;
        public double gz;
        public double wx;
        public double wy;
        public double wz;

        // Weapon lock (MyTargetLockingComponent — a lock-capable weapon like
        // the rocket launcher, not general "is anyone locked onto me").
        // lock_target_* / lock_distance are only meaningful when
        // weapon_locked is true.
        public bool weapon_locked;
        public string lock_target_name;
        public double lock_target_x;
        public double lock_target_y;
        public double lock_target_z;
        public double lock_distance;

        // WeaponCore combat picture (see WeaponCoreBridge.ReadCombatState) —
        // null when WeaponCore isn't loaded.
        public CombatStateDto combat;
    }

    public class CombatStateDto
    {
        // Your ship's construct DPS — null on foot. ship_dps is WeaponCore's
        // "effective" figure (current ammo), ship_peak_dps its optimal one.
        public double? ship_dps;
        public double? ship_peak_dps;
        public int incoming_missiles;
        public List<double[]> incoming_positions = new List<double[]>();
        // WeaponCore's "a priority target / any target is within weapon range".
        public bool in_range_priority;
        public bool in_range_other;
        // Every guided missile this client is simulating (nearby only).
        public List<MissileDto> missiles = new List<MissileDto>();
    }

    public class MissileDto
    {
        public string id;
        public double x;
        public double y;
        public double z;
        public string faction_tag;
    }

    public class PlanetDto
    {
        public string name;
        public double x;
        public double y;
        public double z;
        public double radius;
    }

    public class PlanetSyncRequest
    {
        public List<PlanetDto> planets;
    }

    // Same shape as PlanetDto — radius is a bounding-sphere approximation
    // (see GameStateReader.ReadAsteroids) rather than a precise physical
    // radius, since asteroids are irregular voxel bodies, not spheres.
    public class AsteroidDto
    {
        public string name;
        public double x;
        public double y;
        public double z;
        public double radius;
    }

    public class AsteroidSyncRequest
    {
        public List<AsteroidDto> asteroids;
    }

    public class AsteroidShapeDto
    {
        public string voxel_name;

        // Always "mesh" (see AsteroidShape).
        public string format;

        // Vertices are int16 x/y/z triples (little-endian, base64) in world
        // axes relative to center_x/y/z, multiplied by mesh_scale to get
        // meters; indices are uint16 triangle-list indices (base64).
        public double center_x;
        public double center_y;
        public double center_z;
        public float mesh_scale;
        public int vertex_count;
        public int index_count;
        public string vertices_b64;
        public string indices_b64;
    }

    public class AsteroidShapeSyncRequest
    {
        public List<AsteroidShapeDto> shapes;
    }

    // One terrain tile the web map wants (see TerrainTiles): cube-sphere face
    // 0..5, LOD level, and tile x/y within that level's 2^level grid.
    public class TerrainRequestDto
    {
        public string planet;
        public int face;
        public int level;
        public int x;
        public int y;
    }

    public class TerrainRequestList
    {
        public List<TerrainRequestDto> requests;
    }

    // A sampled tile: 65 x 65 little-endian uint16 (base64) mapping
    // 0..65535 onto vmin..vmax meters of distance from the planet's center.
    public class TerrainTileDto
    {
        public string planet;
        public int face;
        public int level;
        public int x;
        public int y;
        public float vmin;
        public float vmax;
        public string data_b64;
    }

    public class TerrainTileSyncRequest
    {
        public List<TerrainTileDto> tiles;
    }

    // One exact clustered vein location, folded into its parent
    // OreDepositDto.locations — see ReadOreDeposits. Only ever populated for
    // an asteroid's single point; a planet's points are already
    // area-granular (see PlanetClusterMeters), so they don't need this.
    public class OreLocationDto
    {
        public string ore;
        public double x;
        public double y;
        public double z;
    }

    // One ore point per voxel body (asteroid) or per clustered area of a
    // planet — see OreDeposits.ReadOreDeposits. deposit_id is the upsert
    // key: an asteroid's is just its voxel name (never more than one row
    // per asteroid); a planet's also encodes the area, so it can have
    // several.
    public class OreDepositDto
    {
        public string deposit_id;
        // Precomputed display name — "AST - Iron,Ice" for an asteroid,
        // "Earth - Iron,Ice" for a planet's clustered area (see
        // ReadOreDeposits).
        public string name;
        // Comma-joined distinct ore names found here.
        public string ores;
        public string voxel_name;
        public bool is_planet;
        public double x;
        public double y;
        public double z;
        // The individual vein positions averaged together into x/y/z above —
        // only populated for an asteroid's point (see OreLocationDto).
        public List<OreLocationDto> locations;
    }

    public class OreDepositSyncRequest
    {
        public List<OreDepositDto> deposits;
    }

    public class VaultPlayerDto
    {
        public string name;
        public double x;
        public double y;
        public double z;
    }

    public class VaultPlayersResponse
    {
        public List<VaultPlayerDto> players;
    }

    public class HostileSignalDto
    {
        public long signal_id;
        public string name;
        public string color;
        public string category;
        public double x;
        public double y;
        public double z;
        public double vx;
        public double vy;
        public double vz;
        public double ax;
        public double ay;
        public double az;

        // Total gravity (natural + artificial, m/s^2) at this position, and the
        // controlled grid's angular velocity (rad/s, world frame) - lets the
        // server's path prediction fall under gravity and follow a turning
        // ship instead of extrapolating a straight line.
        public double gx;
        public double gy;
        public double gz;
        public double wx;
        public double wy;
        public double wz;

        // The reporting grid's IMyCubeGrid.IsStatic — lets the map draw a
        // station icon vs. a ship icon for this signal.
        public bool is_static;

        // WeaponCore threat rating from your ship's threat list, when it's on it.
        public double? threat;
        // WeaponCore optimal DPS of this grid, when it's loaded on this client.
        public double? dps;
    }

    public class HostileSignalSyncRequest
    {
        public List<HostileSignalDto> signals;
    }

    public class WhoAmIResponse
    {
        public string guild_name;
        public string channel_name;
        // Set when the server responded with an error body (e.g. 401 "invalid
        // or missing token") instead of a successful lookup — see
        // SyncClient.FetchWhoAmIAsync, which deserializes into this class
        // either way rather than only on success, so the caller can show the
        // server's actual reason instead of a generic failure message.
        public string error;
    }
}
