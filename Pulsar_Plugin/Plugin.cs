using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GpsSyncPlugin.Settings;
using GpsSyncPlugin.Settings.Layouts;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Plugins;

namespace GpsSyncPlugin
{
    // Loaded by Pulsar via the standard VRage.Plugins.IPlugin contract.
    // ReSharper disable once UnusedType.Global
    public class Plugin : IPlugin, IDisposable
    {
        public const string Name = "GpsSyncPluginPulsar";
        public static Plugin Instance { get; private set; }

        private const int PlanetsIntervalSeconds = 300; // planets are effectively static; no need to poll them often
        private const int OresIntervalSeconds = 30;

        // How long after a local GPS edit to hold off applying pulled vault
        // data, so the just-pushed edit (see OnLocalGpsChanged) has time to
        // reach the server and beat a concurrent/soon-after pull — otherwise
        // a pull mid-edit could re-apply the stale pre-edit value locally.
        // This is what makes the player's own in-game edit take priority.
        private static readonly TimeSpan LocalGpsEditGracePeriod = TimeSpan.FromSeconds(6);

        // Coalesces bursts of rapid local GPS events (e.g. rename + move) into
        // a single push instead of one per event.
        private const int GpsPushDebounceMs = 800;

        // Pulsar-only: how long the player can go without any keyboard/mouse/
        // joystick input before all sync/comms shut off — see UpdateAfkState.
        private const double AfkTimeoutSeconds = 3600;

        private SettingsGenerator settingsGenerator;
        private Timer gpsTimer;
        private Timer positionTimer;
        private Timer planetsTimer;
        private Timer oresTimer;
        private Timer gpsPushDebounceTimer;
        private volatile bool gpsSyncing;
        private volatile bool positionSyncing;
        private volatile bool planetsSyncing;
        private volatile bool asteroidsSyncing;
        // Set by GameStateReader.AsteroidsChanged (a new voxel map appeared); consumed by Update().
        private volatile bool asteroidsPushPending;
        private volatile bool oresSyncing;
        private DateTime lastLocalGpsEditUtc = DateTime.MinValue;

        // Tracks what CheckForGpsPushTriggers last saw, to detect "just joined
        // this world" / "sync just turned on for this world+profile" edge
        // transitions — see that method.
        private string lastSeenWorldKey;
        private bool lastSeenSyncEnabled;

        // AFK state — read only on the game thread (Update()); the volatile
        // bool is the only piece the background timers touch, so no lock is
        // needed for it.
        private DateTime lastInputUtc = DateTime.UtcNow;
        private bool sessionWasReady;
        private volatile bool isAfk;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void Init(object gameInstance)
        {
            Instance = this;
            Log.Init();
            settingsGenerator = new SettingsGenerator();

            // Background timers, NOT the game's Update() loop: Update() fires on the
            // simulation thread ~60x/sec and must never block on network I/O.
            // Interval changes made in the settings dialog take effect on next launch.
            var config = Config.Current;
            var gpsMs = Math.Max(1, config.GpsIntervalSeconds) * 1000;
            var positionMs = Math.Max(1, config.PositionIntervalSeconds) * 1000;
            var planetsMs = PlanetsIntervalSeconds * 1000;
            gpsTimer = new Timer(OnGpsTick, null, gpsMs, gpsMs);
            positionTimer = new Timer(OnPositionTick, null, positionMs, positionMs);
            planetsTimer = new Timer(OnPlanetsTick, null, 5000, planetsMs); // first attempt shortly after load
            oresTimer = new Timer(OnOresTick, null, 10000, OresIntervalSeconds * 1000);
            gpsPushDebounceTimer = new Timer(OnGpsPushDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
            GameStateReader.LocalGpsChanged += OnLocalGpsChanged;
            GameStateReader.AsteroidsChanged += OnAsteroidsChanged;

            // "Host Locally": runs for the whole game session, main menu
            // included, and stops in Dispose (or with the game process
            // itself, via LocalServerHost's job object).
            Config.HostLocallyChanged += OnHostLocallyChanged;
            if (config.HostLocally)
                LocalServerHost.Start(config.EnsureLocalProfile());

            Log.Instance.Info($"Initialized. Endpoint={config.Endpoint}");
        }

        private static void OnHostLocallyChanged(bool enabled)
        {
            if (!enabled)
            {
                LocalServerHost.Stop();
                MyAPIGateway.Utilities?.ShowMessage("Vault", "Host Locally: local server stopped.");
                return;
            }

            var token = Config.Current.EnsureLocalProfile();
            LocalServerHost.Start(token);
            // Shows the new "Local (this PC)" entry in the Host Profile dropdown.
            Instance?.RefreshSettingsUI();
        }

        // ReSharper disable once UnusedMember.Global
        // Discovered and invoked by Pulsar via reflection (looks for this exact
        // method name) to show a "Configure" button for the plugin.
        public void OpenConfigDialog()
        {
            Instance.settingsGenerator.SetLayout<Simple>();
            MyGuiSandbox.AddScreen(Instance.settingsGenerator.Dialog);
        }

        private volatile bool refreshingSettingsUI;

        /// <summary>
        /// Rebuilds the settings dialog's controls from Config's current
        /// state. Needed after anything that changes the shape of the UI
        /// itself (profiles added/removed/renamed) rather than just a single
        /// field's value — the dialog only reads Config.Profiles when its
        /// controls are (re)created, not continuously. Must run on the game
        /// thread; callers off it (e.g. an async name lookup) should hop via
        /// MyAPIGateway.Utilities.InvokeOnGameThread first. IsLoaded guards
        /// against a lookup that finishes after the player already closed
        /// the dialog — rebuilding a screen that isn't showing is wasted at
        /// best and pokes controls (e.g. the scroll panel) that only get set
        /// up while the screen is loaded. refreshingSettingsUI guards against
        /// stack-overflow-by-reentrancy: some MyGui controls (e.g. a combobox's
        /// SelectItemByIndex) fire their "changed" event synchronously even
        /// for a programmatic selection made while (re)building the dialog,
        /// so a naive rebuild-on-change handler can call back into this
        /// method before the outer rebuild that triggered it has returned.
        /// </summary>
        public void RefreshSettingsUI()
        {
            if (refreshingSettingsUI)
                return;

            var dialog = settingsGenerator?.Dialog;
            if (dialog == null || !dialog.IsLoaded)
                return;

            refreshingSettingsUI = true;
            try
            {
                dialog.RecreateControls(false);
            }
            finally
            {
                refreshingSettingsUI = false;
            }
        }

        /// <summary>
        /// Periodic PULL only — picks up points added/edited elsewhere (Discord,
        /// another player). Pushing the local player's own GPS list is
        /// event-driven now (see OnLocalGpsChanged/OnGpsPushDebounceElapsed),
        /// not tied to this timer — except for the explicit one-off triggers in
        /// Update() (CheckForGpsPushTriggers): joining a world/server, or
        /// switching to a sync-enabled world+profile combination, doesn't raise
        /// the game's own GPS-changed event, so a player who already had GPS
        /// points beforehand would otherwise never push them at all.
        /// </summary>
        private void OnGpsTick(object state)
        {
            // Skip this cycle rather than queue up overlapping syncs if the last
            // one is still in flight (e.g. a slow/stalled connection), and skip
            // while a local edit is still settling so a stale pull can't
            // clobber it — see LocalGpsEditGracePeriod.
            if (gpsSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled || isAfk)
                return;
            if (DateTime.UtcNow - lastLocalGpsEditUtc < LocalGpsEditGracePeriod)
                return;

            gpsSyncing = true;
            Task.Run(async () =>
            {
                try
                {
                    var remotePoints = await SyncClient.Shared.FetchGpsAsync();
                    if (remotePoints == null || remotePoints.Count == 0)
                        return;

                    try
                    {
                        await GameThread.RunAsync(() =>
                        {
                            GameStateReader.ApplyRemoteGpsList(remotePoints);
                            return true;
                        });
                    }
                    catch (TimeoutException)
                    {
                        Log.Instance?.Warn("GPS pull tick: game thread hop timed out, skipping this cycle.");
                    }
                }
                catch (Exception e)
                {
                    Log.Instance?.Error($"GPS pull tick failed: {e.Message}");
                }
                finally
                {
                    gpsSyncing = false;
                }
            });
        }

        /// <summary>
        /// Fires (on the game thread) the instant the local player's own GPS
        /// list changes in-game. Just records when, then (re)schedules the
        /// debounce timer — the actual push happens in
        /// OnGpsPushDebounceElapsed once edits stop arriving for a beat, so a
        /// burst of rapid changes (e.g. rename + move) becomes one push.
        /// </summary>
        private void OnLocalGpsChanged()
        {
            lastLocalGpsEditUtc = DateTime.UtcNow;
            gpsPushDebounceTimer?.Change(GpsPushDebounceMs, Timeout.Infinite);
        }

        private void OnGpsPushDebounceElapsed(object state)
        {
            if (gpsSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled || isAfk)
            {
                // Try again shortly rather than dropping the edit on the floor
                // if a pull happens to be in flight right now.
                gpsPushDebounceTimer?.Change(GpsPushDebounceMs, Timeout.Infinite);
                return;
            }

            gpsSyncing = true;
            Task.Run(async () =>
            {
                try
                {
                    List<GpsPointDto> points;
                    try
                    {
                        points = await GameThread.RunAsync(GameStateReader.ReadGpsList);
                    }
                    catch (TimeoutException)
                    {
                        Log.Instance?.Warn("GPS push: game thread hop timed out.");
                        return;
                    }

                    // Push even when empty — an in-game deletion needs to reach
                    // the server too (see ApplyRemoteGpsList/ReadGpsList: the
                    // server treats a push as this player's full authoritative
                    // list and deletes vault points missing from it).
                    await SyncClient.Shared.SyncGpsAsync(new GpsSyncRequest { points = points });
                }
                catch (Exception e)
                {
                    Log.Instance?.Error($"GPS push failed: {e.Message}");
                }
                finally
                {
                    gpsSyncing = false;
                }
            });
        }

        private void OnPositionTick(object state)
        {
            if (positionSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled || isAfk)
                return;

            positionSyncing = true;
            Task.Run(async () =>
            {
                try
                {
                    PositionSyncRequest position;
                    try
                    {
                        position = await GameThread.RunAsync(GameStateReader.ReadPlayerPosition);
                    }
                    catch (TimeoutException)
                    {
                        Log.Instance?.Warn("Position sync tick: game thread hop timed out, skipping this cycle.");
                        return;
                    }

                    if (position != null)
                        await SyncClient.Shared.SyncPositionAsync(position);
                }
                catch (Exception e)
                {
                    Log.Instance?.Error($"Position sync tick failed: {e.Message}");
                }
                finally
                {
                    positionSyncing = false;
                }
            });
        }

        private void OnPlanetsTick(object state)
        {
            if (planetsSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled || isAfk)
                return;

            planetsSyncing = true;
            Task.Run(async () =>
            {
                try
                {
                    List<PlanetDto> planets;
                    try
                    {
                        planets = await GameThread.RunAsync(GameStateReader.ReadPlanets);
                    }
                    catch (TimeoutException)
                    {
                        Log.Instance?.Warn("Planets sync tick: game thread hop timed out, skipping this cycle.");
                        return;
                    }

                    if (planets.Count > 0)
                        await SyncClient.Shared.SyncPlanetsAsync(new PlanetSyncRequest { planets = planets });
                }
                catch (Exception e)
                {
                    Log.Instance?.Error($"Planets sync tick failed: {e.Message}");
                }
                finally
                {
                    planetsSyncing = false;
                }
            });
        }

        private void OnAsteroidsChanged() => asteroidsPushPending = true;

        /// <summary>
        /// Event-driven asteroid push: GameStateReader.AsteroidsChanged flags
        /// that a new voxel map appeared (or the session just started), and
        /// this — checked every Update() tick, a cheap flag test — sends the
        /// full asteroid list once the session/sync is ready. A flag rather
        /// than pushing straight from the event so a change that arrives
        /// while sync is off, the session isn't ready yet, or a push is
        /// already in flight is picked up afterwards instead of lost.
        /// </summary>
        private void TryPushAsteroids()
        {
            if (!asteroidsPushPending || asteroidsSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled || isAfk)
                return;

            asteroidsPushPending = false;
            asteroidsSyncing = true;
            Task.Run(async () =>
            {
                try
                {
                    // Brief settle so a burst of asteroids streaming in together
                    // becomes one push — anything arriving after the read below
                    // re-sets the flag and gets its own.
                    await Task.Delay(1500);

                    List<AsteroidDto> asteroids;
                    try
                    {
                        asteroids = await GameThread.RunAsync(GameStateReader.ReadAsteroids);
                    }
                    catch (TimeoutException)
                    {
                        Log.Instance?.Warn("Asteroids sync: game thread hop timed out, will retry.");
                        asteroidsPushPending = true;
                        return;
                    }

                    if (asteroids.Count > 0)
                    {
                        await SyncClient.Shared.SyncAsteroidsAsync(new AsteroidSyncRequest { asteroids = asteroids });
                        Log.Instance?.Info($"Asteroids: pushed {asteroids.Count}.");
                    }
                }
                catch (Exception e)
                {
                    Log.Instance?.Error($"Asteroids sync failed: {e.Message}");
                    asteroidsPushPending = true;
                }
                finally
                {
                    asteroidsSyncing = false;
                }
            });
        }

        /// <summary>
        /// Pull only: fetches every deposit anyone in the channel has found
        /// and adds the new ones to the in-game GPS list (Show on HUD off —
        /// see OreDeposits.ApplyRemoteOreDeposits). The local ore detector's
        /// own deposits are never pushed automatically — only when the player
        /// runs /vault capture (see ChatCommands.HandleCapture).
        /// </summary>
        private void OnOresTick(object state)
        {
            if (oresSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled || isAfk)
                return;

            oresSyncing = true;
            Task.Run(async () =>
            {
                try
                {
                    var remote = await SyncClient.Shared.FetchOresAsync();
                    if (remote != null && remote.Count > 0)
                    {
                        await GameThread.RunAsync(() =>
                        {
                            OreDeposits.ApplyRemoteOreDeposits(remote);
                            return true;
                        });
                    }
                }
                catch (TimeoutException)
                {
                    Log.Instance?.Warn("Ores sync tick: game thread hop timed out, skipping this cycle.");
                }
                catch (Exception e)
                {
                    Log.Instance?.Error($"Ores sync tick failed: {e.Message}");
                }
                finally
                {
                    oresSyncing = false;
                }
            });
        }

        public void Dispose()
        {
            GameStateReader.LocalGpsChanged -= OnLocalGpsChanged;
            GameStateReader.AsteroidsChanged -= OnAsteroidsChanged;
            GameStateReader.ShutdownAsteroidSubscription();
            Config.HostLocallyChanged -= OnHostLocallyChanged;
            LocalServerHost.Stop();
            GameStateReader.ShutdownGpsSubscription();
            gpsTimer?.Dispose();
            gpsTimer = null;
            positionTimer?.Dispose();
            positionTimer = null;
            planetsTimer?.Dispose();
            planetsTimer = null;
            oresTimer?.Dispose();
            oresTimer = null;
            gpsPushDebounceTimer?.Dispose();
            gpsPushDebounceTimer = null;
            ChatCommands.Dispose();
            SpectrumBridge.Unload();
            WeaponCoreBridge.Unload();
            Instance = null;
        }

        public void Update()
        {
            // All sync work runs on the background timers above; this only retries
            // the chat-command subscription until MyAPIGateway.Utilities is ready
            // (it's still null when Init() runs). No-op once subscribed.
            ChatCommands.TryInit();

            // Cheap no-op once subscribed to the current session's GPS collection;
            // re-subscribes if a new session/collection appears (e.g. rejoining a
            // different world). Must run on the game thread, which Update() is.
            GameStateReader.EnsureGpsSubscription();
            GameStateReader.EnsureAsteroidSubscription();

            TryPushAsteroids();

            // Optional Spectrum mod handshake — see SpectrumBridge. No-op without it.
            SpectrumBridge.Update();
            WeaponCoreBridge.Update();

            UpdateAfkState();
            CheckForGpsPushTriggers();
        }

        /// <summary>
        /// Pulsar-only: pauses every sync/comms path (see the isAfk checks in
        /// the On*Tick methods above) once the player has gone AfkTimeoutSeconds
        /// without any keyboard/mouse/joystick input, and resumes it the
        /// instant input comes back. The idle clock resets whenever the
        /// session just became ready (world join/rejoin) so time spent at the
        /// main menu or on a loading screen is never counted against the
        /// player.
        /// </summary>
        private void UpdateAfkState()
        {
            var sessionReady = GameStateReader.IsSessionReady;
            if (!sessionReady)
            {
                sessionWasReady = false;
                return;
            }

            if (!sessionWasReady)
            {
                lastInputUtc = DateTime.UtcNow;
                sessionWasReady = true;
            }

            if (GameStateReader.HasInputActivity())
                lastInputUtc = DateTime.UtcNow;

            var idle = DateTime.UtcNow - lastInputUtc;
            if (!isAfk && idle.TotalSeconds >= AfkTimeoutSeconds)
            {
                isAfk = true;
                MyHud.Chat.ShowMessage("Vault", "Player AFK Vault-Sync Deactivated!", ChatCommands.VaultMessageColor);
                Log.Instance?.Info("Player AFK — sync/comms deactivated.");
            }
            else if (isAfk && idle.TotalSeconds < AfkTimeoutSeconds)
            {
                isAfk = false;
                MyHud.Chat.ShowMessage("Vault", "Player Returned! Vault-Sync Re-Activated!", ChatCommands.VaultMessageColor);
                Log.Instance?.Info("Player active again — sync/comms reactivated.");
                // Catch up on anything that happened while sync was paused,
                // same path a real in-game GPS edit takes.
                OnLocalGpsChanged();
            }
        }

        /// <summary>
        /// Fires an immediate GPS push (same path as a real in-game edit — see
        /// OnLocalGpsChanged) the moment a world/server connects, or the moment
        /// sync becomes active for the current world+profile — whichever the
        /// player didn't already have. Needed because the local player's own
        /// GPS list doesn't raise the game's GpsChanged/GpsAdded/ListChanged
        /// events just from connecting or from Config.SyncEnabled flipping on:
        /// without this, a player who already had GPS points before enabling
        /// sync (the common case) would never push them at all, since nothing
        /// would ever have triggered the event push relies on. Cheap no-op
        /// once both trackers catch up to the current state; called every
        /// Update() tick like EnsureGpsSubscription above.
        /// </summary>
        private void CheckForGpsPushTriggers()
        {
            if (!GameStateReader.IsSessionReady)
            {
                // Reset so returning to a world (the same one or a different one)
                // is treated as a fresh join and re-triggers below.
                lastSeenWorldKey = null;
                lastSeenSyncEnabled = false;
                return;
            }

            var worldKey = GameStateReader.CurrentWorldKey;
            var syncEnabled = Config.Current.SyncEnabled;

            var justJoinedWorld = worldKey != lastSeenWorldKey;
            var syncJustEnabled = syncEnabled && !lastSeenSyncEnabled;

            lastSeenWorldKey = worldKey;
            lastSeenSyncEnabled = syncEnabled;

            if (syncEnabled && (justJoinedWorld || syncJustEnabled))
                OnLocalGpsChanged();
        }
    }
}
