// LocalApi: a thin C# client for tailscaled's LocalAPI.
//
// The LocalAPI speaks HTTP over a Unix domain socket. We model that with a
// SocketsHttpHandler whose ConnectCallback returns a NetworkStream wrapping
// the connected AF_UNIX socket. Requests carry the Sec-Tailscale: localapi
// CSRF header tailscaled requires.
//
// We expose just the slice we need for the login flow:
//   - Status (one-shot ipnstate.Status JSON)
//   - EditPrefs / Start to bring the engine up
//   - StartLoginInteractive to trigger an auth URL
//   - WatchIPNBus, the streaming ipn.Notify long-poll
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Tailscale
{
    sealed class LocalApi : IDisposable
    {
        private readonly HttpClient _http;

        public LocalApi(string socketPath)
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (ctx, ct) =>
                {
                    var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await s.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                    return new NetworkStream(s, ownsSocket: true);
                },
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            };
            _http = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://local-tailscaled.sock"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _http.DefaultRequestHeaders.Add("Sec-Tailscale", "localapi");
        }

        public async Task<JsonNode> Status(CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync("/localapi/v0/status", ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException("status " + (int)resp.StatusCode + ": " + body);
            return JsonNode.Parse(body);
        }

        // Start sends a minimal IPN start request which causes tailscaled to
        // honor any previously-saved prefs. With a fresh state it will move
        // straight to NeedsLogin.
        public async Task Start(CancellationToken ct = default)
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("/localapi/v0/start", content, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException("start " + (int)resp.StatusCode + ": " + body);
        }

        // EditPrefs patches a subset of ipn.Prefs; "ControlURL", "Hostname",
        // "WantRunning", "AdvertiseRoutes", "AdvertiseTags" etc, with each
        // mutated key flagged in the *Set fields.
        public async Task EditPrefs(JsonObject maskedPrefs, CancellationToken ct = default)
        {
            var content = new StringContent(maskedPrefs.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.PatchAsync("/localapi/v0/prefs", content, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException("editprefs " + (int)resp.StatusCode + ": " + body);
        }

        public async Task StartLoginInteractive(CancellationToken ct = default)
        {
            using var resp = await _http.PostAsync("/localapi/v0/login-interactive", new StringContent(""), ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException("login-interactive " + (int)resp.StatusCode + ": " + body);
        }

        public async Task Logout(CancellationToken ct = default)
        {
            using var resp = await _http.PostAsync("/localapi/v0/logout", new StringContent(""), ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException("logout " + (int)resp.StatusCode + ": " + body);
        }

        // WatchIPNBus opens the ipn-bus long-poll and yields each ipn.Notify
        // JSON object as it arrives. The stream stays open until the caller
        // cancels.
        //
        // The mask is the OR of the desired NotifyWatchOpt bits; 7 (== state +
        // netmap + prefs) is the set the LocalClient defaults to.
        public async IAsyncEnumerable<JsonNode> WatchIPNBus(int mask = 7, [EnumeratorCancellation] CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/localapi/v0/watch-ipn-bus?mask=" + mask);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            // tailscaled writes one JSON object per line ("ndjson"); parse line-by-line.
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!ct.IsCancellationRequested)
            {
                string line = await reader.ReadLineAsync().WaitAsync(ct);
                if (line == null) yield break;
                if (line.Length == 0) continue;
                JsonNode node = null;
                try { node = JsonNode.Parse(line); } catch { }
                if (node != null) yield return node;
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
