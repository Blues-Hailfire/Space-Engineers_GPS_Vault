using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GpsSyncPlugin.Settings;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace GpsSyncPlugin
{
    /// <summary>
    /// In-game chat commands for browsing the vault GPS list without
    /// cluttering the HUD (vault-synced points are added hidden by default —
    /// see GameStateReader.ApplyRemoteGpsList):
    ///
    ///   /vault search [text]   (aliases: /vs, /VS)
    ///     Lists the player's current GPS points — optionally filtered by a
    ///     name substring — sorted by distance from the player, e.g.:
    ///       1: Ice AST (1.20 km)
    ///       2: Iron AST (3.40 km)
    ///
    ///   /vault confirm &lt;index&gt; [-h | -hide all]   (aliases: /vc, /VC)
    ///     Reveals the given result (by its number from the last search) on
    ///     the HUD, and hides every other point that showed up in that same
    ///     search. Add -h (or -hide all) to also hide every other GPS point
    ///     in your whole list, not just the ones from that search.
    ///
    ///   /vault sync [on|off]   (no aliases)
    ///     Toggles GPS/position syncing on or off — same switch as the
    ///     plugin's "Sync Enabled" setting.
    ///
    ///   /vault players   (aliases: /vp, /VP)
    ///     Toggle: turns on a continuous live feed of every other player
    ///     currently sharing their location via the vault, shown as a GPS
    ///     point named "[VP]PlayerName" (color #71FF00) that keeps updating on
    ///     its own poll cycle. One-way (server -&gt; client) and purely local:
    ///     never pushed back up to the vault. Running the command again while
    ///     active turns it off, removing all "[VP]" markers and stopping the
    ///     poll.
    /// </summary>
    public static class ChatCommands
    {
        private const int VpPollIntervalMs = 5000;

        private static List<IMyGps> lastSearchResults = new List<IMyGps>();
        private static bool subscribed;
        private static readonly SyncClient syncClient = new SyncClient();
        private static Timer vpTimer;
        private static volatile bool vpActive;
        private static volatile bool vpSyncing;

        /// <summary>
        /// Attempts to subscribe to chat input, once. MyAPIGateway.Utilities is
        /// still null when Plugin.Init() runs (too early in game bootstrap),
        /// so this is called from Plugin.Update() on every tick until it
        /// succeeds rather than once from Init() directly.
        /// </summary>
        public static void TryInit()
        {
            if (subscribed || MyAPIGateway.Utilities == null)
                return;
            MyAPIGateway.Utilities.MessageEnteredSender += OnMessageEntered;
            subscribed = true;
        }

        public static void Dispose()
        {
            vpTimer?.Dispose();
            vpTimer = null;
            vpActive = false;

            if (!subscribed)
                return;
            MyAPIGateway.Utilities.MessageEnteredSender -= OnMessageEntered;
            subscribed = false;
        }

        private static void OnMessageEntered(ulong sender, string messageText, ref bool sendToOthers)
        {
            try
            {
                var text = messageText?.Trim();
                if (string.IsNullOrEmpty(text) || text[0] != '/')
                    return;

                SplitFirstWord(text, out var command, out var rest);
                command = command.ToLowerInvariant();

                if (command == "/vault")
                {
                    SplitFirstWord(rest, out var subCommand, out var arg);
                    subCommand = subCommand.ToLowerInvariant();
                    if (subCommand == "search")
                    {
                        HandleSearch(arg);
                        sendToOthers = false;
                    }
                    else if (subCommand == "confirm")
                    {
                        HandleConfirm(arg);
                        sendToOthers = false;
                    }
                    else if (subCommand == "sync")
                    {
                        HandleSyncToggle(arg);
                        sendToOthers = false;
                    }
                    else if (subCommand == "players")
                    {
                        HandlePlayers();
                        sendToOthers = false;
                    }
                }
                else if (command == "/vs")
                {
                    HandleSearch(rest);
                    sendToOthers = false;
                }
                else if (command == "/vc")
                {
                    HandleConfirm(rest);
                    sendToOthers = false;
                }
                else if (command == "/vp")
                {
                    HandlePlayers();
                    sendToOthers = false;
                }
            }
            catch (Exception e)
            {
                Log.Instance?.Error($"Chat command failed: {e.Message}");
            }
        }

        private static void SplitFirstWord(string text, out string first, out string rest)
        {
            var spaceIdx = text.IndexOf(' ');
            if (spaceIdx < 0)
            {
                first = text;
                rest = "";
            }
            else
            {
                first = text.Substring(0, spaceIdx);
                rest = text.Substring(spaceIdx + 1).Trim();
            }
        }

        private static void HandleSearch(string searchText)
        {
            if (!GameStateReader.IsSessionReady)
                return;

            var identityId = MyAPIGateway.Session.Player.IdentityId;
            var playerPos = MyAPIGateway.Session.Player.Character.GetPosition();

            var all = new List<IMyGps>();
            MyAPIGateway.Session.GPS.GetGpsList(identityId, all);

            var matches = all
                .Where(g => string.IsNullOrEmpty(searchText)
                            || (g.Name?.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                .Select(g => new { Gps = g, Dist = Vector3D.Distance(g.Coords, playerPos) })
                .OrderBy(x => x.Dist)
                .ToList();

            lastSearchResults = matches.Select(x => x.Gps).ToList();

            if (matches.Count == 0)
            {
                MyAPIGateway.Utilities.ShowMessage("Vault", "No matching GPS points.");
                return;
            }

            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                MyAPIGateway.Utilities.ShowMessage("Vault", $"{i + 1}: {m.Gps.Name} ({m.Dist / 1000.0:0.00} km)");
            }
        }

        private static void HandleConfirm(string argText)
        {
            if (!GameStateReader.IsSessionReady)
                return;

            var trimmed = (argText ?? "").Trim();
            var lower = trimmed.ToLowerInvariant();
            bool hideAll = false;
            if (lower.EndsWith("-hide all"))
            {
                hideAll = true;
                trimmed = trimmed.Substring(0, trimmed.Length - "-hide all".Length).Trim();
            }
            else if (lower.EndsWith("-h"))
            {
                hideAll = true;
                trimmed = trimmed.Substring(0, trimmed.Length - "-h".Length).Trim();
            }

            if (!int.TryParse(trimmed, out var index) || index < 1 || index > lastSearchResults.Count)
            {
                MyAPIGateway.Utilities.ShowMessage("Vault", "Run /vs [search] first, then /vc <number> [-h] from that list.");
                return;
            }

            var identityId = MyAPIGateway.Session.Player.IdentityId;
            var chosen = lastSearchResults[index - 1];

            // Always hide the other points that showed up in this same search.
            foreach (var gps in lastSearchResults)
            {
                if (gps.Hash == chosen.Hash)
                    continue;
                MyAPIGateway.Session.GPS.SetShowOnHud(identityId, gps, false);
            }

            if (hideAll)
            {
                var all = new List<IMyGps>();
                MyAPIGateway.Session.GPS.GetGpsList(identityId, all);
                foreach (var gps in all)
                {
                    if (gps.Hash == chosen.Hash)
                        continue;
                    MyAPIGateway.Session.GPS.SetShowOnHud(identityId, gps, false);
                }
            }

            MyAPIGateway.Session.GPS.SetShowOnHud(identityId, chosen, true);
            MyAPIGateway.Utilities.ShowMessage("Vault", hideAll
                ? $"Showing {chosen.Name} on HUD, hid every other GPS point."
                : $"Showing {chosen.Name} on HUD, hid the other {lastSearchResults.Count - 1} search result(s).");
        }

        private static void HandleSyncToggle(string arg)
        {
            var a = (arg ?? "").Trim().ToLowerInvariant();
            bool newState;
            if (a == "on")
                newState = true;
            else if (a == "off")
                newState = false;
            else
                newState = !Config.Current.SyncEnabled;

            Config.Current.SyncEnabled = newState;
            ConfigStorage.Save(Config.Current);

            MyAPIGateway.Utilities.ShowMessage("Vault", newState
                ? "Syncing enabled."
                : "Syncing disabled — your GPS list and position will no longer push to or pull from the vault.");
        }

        /// <summary>
        /// Toggles the continuous "/vp" live-player feed on or off. While on,
        /// a background timer keeps polling the vault and refreshing the
        /// "[VP]" markers on their own, independent of the regular GPS pull —
        /// turning it off stops the timer and removes the markers.
        /// </summary>
        private static void HandlePlayers()
        {
            if (!GameStateReader.IsSessionReady)
                return;

            if (vpActive)
            {
                vpActive = false;
                vpTimer?.Dispose();
                vpTimer = null;

                MyAPIGateway.Utilities.ShowMessage("Vault", "Stopped tracking other players.");
                MyAPIGateway.Utilities?.InvokeOnGameThread(() =>
                {
                    try
                    {
                        GameStateReader.ApplyVaultPlayerMarkers(new List<VaultPlayerDto>());
                    }
                    catch (Exception e)
                    {
                        Log.Instance?.Error($"Clearing vault player markers failed: {e.Message}");
                    }
                });
                return;
            }

            vpActive = true;
            MyAPIGateway.Utilities.ShowMessage("Vault", "Tracking other active players (live)...");
            FetchAndApplyPlayers();
            vpTimer = new Timer(OnVpTick, null, VpPollIntervalMs, VpPollIntervalMs);
        }

        private static void OnVpTick(object state)
        {
            if (!vpActive)
                return;
            FetchAndApplyPlayers();
        }

        private static void FetchAndApplyPlayers()
        {
            if (vpSyncing || !GameStateReader.IsSessionReady)
                return;

            vpSyncing = true;
            var selfName = MyAPIGateway.Session.Player.DisplayName;

            Task.Run(async () =>
            {
                try
                {
                    var players = await syncClient.FetchActivePlayersAsync(selfName);
                    if (players == null)
                        return;

                    // AddLocalGps/RemoveLocalGps are still local-entity mutations,
                    // so run them on the game thread like the other GPS-list edits.
                    MyAPIGateway.Utilities?.InvokeOnGameThread(() =>
                    {
                        try
                        {
                            GameStateReader.ApplyVaultPlayerMarkers(players);
                        }
                        catch (Exception e)
                        {
                            Log.Instance?.Error($"Applying vault player markers failed: {e.Message}");
                        }
                    });
                }
                catch (Exception e)
                {
                    Log.Instance?.Error($"/vault players poll failed: {e.Message}");
                }
                finally
                {
                    vpSyncing = false;
                }
            });
        }
    }
}
