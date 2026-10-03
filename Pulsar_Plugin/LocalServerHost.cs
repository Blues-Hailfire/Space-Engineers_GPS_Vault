using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Sandbox.Game.Gui;
using Sandbox.ModAPI;
using VRage.FileSystem;

namespace GpsSyncPlugin
{
    /// <summary>
    /// "Host Locally": downloads and runs a private copy of the GPS Vault web
    /// server (GpsVaultLocalServer.exe — local_server.py frozen by
    /// .github/workflows/local-server.yml) on 127.0.0.1:Port for as long as
    /// the game is running.
    ///
    /// The exe comes from the repo's rolling "local-server-latest" release,
    /// rebuilt on every push to main; it's re-downloaded only when that
    /// release's asset id changes, and the cached copy is used when GitHub
    /// can't be reached. The server authenticates the plugin with
    /// Config.LocalHostToken, passed through an environment variable.
    ///
    /// Lifetime: the server process is placed in a Windows Job Object that
    /// kills everything in it when its last handle closes — which the OS
    /// does when the game process exits for ANY reason, crash included.
    /// That also covers the PyInstaller "onefile" launcher spawning the real
    /// server as a child process, which a plain Process.Kill would orphan.
    /// </summary>
    public static class LocalServerHost
    {
        public const int Port = 4040;
        public static readonly string Endpoint = $"http://127.0.0.1:{Port}";

        private const string Repo = "Blues-Hailfire/Space-Engineers_GPS_Vault";
        private const string ReleaseTag = "local-server-latest";
        private const string AssetName = "GpsVaultLocalServer.exe";

        private static readonly object gate = new object();
        private static Process process;
        private static int generation; // bumped by Stop() to cancel an in-flight Start()
        private static IntPtr job = IntPtr.Zero;

        private static string RootDir => Path.Combine(MyFileSystem.UserDataPath, "Storage", "GpsVaultLocalServer");
        private static string DataDir => Path.Combine(RootDir, "data");
        private static string ExePath => Path.Combine(RootDir, AssetName);
        private static string VersionPath => Path.Combine(RootDir, "version.txt");
        private static string ServerLogPath => Path.Combine(RootDir, "server.log");

        public static bool IsRunning
        {
            get
            {
                lock (gate)
                    return process != null && !process.HasExited;
            }
        }

        /// <summary>Starts the server in the background (download first if needed). No-op if already running.</summary>
        public static void Start(string token)
        {
            int myGeneration;
            lock (gate)
            {
                if (process != null && !process.HasExited)
                    return;
                myGeneration = ++generation;
            }

            Task.Run(async () =>
            {
                try
                {
                    var exe = await EnsureExeAsync();
                    if (exe == null)
                    {
                        Notify("Host Locally: couldn't download the local server, and there's no cached copy yet. Check your connection, then toggle it again.");
                        return;
                    }

                    lock (gate)
                    {
                        if (myGeneration != generation)
                            return; // Stop() was called while we were downloading

                        if (IsPortInUse())
                        {
                            Notify($"Host Locally: port {Port} is already in use on this PC (another server running?). Not starting.");
                            return;
                        }

                        Directory.CreateDirectory(DataDir);
                        var psi = new ProcessStartInfo(exe, $"--data-dir \"{DataDir}\" --port {Port}")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = DataDir,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        };
                        psi.EnvironmentVariables["GPSVAULT_LOCAL_TOKEN"] = token;
                        psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

                        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                        p.OutputDataReceived += (s, e) => AppendServerLog(e.Data);
                        p.ErrorDataReceived += (s, e) => AppendServerLog(e.Data);
                        p.Exited += (s, e) => Log.Instance?.Info($"Local server exited (code {SafeExitCode(p)}).");
                        p.Start();
                        AssignToJob(p);
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        process = p;
                    }

                    Log.Instance?.Info($"Local server started: {Endpoint}");
                    Notify($"Host Locally: running — map at http://localhost:{Port}/map");
                }
                catch (Exception e)
                {
                    Log.Instance?.Error($"Local server failed to start: {e.Message}");
                    Notify("Host Locally: failed to start the local server — see the plugin log.");
                }
            });
        }

        /// <summary>Stops the server (and cancels a start that's still downloading). Safe to call anytime.</summary>
        public static void Stop()
        {
            lock (gate)
            {
                generation++;
                var p = process;
                process = null;

                // Kills the launcher AND the real server it spawned.
                if (job != IntPtr.Zero)
                    TerminateJobObject(job, 0);

                try
                {
                    if (p != null && !p.HasExited)
                        p.Kill();
                }
                catch (Exception e)
                {
                    Log.Instance?.Warn($"Local server stop: {e.Message}");
                }
                p?.Dispose();
            }
        }

        private static async Task<string> EnsureExeAsync()
        {
            Directory.CreateDirectory(RootDir);

            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("GpsVault-Datalink-Plugin");

                    var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/tags/{ReleaseTag}");
                    var asset = (JObject.Parse(json)["assets"] as JArray)?
                        .FirstOrDefault(a => (string)a["name"] == AssetName);
                    var assetId = asset?["id"]?.ToString();
                    var url = (string)asset?["browser_download_url"];

                    var cachedId = File.Exists(VersionPath) ? File.ReadAllText(VersionPath).Trim() : null;
                    if (assetId != null && url != null && (assetId != cachedId || !File.Exists(ExePath)))
                    {
                        Notify("Host Locally: downloading the latest local server...");
                        var tmp = ExePath + ".download";
                        using (var response = await http.GetAsync(url))
                        {
                            response.EnsureSuccessStatusCode();
                            using (var file = File.Create(tmp))
                                await response.Content.CopyToAsync(file);
                        }
                        if (File.Exists(ExePath))
                            File.Delete(ExePath);
                        File.Move(tmp, ExePath);
                        File.WriteAllText(VersionPath, assetId);
                        Log.Instance?.Info($"Local server downloaded (asset {assetId}).");
                    }
                }
            }
            catch (Exception e)
            {
                // Offline, GitHub down, rate-limited, or no release published
                // yet — fall back to whatever's cached, if anything.
                Log.Instance?.Warn($"Local server update check failed, using cached copy if any: {e.Message}");
            }

            return File.Exists(ExePath) ? ExePath : null;
        }

        private static bool IsPortInUse()
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == Port);
        }

        private static readonly object logGate = new object();

        private static void AppendServerLog(string line)
        {
            if (line == null)
                return;
            try
            {
                lock (logGate)
                    File.AppendAllText(ServerLogPath, $"[{DateTime.Now:O}] {line}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never crash the plugin.
            }
        }

        private static string SafeExitCode(Process p)
        {
            try { return p.ExitCode.ToString(); }
            catch { return "?"; }
        }

        private static void Notify(string message)
        {
            Log.Instance?.Info(message);
            var utilities = MyAPIGateway.Utilities;
            if (utilities == null || MyAPIGateway.Session == null)
                return; // main menu — the log line above is all we can do
            utilities.InvokeOnGameThread(() => MyHud.Chat?.ShowMessage("Vault", message, ChatCommands.VaultMessageColor));
        }

        // ── Windows Job Object: kill the server tree when the game exits ──

        private static void AssignToJob(Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                        (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
                    // The handle is intentionally never closed: the OS closes
                    // it when the game exits, which is what kills the server.
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch (Exception e)
            {
                // Non-Windows (e.g. Proton) or restricted environment: Stop()
                // on normal shutdown still works, only crash cleanup is lost.
                Log.Instance?.Warn($"Local server: couldn't attach to a job object ({e.Message}) — it may outlive a game crash.");
            }
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
