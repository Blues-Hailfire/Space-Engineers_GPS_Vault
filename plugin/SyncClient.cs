using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace GpsSyncPlugin
{
    /// <summary>
    /// Pushes state to the bot's sync API. Every call is fire-and-forget from the
    /// caller's perspective and swallows its own errors — a dropped connection
    /// must never surface into the game. Endpoint/token are read fresh from
    /// Config.Current on every call, so edits made in the settings dialog apply
    /// immediately without needing a restart.
    /// </summary>
    public class SyncClient
    {
        // Shared instance: HttpClient is meant to be reused for the app's lifetime
        // rather than one per caller (each instance owns its own connection pool).
        public static readonly SyncClient Shared = new SyncClient();

        private readonly HttpClient http;

        public SyncClient()
        {
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        private static void AddAuthHeaders(HttpRequestMessage requestMessage)
        {
            AddAuthHeaders(requestMessage, Config.Current.ActiveToken, Config.Current.ActiveChannelId);
        }

        private static void AddAuthHeaders(HttpRequestMessage requestMessage, string token, string channelId)
        {
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var trimmedChannelId = channelId?.Trim();
            if (!string.IsNullOrEmpty(trimmedChannelId))
                requestMessage.Headers.Add("X-Channel-Id", trimmedChannelId);
        }

        public async Task SyncGpsAsync(GpsSyncRequest request)
        {
            await PostAsync("/sync/gps", request);
        }

        public async Task SyncPositionAsync(PositionSyncRequest request)
        {
            await PostAsync("/sync/position", request);
        }

        public async Task SyncPlanetsAsync(PlanetSyncRequest request)
        {
            await PostAsync("/sync/planets", request);
        }

        public async Task SyncHostilesAsync(HostileSignalSyncRequest request)
        {
            await PostAsync("/sync/hostiles", request);
        }

        /// <summary>
        /// Pulls the channel's current vault GPS list (Discord-added/edited
        /// points included) so it can be merged into the local player's
        /// in-game GPS list. Returns null on any failure.
        /// </summary>
        public async Task<List<GpsPointDto>> FetchGpsAsync()
        {
            try
            {
                var endpoint = Config.Current.ActiveEndpoint.TrimEnd('/');
                using (var requestMessage = new HttpRequestMessage(HttpMethod.Get, endpoint + "/sync/gps"))
                {
                    AddAuthHeaders(requestMessage);
                    using (var response = await http.SendAsync(requestMessage))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var text = await response.Content.ReadAsStringAsync();
                            Log.Instance?.Warn($"Sync GET /sync/gps failed: {(int)response.StatusCode} {text}");
                            return null;
                        }
                        var json = await response.Content.ReadAsStringAsync();
                        return JsonConvert.DeserializeObject<GpsSyncRequest>(json)?.points;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Instance?.Warn($"Sync GET /sync/gps error: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Other active players' current positions in this channel (used by
        /// the /vault players ("/vp") command). Returns null on any failure.
        /// </summary>
        public async Task<List<VaultPlayerDto>> FetchActivePlayersAsync(string excludeName)
        {
            try
            {
                var endpoint = Config.Current.ActiveEndpoint.TrimEnd('/');
                var query = string.IsNullOrEmpty(excludeName) ? "" : "?exclude=" + Uri.EscapeDataString(excludeName);
                using (var requestMessage = new HttpRequestMessage(HttpMethod.Get, endpoint + "/sync/players" + query))
                {
                    AddAuthHeaders(requestMessage);
                    using (var response = await http.SendAsync(requestMessage))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var text = await response.Content.ReadAsStringAsync();
                            Log.Instance?.Warn($"Sync GET /sync/players failed: {(int)response.StatusCode} {text}");
                            return null;
                        }
                        var json = await response.Content.ReadAsStringAsync();
                        return JsonConvert.DeserializeObject<VaultPlayersResponse>(json)?.players;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Instance?.Warn($"Sync GET /sync/players error: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolves a profile's endpoint/token/channel to the Discord server
        /// and channel name it points at, so the settings dialog can show a
        /// readable label instead of a raw snowflake ID. Takes the profile's
        /// fields explicitly (rather than reading Config.Current) since the
        /// caller may be looking up a profile that isn't the active one.
        /// Returns a WhoAmIResponse with .error set (not null) on a server
        /// error (e.g. 401 "invalid or missing token" — a bad/blank Token or
        /// wrong Endpoint, the two most common causes), so the caller can
        /// show the actual reason. Returns null only when the server
        /// couldn't be reached at all (network failure, wrong host/port,
        /// TLS problem, etc.) — that case has no server-provided reason.
        /// </summary>
        public async Task<WhoAmIResponse> FetchWhoAmIAsync(string endpoint, string token, string channelId)
        {
            try
            {
                var trimmedEndpoint = (endpoint ?? "").TrimEnd('/');
                using (var requestMessage = new HttpRequestMessage(HttpMethod.Get, trimmedEndpoint + "/sync/whoami"))
                {
                    AddAuthHeaders(requestMessage, token, channelId);
                    using (var response = await http.SendAsync(requestMessage))
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                        {
                            Log.Instance?.Warn($"Sync GET /sync/whoami failed: {(int)response.StatusCode} {json}");
                            WhoAmIResponse errorResponse;
                            try
                            {
                                errorResponse = JsonConvert.DeserializeObject<WhoAmIResponse>(json) ?? new WhoAmIResponse();
                            }
                            catch (JsonException)
                            {
                                errorResponse = new WhoAmIResponse();
                            }
                            if (string.IsNullOrEmpty(errorResponse.error))
                                errorResponse.error = $"HTTP {(int)response.StatusCode}";
                            return errorResponse;
                        }
                        return JsonConvert.DeserializeObject<WhoAmIResponse>(json);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Instance?.Warn($"Sync GET /sync/whoami error: {e.Message}");
                return null;
            }
        }

        private async Task PostAsync(string path, object body)
        {
            try
            {
                var endpoint = Config.Current.ActiveEndpoint.TrimEnd('/');
                var json = JsonConvert.SerializeObject(body);
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint + path) { Content = content })
                {
                    AddAuthHeaders(requestMessage);
                    using (var response = await http.SendAsync(requestMessage))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var text = await response.Content.ReadAsStringAsync();
                            Log.Instance?.Warn($"Sync POST {path} failed: {(int)response.StatusCode} {text}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Instance?.Warn($"Sync POST {path} error: {e.Message}");
            }
        }
    }
}
