using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GpsSyncPlugin.Settings;
using GpsSyncPlugin.Settings.Layouts;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Plugins;

namespace GpsSyncPlugin
{
    // Loaded by Pulsar via the standard VRage.Plugins.IPlugin contract.
    // ReSharper disable once UnusedType.Global
    public class Plugin : IPlugin, IDisposable
    {
        public const string Name = "GpsSyncPlugin";
        public static Plugin Instance { get; private set; }

        private const int PlanetsIntervalSeconds = 300; // planets are effectively static; no need to poll them often

        // How long after a local GPS edit to hold off applying pulled vault
        // data, so the just-pushed edit (see OnLocalGpsChanged) has time to
        // reach the server and beat a concurrent/soon-after pull — otherwise
        // a pull mid-edit could re-apply the stale pre-edit value locally.
        // This is what makes the player's own in-game edit take priority.
        private static readonly TimeSpan LocalGpsEditGracePeriod = TimeSpan.FromSeconds(6);

        // Coalesces bursts of rapid local GPS events (e.g. rename + move) into
        // a single push instead of one per event.
        private const int GpsPushDebounceMs = 800;

        private SettingsGenerator settingsGenerator;
        private Timer gpsTimer;
        private Timer positionTimer;
        private Timer planetsTimer;
        private Timer hostilesTimer;
        private Timer gpsPushDebounceTimer;
        private volatile bool gpsSyncing;
        private volatile bool positionSyncing;
        private volatile bool planetsSyncing;
        private volatile bool hostilesSyncing;
        private DateTime lastLocalGpsEditUtc = DateTime.MinValue;

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
            var hostilesMs = Math.Max(1, config.HostilesIntervalSeconds) * 1000;
            gpsTimer = new Timer(OnGpsTick, null, gpsMs, gpsMs);
            positionTimer = new Timer(OnPositionTick, null, positionMs, positionMs);
            planetsTimer = new Timer(OnPlanetsTick, null, 5000, planetsMs); // first attempt shortly after load
            hostilesTimer = new Timer(OnHostilesTick, null, hostilesMs, hostilesMs);
            gpsPushDebounceTimer = new Timer(OnGpsPushDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
            GameStateReader.LocalGpsChanged += OnLocalGpsChanged;

            Log.Instance.Info($"Initialized. Endpoint={config.Endpoint}");
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
        /// not tied to this timer.
        /// </summary>
        private void OnGpsTick(object state)
        {
            // Skip this cycle rather than queue up overlapping syncs if the last
            // one is still in flight (e.g. a slow/stalled connection), and skip
            // while a local edit is still settling so a stale pull can't
            // clobber it — see LocalGpsEditGracePeriod.
            if (gpsSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled)
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
            if (gpsSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled)
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
            if (positionSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled)
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
            if (planetsSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled)
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

        private void OnHostilesTick(object state)
        {
            if (hostilesSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled)
                return;

            hostilesSyncing = true;
            Task.Run(async () =>
            {
                try
                {
                    List<HostileSignalDto> signals;
                    try
                    {
                        signals = await GameThread.RunAsync(GameStateReader.ReadHostileSignals);
                    }
                    catch (TimeoutException)
                    {
                        Log.Instance?.Warn("Hostile signal sync tick: game thread hop timed out, skipping this cycle.");
                        return;
                    }

                    if (signals.Count > 0)
                        await SyncClient.Shared.SyncHostilesAsync(new HostileSignalSyncRequest { signals = signals });
                }
                catch (Exception e)
                {
                    Log.Instance?.Error($"Hostile signal sync tick failed: {e.Message}");
                }
                finally
                {
                    hostilesSyncing = false;
                }
            });
        }

        public void Dispose()
        {
            GameStateReader.LocalGpsChanged -= OnLocalGpsChanged;
            GameStateReader.ShutdownGpsSubscription();
            gpsTimer?.Dispose();
            gpsTimer = null;
            positionTimer?.Dispose();
            positionTimer = null;
            planetsTimer?.Dispose();
            planetsTimer = null;
            hostilesTimer?.Dispose();
            hostilesTimer = null;
            gpsPushDebounceTimer?.Dispose();
            gpsPushDebounceTimer = null;
            ChatCommands.Dispose();
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
        }
    }
}
