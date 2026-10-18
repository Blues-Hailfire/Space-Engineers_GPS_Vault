using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GpsSyncPlugin.Settings;
using GpsSyncPlugin.Settings.Layouts;
using Sandbox.Graphics.GUI;
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
        private const int OresIntervalSeconds = 30;

        // One asteroid mesh is built and sent per tick (see
        // AsteroidShape.ReadNextShapes), so the game-thread cost is spread out.
        private const int ShapesIntervalSeconds = 1;
        // Per-frame time slice for sampling planet terrain tiles (see TerrainTiles.Advance),
        // and how often to ask the server which tiles the web map is waiting on.
        private const double TerrainSampleBudgetMs = 3.0;
        private const int TerrainIntervalSeconds = 1;
        // How often to forget which asteroids were already scanned, so a
        // mined/changed rock's shape eventually gets re-sent.
        private const int ShapesResyncIntervalSeconds = 1800;

        // How long after a local GPS edit to hold off applying pulled vault
        // data, so the just-pushed edit (see OnLocalGpsChanged) has time to
        // reach the server and beat a concurrent/soon-after pull — otherwise
        // a pull mid-edit could re-apply the stale pre-edit value locally.
        // This is what makes the player's own in-game edit take priority.
        private static readonly TimeSpan LocalGpsEditGracePeriod = TimeSpan.FromSeconds(6);

        // Coalesces bursts of rapid local GPS events (e.g. rename + move) into
        // a single push instead of one per event.
        private const int GpsPushDebounceMs = 800;

        // How long to wait before retrying a pull-then-push (see
        // RequestGpsPullThenPush) whose pull failed.
        private const int GpsPullRetryMs = 5000;

        private SettingsGenerator settingsGenerator;
        private Timer gpsTimer;
        private Timer positionTimer;
        private Timer planetsTimer;
        private Timer oresTimer;
        private Timer shapesTimer;
        private Timer terrainTimer;
        private volatile bool terrainSyncing;
        private Timer shapesResyncTimer;
        private Timer hostilesTimer;
        private Timer gpsPushDebounceTimer;
        private volatile bool gpsSyncing;
        private volatile bool positionSyncing;
        private volatile bool planetsSyncing;
        private volatile bool asteroidsSyncing;
        // Set by GameStateReader.AsteroidsChanged (a new voxel map appeared); consumed by Update().
        private volatile bool asteroidsPushPending;
        private volatile bool oresSyncing;
        private volatile bool shapesSyncing;
        private volatile bool hostilesSyncing;
        private DateTime lastLocalGpsEditUtc = DateTime.MinValue;
        // Set by RequestGpsPullThenPush; the next push applies a pull first.
        private volatile bool pullBeforeNextGpsPush;

        // Tracks what CheckForGpsPushTriggers last saw, to detect "just joined
        // this world (or switched profile/channel)" / "sync just turned on for
        // this world+profile" edge transitions — see that method.
        private string lastSeenSyncScopeKey;
        private bool lastSeenSyncEnabled;

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
            oresTimer = new Timer(OnOresTick, null, 10000, OresIntervalSeconds * 1000);
            shapesTimer = new Timer(OnAsteroidShapeTick, null, 15000, ShapesIntervalSeconds * 1000);
            terrainTimer = new Timer(OnTerrainTick, null, 10000, TerrainIntervalSeconds * 1000);
            shapesResyncTimer = new Timer(
                (s) => AsteroidShape.ResetSyncedTracking(),
                null, ShapesResyncIntervalSeconds * 1000, ShapesResyncIntervalSeconds * 1000);
            hostilesTimer = new Timer(OnHostilesTick, null, hostilesMs, hostilesMs);
            gpsPushDebounceTimer = new Timer(OnGpsPushDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
            GameStateReader.LocalGpsChanged += OnLocalGpsChanged;
            GameStateReader.AsteroidsChanged += OnAsteroidsChanged;

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

        /// <summary>
        /// Schedules a push that first pulls and applies the vault's current
        /// list (see OnGpsPushDebounceElapsed). For the pushes not caused by a
        /// local edit — joining a world, sync turning on, returning from AFK —
        /// where the local list may be long out of date.
        /// </summary>
        private void RequestGpsPullThenPush()
        {
            pullBeforeNextGpsPush = true;
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
                    // After a join / sync-enable / AFK return the local list
                    // may be stale, so apply the vault's current state first —
                    // otherwise this push would overwrite coords/colors that
                    // Discord or other players changed meanwhile with the old
                    // local copy. Not done for edit-triggered pushes: applying
                    // a pull there would clobber the very edit being pushed
                    // (see LocalGpsEditGracePeriod).
                    if (pullBeforeNextGpsPush)
                    {
                        var remotePoints = await SyncClient.Shared.FetchGpsAsync();
                        if (remotePoints == null)
                        {
                            gpsPushDebounceTimer?.Change(GpsPullRetryMs, Timeout.Infinite);
                            return;
                        }

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
                            Log.Instance?.Warn("GPS pull-then-push: game thread hop timed out, retrying.");
                            gpsPushDebounceTimer?.Change(GpsPullRetryMs, Timeout.Infinite);
                            return;
                        }
                        pullBeforeNextGpsPush = false;
                    }

                    GpsSyncRequest request;
                    var trackingGeneration = 0;
                    try
                    {
                        // One hop, so the points and the deletions come from
                        // the same snapshot of the local list.
                        request = await GameThread.RunAsync(() =>
                        {
                            trackingGeneration = GameStateReader.VaultTrackingGeneration;
                            return new GpsSyncRequest
                            {
                                points = GameStateReader.ReadGpsList(),
                                deleted = GameStateReader.ReadDeletedVaultNames(),
                            };
                        });
                    }
                    catch (TimeoutException)
                    {
                        Log.Instance?.Warn("GPS push: game thread hop timed out.");
                        return;
                    }

                    // The server trashes only the names in `deleted` (vault
                    // points this player had and has since removed in-game),
                    // never "whatever's missing from points" — which would
                    // trash points other players added that this player just
                    // hasn't pulled yet.
                    if (!await SyncClient.Shared.SyncGpsAsync(request))
                        return;

                    try
                    {
                        await GameThread.RunAsync(() =>
                        {
                            GameStateReader.RecordGpsPush(trackingGeneration, request.points, request.deleted);
                            return true;
                        });
                    }
                    catch (TimeoutException)
                    {
                        // Harmless: the next successful push records it again.
                        Log.Instance?.Warn("GPS push: game thread hop timed out recording the push.");
                    }
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
            if (!asteroidsPushPending || asteroidsSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled)
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
        /// Terrain tile exchange with the server: uploads tiles finished since
        /// the last tick, then — unless a backlog is already queued — asks which
        /// tiles the web map is waiting on and queues them for sampling in
        /// Update() (see TerrainTiles). A poll rather than a push because the
        /// server can't reach into the game; an idle poll is one tiny request.
        /// </summary>
        private void OnTerrainTick(object state)
        {
            if (terrainSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled)
                return;

            terrainSyncing = true;
            Task.Run(async () =>
            {
                try
                {
                    var done = TerrainTiles.TakeCompleted(24);
                    if (done.Count > 0)
                        await SyncClient.Shared.SyncTerrainTilesAsync(new TerrainTileSyncRequest { tiles = done });

                    if (TerrainTiles.Backlog < 24)
                    {
                        var requests = await SyncClient.Shared.FetchTerrainRequestsAsync();
                        if (requests != null && requests.Count > 0)
                            TerrainTiles.Enqueue(requests);
                    }
                }
                catch (Exception e)
                {
                    Log.Instance?.Error($"Terrain tile tick failed: {e.Message}");
                }
                finally
                {
                    terrainSyncing = false;
                }
            });
        }

        private void OnAsteroidShapeTick(object state)
        {
            if (shapesSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled)
                return;

            shapesSyncing = true;
            Task.Run(async () =>
            {
                try
                {
                    List<AsteroidShapeDto> shapes;
                    try
                    {
                        shapes = await GameThread.RunAsync(() => AsteroidShape.ReadNextShapes());
                    }
                    catch (TimeoutException)
                    {
                        Log.Instance?.Warn("Asteroid shape sync tick: game thread hop timed out, skipping this cycle.");
                        return;
                    }

                    if (shapes.Count > 0)
                        await SyncClient.Shared.SyncAsteroidShapesAsync(new AsteroidShapeSyncRequest { shapes = shapes });
                }
                catch (Exception e)
                {
                    Log.Instance?.Error($"Asteroid shape sync tick failed: {e.Message}");
                }
                finally
                {
                    shapesSyncing = false;
                }
            });
        }

        /// <summary>
        /// Pushes whatever the local ore detector currently shows, then pulls
        /// every deposit anyone in the channel has found and adds the new ones
        /// to the in-game GPS list (Show on HUD off — see
        /// OreDeposits.ApplyRemoteOreDeposits).
        /// </summary>
        private void OnOresTick(object state)
        {
            if (oresSyncing || !GameStateReader.IsSessionReady || !Config.Current.SyncEnabled)
                return;

            oresSyncing = true;
            Task.Run(async () =>
            {
                try
                {
                    var local = await GameThread.RunAsync(OreDeposits.ReadOreDeposits);
                    // Optional Asteroid Scanner mod results — see AsteroidScannerBridge.
                    // Empty without it.
                    local.AddRange(await GameThread.RunAsync(AsteroidScannerBridge.ReadScannedDeposits));
                    if (local.Count > 0)
                        await SyncClient.Shared.SyncOresAsync(new OreDepositSyncRequest { deposits = local });

                    // Scanned asteroids out of streaming range have no voxel map
                    // here for the asteroid sync to find, so the map would have
                    // nothing to attach their ore point to.
                    var scannedAsteroids = await GameThread.RunAsync(AsteroidScannerBridge.ReadUnloadedScannedAsteroids);
                    if (scannedAsteroids.Count > 0
                        && await SyncClient.Shared.SyncAsteroidsAsync(new AsteroidSyncRequest { asteroids = scannedAsteroids }))
                    {
                        await GameThread.RunAsync(() =>
                        {
                            AsteroidScannerBridge.MarkAsteroidsPushed(scannedAsteroids);
                            return true;
                        });
                    }

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
            GameStateReader.AsteroidsChanged -= OnAsteroidsChanged;
            GameStateReader.ShutdownAsteroidSubscription();
            GameStateReader.ShutdownGpsSubscription();
            gpsTimer?.Dispose();
            gpsTimer = null;
            positionTimer?.Dispose();
            positionTimer = null;
            planetsTimer?.Dispose();
            planetsTimer = null;
            oresTimer?.Dispose();
            oresTimer = null;
            shapesTimer?.Dispose();
            shapesTimer = null;
            terrainTimer?.Dispose();
            terrainTimer = null;
            shapesResyncTimer?.Dispose();
            shapesResyncTimer = null;
            hostilesTimer?.Dispose();
            hostilesTimer = null;
            gpsPushDebounceTimer?.Dispose();
            gpsPushDebounceTimer = null;
            ChatCommands.Dispose();
            SpectrumBridge.Unload();
            WeaponCoreBridge.Unload();
            AsteroidScannerBridge.Unload();
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

            // Planet terrain tile sampling: a small per-frame time slice, resumed each frame.
            if (Config.Current.SyncEnabled)
                TerrainTiles.Advance(TerrainSampleBudgetMs);
            TryPushAsteroids();

            // Optional Spectrum mod handshake — see SpectrumBridge. No-op without it.
            SpectrumBridge.Update();
            WeaponCoreBridge.Update();
            AsteroidScannerBridge.Update();

            CheckForGpsPushTriggers();
        }

        /// <summary>
        /// Fires an immediate pull-then-push (see RequestGpsPullThenPush) the
        /// moment a world/server connects (or the host profile/channel
        /// changes), or the moment
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
                lastSeenSyncScopeKey = null;
                lastSeenSyncEnabled = false;
                return;
            }

            // Everything that decides which vault channel this player syncs
            // with: a change means the known vault names belong to another
            // channel and must not be reported as deletions to this one.
            var config = Config.Current;
            var syncScopeKey = GameStateReader.CurrentWorldKey + "|" + config.ActiveProfileId + "|"
                               + config.ActiveEndpoint + "|" + config.ActiveChannelId;
            var syncEnabled = config.SyncEnabled;

            var syncScopeChanged = syncScopeKey != lastSeenSyncScopeKey;
            var syncJustEnabled = syncEnabled && !lastSeenSyncEnabled;

            lastSeenSyncScopeKey = syncScopeKey;
            lastSeenSyncEnabled = syncEnabled;

            if (syncScopeChanged)
                GameStateReader.ResetVaultTracking();

            if (syncEnabled && (syncScopeChanged || syncJustEnabled))
                RequestGpsPullThenPush();
        }
    }
}
