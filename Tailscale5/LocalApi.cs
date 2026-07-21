// LocalApi: a thin C# client for tailscaled's LocalAPI.
//
// This is the Tizen 5.0 port of Tailscale/LocalApi.cs. The Tizen 8 build used
// SocketsHttpHandler.ConnectCallback (a .NET 5+ API) to run HttpClient over an
// AF_UNIX socket. That API does not exist on the .NET Core runtime shipped with
// Tizen 5.0, so here we talk to tailscaled directly: open the unix socket, write
// a minimal HTTP/1.0 request, and read the response off the raw stream.
//
// Why HTTP/1.0 and not 1.1: with 1.1 the Go http.Server in tailscaled would
// frame streaming responses (watch-ipn-bus) with Transfer-Encoding: chunked,
// which we'd then have to de-chunk by hand. With 1.0 there is no chunked
// framing at all — one-shot responses are delimited by connection-close, and
// the long-lived watch stream is written raw (each ipn.Notify is one flushed
// ndjson line). So a plain StreamReader.ReadLine over the socket Just Works for
// both cases, and the whole transport stays ~one screen of code. If a future
// tailscaled ever rejects 1.0 on the localapi, switch the request line to 1.1
// and add a chunked-decoding body stream here.
//
// We keep the same public surface as the Tizen 8 LocalApi so Program.cs can use
// either interchangeably:
//   - Status (one-shot ipnstate.Status JSON)
//   - EditPrefs / Start to bring the engine up
//   - StartLoginInteractive to trigger an auth URL
//   - Logout
//   - WatchIPNBus, the streaming ipn.Notify long-poll
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Tailscale
{
    // Tizen 5.0 compiles against a netstandard2.0-level surface that lacks
    // System.Net.Sockets.UnixDomainSocketEndPoint (that type needs 2.1). So we
    // hand-roll the AF_UNIX endpoint: Socket.Connect marshals an EndPoint via
    // Serialize(), and a Linux sockaddr_un is just [family:2][path][NUL]. This
    // is the well-worn pre-2.1 workaround.
    sealed class UnixEndPoint : EndPoint
    {
        private readonly string _path;
        public UnixEndPoint(string path) { _path = path; }
        public override AddressFamily AddressFamily => AddressFamily.Unix;

        public override SocketAddress Serialize()
        {
            byte[] p = Encoding.UTF8.GetBytes(_path);
            // The SocketAddress ctor writes the family into bytes [0..1]; we
            // fill sun_path from offset 2 and NUL-terminate.
            var sa = new SocketAddress(AddressFamily.Unix, 2 + p.Length + 1);
            for (int i = 0; i < p.Length; i++) sa[2 + i] = p[i];
            sa[2 + p.Length] = 0;
            return sa;
        }

        public override EndPoint Create(SocketAddress socketAddress) => this;
        public override string ToString() => _path;
    }

    sealed class LocalApi : IDisposable
    {
        private readonly string _socketPath;

        // tailscaled's localapi guards against DNS-rebinding/CSRF by requiring a
        // fixed Host and the Sec-Tailscale header; the Tizen 8 client sent the
        // same pair (BaseAddress host "local-tailscaled.sock" + Sec-Tailscale
        // header), so we reproduce both verbatim.
        private const string HostHeader = "local-tailscaled.sock";

        public LocalApi(string socketPath)
        {
            _socketPath = socketPath;
        }

        // ---- transport --------------------------------------------------------

        // Open a fresh AF_UNIX connection to tailscaled. We use the APM
        // Begin/End pattern because the Task-returning Socket.ConnectAsync(EndPoint)
        // overload is .NET 5+; BeginConnect/EndConnect exist on every runtime.
        private async Task<Socket> ConnectAsync()
        {
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var ep = new UnixEndPoint(_socketPath);
            await Task.Factory.FromAsync(
                (cb, st) => s.BeginConnect(ep, cb, st),
                s.EndConnect,
                null);
            return s;
        }

        // Write a full HTTP/1.0 request. body==null means no body (GET); a
        // non-null (possibly empty) body sends Content-Length and, when given,
        // Content-Type.
        private static async Task WriteRequestAsync(Stream stream, string method, string path, string body, string contentType, CancellationToken ct)
        {
            byte[] bodyBytes = body != null ? Encoding.UTF8.GetBytes(body) : Array.Empty<byte>();

            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.0\r\n");
            sb.Append("Host: ").Append(HostHeader).Append("\r\n");
            sb.Append("Sec-Tailscale: localapi\r\n");
            if (body != null)
            {
                if (contentType != null)
                    sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
                sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            }
            sb.Append("Connection: close\r\n\r\n");

            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(head, 0, head.Length, ct);
            if (bodyBytes.Length > 0)
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, ct);
            await stream.FlushAsync(ct);
        }

        // Read and parse the status line + headers, leaving the stream positioned
        // at the first body byte. Reads a byte at a time — headers are tiny and
        // this avoids any read-ahead into a streaming body (crucial for watch).
        private static async Task<int> ReadStatusAsync(Stream stream, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                int n = await stream.ReadAsync(one, 0, 1, ct);
                if (n == 0) break; // EOF before end of headers
                sb.Append((char)one[0]);
                int len = sb.Length;
                if (len >= 4 && sb[len - 1] == '\n' && sb[len - 2] == '\r' && sb[len - 3] == '\n' && sb[len - 4] == '\r')
                    break;
            }

            // First line: "HTTP/1.0 200 OK".
            string headers = sb.ToString();
            int eol = headers.IndexOf("\r\n", StringComparison.Ordinal);
            string statusLine = eol >= 0 ? headers.Substring(0, eol) : headers;
            string[] parts = statusLine.Split(new[] { ' ' }, 3);
            if (parts.Length >= 2 && int.TryParse(parts[1], out int code))
                return code;
            throw new Exception("malformed status line: " + statusLine);
        }

        private static async Task<string> ReadToEndAsync(Stream stream, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            byte[] buf = new byte[4096];
            while (true)
            {
                int n = await stream.ReadAsync(buf, 0, buf.Length, ct);
                if (n == 0) break;
                ms.Write(buf, 0, n);
            }
            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }

        // One-shot request/response: connect, send, read the whole body, close.
        private async Task<string> RequestAsync(string method, string path, string body, string contentType, CancellationToken ct)
        {
            using var socket = await ConnectAsync();
            using var stream = new NetworkStream(socket, ownsSocket: false);
            await WriteRequestAsync(stream, method, path, body, contentType, ct);
            int status = await ReadStatusAsync(stream, ct);
            string respBody = await ReadToEndAsync(stream, ct);
            if (status < 200 || status >= 300)
                throw new Exception(path + " -> " + status + ": " + respBody);
            return respBody;
        }

        // ---- API surface ------------------------------------------------------

        public async Task<JsonNode> Status(CancellationToken ct = default)
        {
            string body = await RequestAsync("GET", "/localapi/v0/status", null, null, ct);
            return JsonNode.Parse(body);
        }

        // Start sends a minimal IPN start request which causes tailscaled to
        // honor any previously-saved prefs. With a fresh state it will move
        // straight to NeedsLogin.
        public Task Start(CancellationToken ct = default) =>
            RequestAsync("POST", "/localapi/v0/start", "{}", "application/json", ct);

        // EditPrefs patches a subset of ipn.Prefs; "ControlURL", "Hostname",
        // "WantRunning", "AdvertiseRoutes", "AdvertiseTags" etc, with each
        // mutated key flagged in the *Set fields.
        public Task EditPrefs(JsonObject maskedPrefs, CancellationToken ct = default) =>
            RequestAsync("PATCH", "/localapi/v0/prefs", maskedPrefs.ToJsonString(), "application/json", ct);

        public Task StartLoginInteractive(CancellationToken ct = default) =>
            RequestAsync("POST", "/localapi/v0/login-interactive", "", "text/plain", ct);

        public Task Logout(CancellationToken ct = default) =>
            RequestAsync("POST", "/localapi/v0/logout", "", "text/plain", ct);

        // WatchIPNBus opens the ipn-bus long-poll and invokes onNotify for each
        // ipn.Notify JSON object as it arrives. It runs until the stream ends
        // (or the app exits). We use a callback rather than IAsyncEnumerable /
        // `await foreach` because async streams are not in the BCL of the old
        // .NET Core runtime on Tizen 5.0 (they'd need a Microsoft.Bcl.
        // AsyncInterfaces polyfill); a callback keeps the transport dependency-
        // free on that runtime.
        //
        // The mask is the OR of the desired NotifyWatchOpt bits; 7 (== state +
        // netmap + prefs) is the set the LocalClient defaults to.
        public async Task WatchIPNBus(int mask, Action<JsonNode> onNotify, CancellationToken ct = default)
        {
            Socket socket = await ConnectAsync();
            using (var stream = new NetworkStream(socket, ownsSocket: true))
            {
                await WriteRequestAsync(stream, "GET", "/localapi/v0/watch-ipn-bus?mask=" + mask, null, null, ct);
                int status = await ReadStatusAsync(stream, ct);
                if (status < 200 || status >= 300)
                    throw new Exception("watch-ipn-bus -> " + status);

                // tailscaled writes one JSON object per line ("ndjson"); parse
                // line-by-line off the raw HTTP/1.0 body stream.
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    while (!ct.IsCancellationRequested)
                    {
                        string line;
                        try { line = await reader.ReadLineAsync(); }
                        catch { break; } // peer closed / socket faulted
                        if (line == null) break;
                        if (line.Length == 0) continue;
                        JsonNode node = null;
                        try { node = JsonNode.Parse(line); } catch { }
                        if (node != null) onNotify(node);
                    }
                }
            }
        }

        public void Dispose() { }
    }
}
