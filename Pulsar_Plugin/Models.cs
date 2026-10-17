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
        // Push only: vault names this player had synced and has since deleted
        // in-game — the only points the server trashes (see
        // GameStateReader.ReadDeletedVaultNames). Absent on a pull response.
        public List<string> deleted;
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

        // Set only for "neutral"/"enemy" (see GameStateReader.ReadHostileSignals):
        // the max meters x/y/z may have been randomly offset from this
        // signal's true position, so a map can draw an uncertainty circle
        // instead of implying an exact fix. Null for every other category,
        // whose position is reported exactly.
        public double? radius;

        // The reporting grid's IMyCubeGrid.IsStatic — lets the map draw a
        // station icon vs. a ship icon for this signal.
        public bool is_static;
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
