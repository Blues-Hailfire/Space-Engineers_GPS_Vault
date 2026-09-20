using GpsSyncPlugin.Settings;
using GpsSyncPlugin.Settings.Elements;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace GpsSyncPlugin
{
    public class Config : INotifyPropertyChanged
    {
        #region Options

        private List<SyncProfile> profiles = new List<SyncProfile>();
        private string activeProfileId = "";
        private List<WorldSyncBinding> worldBindings = new List<WorldSyncBinding>();
        private int gpsIntervalSeconds = 30;
        private int positionIntervalSeconds = 5;
        private int hostilesIntervalSeconds = 5;

        // Legacy single-profile fields (pre-multi-profile config). Kept under
        // their original XML element names, with no UI attribute of their
        // own, purely so an old .cfg file still deserializes into these and
        // MigrateLegacyProfile() (called from ConfigStorage.Load) can fold
        // them into the player's first SyncProfile instead of losing them.
        public string Endpoint { get; set; } = "";
        public string Token { get; set; } = "";
        public string ChannelId { get; set; } = "";

        #endregion

        #region User interface

        public readonly string Title = "GPS Vault - DataLink";

        [Separator("Discord bot connection")]

        [ProfileDropdown(label: "Host Profile", description: "Which saved endpoint/token/channel to sync with. " +
                                 "Use the buttons below to add another, or look up its real server/channel names. " +
                                 "Also switchable in-game with /vault host \"name\" (or /vh).")]
        public string ActiveProfileId
        {
            get
            {
                if (profiles.Count == 0)
                    return "";
                if (!profiles.Any(p => p.Id == activeProfileId))
                    activeProfileId = profiles[0].Id;
                return activeProfileId;
            }
            set => SetField(ref activeProfileId, value);
        }

        [Textbox(description: "A friendly name for this host profile, shown in the Host Profile dropdown above " +
                               "and used to switch to it with /vault host \"name\". Purely cosmetic — does not affect " +
                               "syncing. Overwritten by the Lookup button below, if you use it.")]
        public string ActiveNickname
        {
            get => ActiveProfile.Nickname;
            set => ActiveProfile.Nickname = value;
        }

        [Textbox(description: "Base URL of the GPS Manager bot's sync API, e.g. https://your-server:4040. " +
                               "Requires the server to have cert_file/key_file configured (see sync_config.json) — " +
                               "plain http:// sends your sync token unencrypted. Changing this turns sync off for " +
                               "this world until you re-enable it below.")]
        public string ActiveEndpoint
        {
            get => ActiveProfile.Endpoint;
            set => SetActiveProfileField(p => p.Endpoint, (p, v) => p.Endpoint = v, value);
        }

        [Textbox(description: "Sync token from the bot's /create_sync_token command. Changing this turns sync off " +
                               "for this world until you re-enable it below.")]
        public string ActiveToken
        {
            get => ActiveProfile.Token;
            set => SetActiveProfileField(p => p.Token, (p, v) => p.Token = v, value);
        }

        [Textbox(description: "Optional: Discord channel ID to sync to. Leave blank to use the " +
                               "channel the token was created for — the token authorizes every " +
                               "channel the bot is bound to in that same Discord server, so this " +
                               "just picks which one. Changing this turns sync off for this world " +
                               "until you re-enable it below.")]
        public string ActiveChannelId
        {
            get => ActiveProfile.ChannelId;
            set => SetActiveProfileField(p => p.ChannelId, (p, v) => p.ChannelId = v, value);
        }

        // Static, and operating on Config.Current explicitly rather than an
        // instance: SettingsGenerator builds [Button] delegates via
        // Delegate.CreateDelegate(type, target: null, methodInfo) — a
        // non-static method bound that way runs with `this == null` and
        // NullReferenceExceptions the instant it touches an instance field.
        //
        // [RenderAfter] pins these two rows right under Channel ID — method
        // rows otherwise always render after every property row (see
        // SettingsGenerator.ExtractAttributes).

        [Button(label: "Lookup", description: "Fetches the Discord server/channel name for the current host profile from the bot, so the dropdown shows names instead of raw IDs. Also overwrites this profile's Nickname with the channel name, even if you'd set a custom one.")]
        [RenderAfter("ActiveChannelId")]
        public static void RefreshProfileNames()
        {
            var profile = Current.ActiveProfile;
            MyAPIGateway.Utilities?.ShowMessage("Vault", $"Looking up names for \"{profile.Nickname}\"...");
            Task.Run(async () =>
            {
                var info = await SyncClient.Shared.FetchWhoAmIAsync(profile.Endpoint, profile.Token, profile.ChannelId);

                MyAPIGateway.Utilities?.InvokeOnGameThread(() =>
                {
                    if (info == null)
                    {
                        // The server couldn't be reached at all — no server-provided
                        // reason to show (see FetchWhoAmIAsync); check the plugin log.
                        MyAPIGateway.Utilities?.ShowMessage("Vault",
                            $"Lookup failed for \"{profile.Nickname}\" — couldn't reach the server. Check its Endpoint, or the plugin log for details.");
                        return;
                    }

                    if (!string.IsNullOrEmpty(info.error))
                    {
                        MyAPIGateway.Utilities?.ShowMessage("Vault",
                            $"Lookup failed for \"{profile.Nickname}\": {info.error}");
                        return;
                    }

                    var oldNickname = profile.Nickname;
                    profile.GuildName = info.guild_name ?? profile.GuildName;
                    profile.ChannelName = info.channel_name ?? profile.ChannelName;
                    // Always overwrites, even a nickname the player typed
                    // themselves — a deliberate choice, not a default-only
                    // fill, so /vault host "name" always matches the actual
                    // channel name after a lookup.
                    profile.Nickname = info.channel_name ?? profile.Nickname;
                    Plugin.Instance?.RefreshSettingsUI();
                    MyAPIGateway.Utilities?.ShowMessage("Vault",
                        $"\"{oldNickname}\" -> \"{profile.Nickname}\" ({profile.GuildName ?? "?"} / #{profile.ChannelName ?? "?"})");
                });
            });
        }

        // Stacked as three separate rows (Lookup above, this, then Delete) —
        // NOT side by side. A side-by-side row was tried first (splitting
        // the panel's ~0.3 remaining width in half, ~0.145 each) and kept
        // overflowing/overlapping regardless of label length or the
        // Simple.LayoutControls SetMaxWidth fix, so it's not worth the
        // fragility versus just giving each button the full row width.
        [Button(label: "Add New Host Profile", description: "Creates a new blank host profile and switches to it.")]
        [RenderAfter("ActiveChannelId")]
        public static void AddProfile()
        {
            var config = Current;
            var profile = new SyncProfile { Id = Guid.NewGuid().ToString("N"), Nickname = $"Host {config.profiles.Count + 1}" };
            config.profiles.Add(profile);
            config.activeProfileId = profile.Id;
            config.DisableSyncForCurrentWorld();
            Plugin.Instance?.RefreshSettingsUI();
        }

        [Button(label: "Delete Current Host Profile", description: "Removes the selected host profile, and its saved on/off state for every world.")]
        [RenderAfter("ActiveChannelId")]
        public static void DeleteProfile()
        {
            var config = Current;
            var current = config.ActiveProfile;
            if (!config.profiles.Remove(current))
                return;
            config.worldBindings.RemoveAll(b => b.ProfileId == current.Id);
            config.activeProfileId = config.profiles.Count > 0 ? config.profiles[0].Id : "";
            // Switching to whatever profile lands in the dropdown next must
            // not silently turn ON syncing to a different Discord channel —
            // same "new channel = sync off" rule as editing the fields directly.
            config.DisableSyncForCurrentWorld();
            Plugin.Instance?.RefreshSettingsUI();
        }

        // No settings-dialog control for this anymore — toggle with /vault sync
        // (or /vs) in-game instead. The property itself, and its per-world/
        // per-host-profile persistence, are unchanged; only the dialog checkbox
        // was removed.
        public bool SyncEnabled
        {
            get => FindWorldBinding(GameStateReader.CurrentWorldKey, ActiveProfileId)?.SyncEnabled ?? false;
            set => GetOrCreateWorldBinding(GameStateReader.CurrentWorldKey, ActiveProfileId).SyncEnabled = value;
        }

        [WorldSyncList(label: "Sync Active On", description: "Every world/server + host profile combination that currently has sync turned on. Toggle it in-game with /vault sync (or /vs).")]
        public string ActiveWorldsSummary
        {
            get
            {
                var active = worldBindings.Where(b => b.SyncEnabled).ToList();
                if (active.Count == 0)
                    return "(none)";

                var profileNamesById = profiles.ToDictionary(p => p.Id, p => p.Nickname);
                return string.Join("\n", active.Select(b =>
                {
                    var profileName = profileNamesById.TryGetValue(b.ProfileId, out var name) ? name : "(deleted profile)";
                    var worldName = string.IsNullOrWhiteSpace(b.DisplayName) ? b.WorldKey : b.DisplayName;
                    return $"{worldName} — {profileName}";
                }));
            }
        }

        [Separator("Sync intervals")]

        [Slider(1f, 300f, 1f, SliderAttribute.SliderType.Integer, description: "How often (seconds) to check the vault for GPS points added/edited elsewhere (Discord, other players). Your own in-game GPS edits push immediately, not on this timer.")]
        public int GpsIntervalSeconds
        {
            get => gpsIntervalSeconds;
            set => SetField(ref gpsIntervalSeconds, value);
        }

        [Slider(1f, 60f, 1f, SliderAttribute.SliderType.Integer, description: "How often (seconds) to push live player position")]
        public int PositionIntervalSeconds
        {
            get => positionIntervalSeconds;
            set => SetField(ref positionIntervalSeconds, value);
        }

        [Slider(1f, 60f, 1f, SliderAttribute.SliderType.Integer, description: "How often (seconds) to push detected hostile signals (antenna/sensor contacts currently visible on your HUD)")]
        public int HostilesIntervalSeconds
        {
            get => hostilesIntervalSeconds;
            set => SetField(ref hostilesIntervalSeconds, value);
        }

        // No [RenderAfter] — lands at the very bottom of the dialog, same as
        // every other un-anchored [Button] method (all property rows render
        // first, then methods in declaration order; see SettingsGenerator.
        // ExtractAttributes). Static for the same CreateDelegate(target: null)
        // reason as the other [Button] methods above.
        [Button(label: "Bring Your Own Bot", description: "Opens the GPS Vault bot's GitHub repo, for hosting your own instance instead of using someone else's.")]
        public static void OpenRepoLink()
        {
            try
            {
                System.Diagnostics.Process.Start("https://github.com/Blues-Hailfire/Space-Engineers_GPS_Vault");
            }
            catch (Exception e)
            {
                Log.Instance?.Warn($"Failed to open repo link: {e.Message}");
            }
        }

        #endregion

        #region Profiles / per-world sync state

        public List<SyncProfile> Profiles
        {
            get => profiles;
            set => profiles = value ?? new List<SyncProfile>();
        }

        public List<WorldSyncBinding> WorldBindings
        {
            get => worldBindings;
            set => worldBindings = value ?? new List<WorldSyncBinding>();
        }

        /// <summary>
        /// The profile currently selected in the dropdown. Lazily creates a
        /// first profile if none exist yet (fresh install, or every profile
        /// was deleted) so the endpoint/token/channel textboxes always have
        /// something to edit.
        /// </summary>
        public SyncProfile ActiveProfile
        {
            get
            {
                if (profiles.Count == 0)
                {
                    var profile = new SyncProfile { Id = Guid.NewGuid().ToString("N"), Nickname = "Default" };
                    profiles.Add(profile);
                    activeProfileId = profile.Id;
                    return profile;
                }

                return profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? profiles[0];
            }
        }

        private void SetActiveProfileField(Func<SyncProfile, string> getter, Action<SyncProfile, string> setter, string value)
        {
            var profile = ActiveProfile;
            if (getter(profile) == value)
                return;
            setter(profile, value);
            DisableSyncForCurrentWorld();
        }

        private WorldSyncBinding FindWorldBinding(string worldKey, string profileId)
        {
            if (string.IsNullOrEmpty(worldKey))
                return null;
            return worldBindings.FirstOrDefault(b => b.WorldKey == worldKey && b.ProfileId == profileId);
        }

        private WorldSyncBinding GetOrCreateWorldBinding(string worldKey, string profileId)
        {
            var binding = FindWorldBinding(worldKey, profileId);

            // No current world (e.g. dialog opened outside a session) — hand
            // back a throwaway binding rather than null so callers can still
            // set/read it without special-casing; it's never persisted since
            // it's never added to worldBindings.
            if (binding == null && string.IsNullOrEmpty(worldKey))
                return new WorldSyncBinding { WorldKey = "", ProfileId = profileId };

            if (binding == null)
            {
                binding = new WorldSyncBinding { WorldKey = worldKey, ProfileId = profileId };
                worldBindings.Add(binding);
            }

            // Refreshed on every touch (every call always passes the CURRENT
            // world's key), so a server's advertised HostName change is
            // reflected next time the player toggles sync there, without
            // needing a dedicated background refresh mechanism.
            var displayName = GameStateReader.CurrentWorldDisplayName;
            if (!string.IsNullOrEmpty(displayName))
                binding.DisplayName = displayName;

            return binding;
        }

        private void DisableSyncForCurrentWorld()
        {
            var worldKey = GameStateReader.CurrentWorldKey;
            if (string.IsNullOrEmpty(worldKey))
                return;
            GetOrCreateWorldBinding(worldKey, ActiveProfileId).SyncEnabled = false;
        }

        /// <summary>
        /// Folds a pre-multi-profile .cfg's flat Endpoint/Token/ChannelId
        /// into a real SyncProfile, once, on load. Called by ConfigStorage
        /// right after deserializing so old installs don't silently lose
        /// their settings when this version's Profiles list comes back empty.
        /// </summary>
        public void MigrateLegacyProfile()
        {
            if (profiles.Count > 0)
                return;
            if (string.IsNullOrEmpty(Endpoint) && string.IsNullOrEmpty(Token) && string.IsNullOrEmpty(ChannelId))
                return;

            var profile = new SyncProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Nickname = "Default",
                Endpoint = string.IsNullOrEmpty(Endpoint) ? new SyncProfile().Endpoint : Endpoint,
                Token = Token ?? "",
                ChannelId = ChannelId ?? "",
            };
            profiles.Add(profile);
            activeProfileId = profile.Id;
        }

        #endregion

        #region Property change notification boilerplate

        public static readonly Config Default = new Config();
        public static readonly Config Current = ConfigStorage.Load();

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        #endregion
    }
}
