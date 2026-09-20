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
