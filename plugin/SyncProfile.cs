using System.Collections.Generic;

namespace GpsSyncPlugin
{
    /// <summary>
    /// One saved endpoint/token/channel combination a player can sync with
    /// ("host profile"). Players can save several (e.g. one per Discord
    /// server) and switch between them from the settings dialog's Host
    /// Profile dropdown, or in-game via /vault host "name" (/vh).
    /// </summary>
    public class SyncProfile
    {
        public string Id = "";
        public string Nickname = "";
        public string Endpoint = "https://localhost:4040";
        public string Token = "";
        public string ChannelId = "";

        // Cached from the bot via SyncClient.FetchWhoAmIAsync so the dropdown
        // can show real names instead of raw Discord snowflake IDs. Populated
        // on demand ("Look Up Server/Channel Names" button) rather than kept
        // live, so the dropdown still reads sensibly offline.
        public string GuildName = "";
        public string ChannelName = "";

        public string DisplayLabel
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Nickname))
                    parts.Add(Nickname);

                if (!string.IsNullOrWhiteSpace(GuildName) && !string.IsNullOrWhiteSpace(ChannelName))
                    parts.Add($"{GuildName} / #{ChannelName}");
                else if (!string.IsNullOrWhiteSpace(GuildName))
                    parts.Add(GuildName);

                if (parts.Count == 0)
                    parts.Add(string.IsNullOrWhiteSpace(ChannelId) ? "(new host)" : ChannelId);

                return string.Join(" — ", parts);
            }
        }
    }

    /// <summary>
    /// Whether syncing is turned on for one (world/server, sync profile) pair.
    /// New worlds have no entry, so they default to off (see Config.SyncEnabled)
    /// until the player explicitly enables sync there — at which point it
    /// stays on for that same world+profile combination across restarts.
    /// </summary>
    public class WorldSyncBinding
    {
        public string WorldKey = "";
        public string ProfileId = "";
        public bool SyncEnabled;

        // Human-readable label for WorldKey (a raw Steam ID + save path/session
        // name isn't fit to show a player) — the world/save name, or for a
        // dedicated server, its HostName when available (see
        // GameStateReader.CurrentWorldDisplayName). Refreshed every time this
        // binding is touched (see Config.GetOrCreateWorldBinding), so it stays
        // reasonably current if a server's advertised name changes.
        public string DisplayName = "";
    }
}
