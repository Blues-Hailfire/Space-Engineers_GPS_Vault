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
}
