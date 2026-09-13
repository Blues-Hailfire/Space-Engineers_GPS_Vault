using System;
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

        private SettingsGenerator settingsGenerator;
        private SyncClient syncClient;
        private Timer gpsTimer;
        private Timer positionTimer;
        private Timer planetsTimer;
        private Timer hostilesTimer;
        private volatile bool gpsSyncing;
        private volatile bool positionSyncing;
        private volatile bool planetsSyncing;
        private volatile bool hostilesSyncing;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void Init(object gameInstance)
        {
            Instance = this;
            Log.Init();
            settingsGenerator = new SettingsGenerator();
            syncClient = new SyncClient();

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

        private void OnGpsTick(object state)
        {
            // Skip this cycle rather than queue up overlapping syncs if the last
            // one is still in flight (e.g. a slow/stalled connection).
            if (gpsSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled)
                return;

            gpsSyncing = true;
            Task.Run(async () =>
            {
                try
                {
                    // Pull BEFORE push: the server treats a GPS push as that player's
                    // full authoritative list and deletes vault points missing from
                    // it (so deleting a GPS in-game deletes it from the vault too).
                    // Pulling first means anything newly added elsewhere (Discord,
                    // another player) is already back in the local list by push time,
                    // instead of looking "deleted" for one cycle.
                    var remotePoints = await syncClient.FetchGpsAsync();
                    if (remotePoints != null && remotePoints.Count > 0)
                    {
                        // AddGps/ModifyGps are network-mutating calls, so they must
                        // run on the game's main thread rather than this background one.
                        MyAPIGateway.Utilities?.InvokeOnGameThread(() =>
                        {
                            try
                            {
                                GameStateReader.ApplyRemoteGpsList(remotePoints);
                            }
                            catch (Exception e)
                            {
                                // Runs decoupled from this method's call stack (scheduled
                                // onto the game thread), so it must catch its own errors.
                                Log.Instance?.Error($"Applying remote GPS list failed: {e.Message}");
                            }
                        });

                        // Give the game thread a moment to process the queued action
                        // above before reading the (now up-to-date) list back out below.
                        await Task.Delay(250);
                    }

                    var points = GameStateReader.ReadGpsList();
                    if (points.Count > 0)
                        await syncClient.SyncGpsAsync(new GpsSyncRequest { points = points });
                }
                catch (Exception e)
                {
                    Log.Instance?.Error($"GPS sync tick failed: {e.Message}");
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
                    var position = GameStateReader.ReadPlayerPosition();
                    if (position != null)
                        await syncClient.SyncPositionAsync(position);
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
                    var planets = GameStateReader.ReadPlanets();
                    if (planets.Count > 0)
                        await syncClient.SyncPlanetsAsync(new PlanetSyncRequest { planets = planets });
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
                    var signals = GameStateReader.ReadHostileSignals();
                    if (signals.Count > 0)
                        await syncClient.SyncHostilesAsync(new HostileSignalSyncRequest { signals = signals });
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
            gpsTimer?.Dispose();
            gpsTimer = null;
            positionTimer?.Dispose();
            positionTimer = null;
            planetsTimer?.Dispose();
            planetsTimer = null;
            hostilesTimer?.Dispose();
            hostilesTimer = null;
            ChatCommands.Dispose();
            Instance = null;
        }

        public void Update()
        {
            // All sync work runs on the background timers above; this only retries
            // the chat-command subscription until MyAPIGateway.Utilities is ready
            // (it's still null when Init() runs). No-op once subscribed.
            ChatCommands.TryInit();
        }
    }
}
