using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Tailscale
{
    // Per-app Tailscale routing without a TUN.
    //
    // tailscaled runs with --socks5-server=localhost:<socksPort>. For each
    // configured entry this forwarder puts a plain TCP listener on
    // 127.0.0.1:<localPort> and splices every connection to a SOCKS5 CONNECT
    // into tailscaled, so a Tizen wgt that can only point at
    // http://127.0.0.1:<port> reaches a service on the tailnet. tailscaled's
    // SOCKS5 dialer resolves MagicDNS, so a target can be a hostname.
    //
    // Listeners bind loopback only (never 0.0.0.0), start when IPN state is
    // Running, and stop otherwise so apps get connection-refused fast before
    // the tailnet is up. Config persists as JSON in the app data dir.
    sealed class ForwardEntry
    {
        public int LocalPort { get; set; }
        public string TargetHost { get; set; }
        public int TargetPort { get; set; }
        public override string ToString() => $"127.0.0.1:{LocalPort} -> {TargetHost}:{TargetPort}";
    }

    sealed class Forwarder
    {
        private readonly int _socksPort;
        private readonly string _configPath;
        private readonly Action<string> _log;
        private readonly object _gate = new object();
        private readonly List<ForwardEntry> _entries = new List<ForwardEntry>();
        private readonly Dictionary<int, Listener> _listeners = new Dictionary<int, Listener>();
        private bool _running;

        public Forwarder(int socksPort, string configPath, Action<string> log)
        {
            _socksPort = socksPort;
            _configPath = configPath;
            _log = log ?? (_ => { });
            Load();
        }

        public List<ForwardEntry> Entries
        {
            get { lock (_gate) return new List<ForwardEntry>(_entries); }
        }

        public bool IsRunning
        {
            get { lock (_gate) return _running; }
        }

        public string StatusOf(int localPort)
        {
            lock (_gate)
            {
                if (_listeners.TryGetValue(localPort, out var l)) return l.Status;
                return _running ? "not started" : "waiting for tailnet";
            }
        }

        // ---- config ---------------------------------------------------------

        public void Add(ForwardEntry e)
        {
            lock (_gate)
            {
                _entries.RemoveAll(x => x.LocalPort == e.LocalPort);
                _entries.Add(e);
                Save();
            }
            _log("forwarder: added " + e);
            if (IsRunning) RestartListeners();
        }

        public void Remove(int localPort)
        {
            lock (_gate)
            {
                _entries.RemoveAll(x => x.LocalPort == localPort);
                Save();
            }
            if (IsRunning) RestartListeners();
        }

        public void RemoveAll()
        {
            lock (_gate) { _entries.Clear(); Save(); }
            StopListeners();
            _log("forwarder: all forwards removed");
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_configPath)) return;
                var list = JsonSerializer.Deserialize<List<ForwardEntry>>(File.ReadAllText(_configPath));
                if (list != null) { _entries.Clear(); _entries.AddRange(list); }
                _log($"forwarder: loaded {_entries.Count} forward(s)");
            }
            catch (Exception ex) { _log("forwarder: load failed: " + ex.Message); }
        }

        private void Save()
        {
            try { File.WriteAllText(_configPath, JsonSerializer.Serialize(_entries)); }
            catch (Exception ex) { _log("forwarder: save failed: " + ex.Message); }
        }

        // ---- lifecycle ------------------------------------------------------

        public void Start()
        {
            lock (_gate) { if (_running) return; _running = true; }
            _log("forwarder: tailnet up, starting listeners");
            RestartListeners();
        }

        public void Stop()
        {
            lock (_gate) { if (!_running) return; _running = false; }
            _log("forwarder: tailnet down, stopping listeners");
            StopListeners();
        }

        private void RestartListeners()
        {
            StopListeners();
            List<ForwardEntry> snapshot;
            lock (_gate) { if (!_running) return; snapshot = new List<ForwardEntry>(_entries); }
            foreach (var e in snapshot)
            {
                try
                {
                    var l = new Listener(e, _socksPort, _log);
                    l.Start();
                    lock (_gate) _listeners[e.LocalPort] = l;
                }
                catch (Exception ex) { _log($"forwarder: listen {e.LocalPort} failed: {ex.Message}"); }
            }
        }

        private void StopListeners()
        {
            List<Listener> ls;
            lock (_gate) { ls = new List<Listener>(_listeners.Values); _listeners.Clear(); }
            foreach (var l in ls) l.Dispose();
        }

        // ---- one listener ---------------------------------------------------

        private sealed class Listener : IDisposable
        {
            private readonly ForwardEntry _e;
            private readonly int _socksPort;
            private readonly Action<string> _log;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private TcpListener _tcp;

            public string Status { get; private set; } = "starting";

            public Listener(ForwardEntry e, int socksPort, Action<string> log)
            {
                _e = e; _socksPort = socksPort; _log = log;
            }

            public void Start()
            {
                _tcp = new TcpListener(IPAddress.Loopback, _e.LocalPort);
                _tcp.Start();
                Status = "listening";
                _log($"forwarder: listening 127.0.0.1:{_e.LocalPort} -> {_e.TargetHost}:{_e.TargetPort}");
                _ = AcceptLoop();
            }

            private async Task AcceptLoop()
            {
                var ct = _cts.Token;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var client = await _tcp.AcceptTcpClientAsync();
                        _ = HandleAsync(client, ct);
                    }
                }
                catch (ObjectDisposedException) { }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        Status = "error: " + ex.Message;
                        _log($"forwarder: accept {_e.LocalPort}: {ex.Message}");
                    }
                }
            }

            private async Task HandleAsync(TcpClient client, CancellationToken ct)
            {
                using (client)
                using (var upstream = new TcpClient())
                {
                    try
                    {
                        await upstream.ConnectAsync(IPAddress.Loopback, _socksPort);
                        var s = upstream.GetStream();

                        // SOCKS5 greeting: version 5, 1 method, no-auth.
                        await s.WriteAsync(new byte[] { 5, 1, 0 }, 0, 3, ct);
                        var greet = new byte[2];
                        await ReadFullyAsync(s, greet, 2, ct);
                        if (greet[1] != 0) throw new IOException("socks auth rejected");

                        // CONNECT with domain address type (0x03) so MagicDNS
                        // resolution happens inside tailscaled.
                        var host = Encoding.ASCII.GetBytes(_e.TargetHost ?? "");
                        if (host.Length == 0 || host.Length > 255) throw new IOException("bad target host");
                        var req = new byte[7 + host.Length];
                        req[0] = 5; req[1] = 1; req[2] = 0; req[3] = 3; req[4] = (byte)host.Length;
                        Array.Copy(host, 0, req, 5, host.Length);
                        req[req.Length - 2] = (byte)(_e.TargetPort >> 8);
                        req[req.Length - 1] = (byte)(_e.TargetPort & 0xff);
                        await s.WriteAsync(req, 0, req.Length, ct);

                        // Reply: VER REP RSV ATYP BND.ADDR BND.PORT.
                        var head = new byte[4];
                        await ReadFullyAsync(s, head, 4, ct);
                        if (head[1] != 0) throw new IOException("socks connect failed, code " + head[1]);
                        int addrLen = head[3] == 1 ? 4 : head[3] == 4 ? 16 : head[3] == 3 ? await ReadByteAsync(s, ct) : 0;
                        var rest = new byte[addrLen + 2]; // bound addr + port, discarded
                        await ReadFullyAsync(s, rest, rest.Length, ct);

                        var local = client.GetStream();
                        var up = local.CopyToAsync(s, ct);
                        var down = s.CopyToAsync(local, ct);
                        await Task.WhenAny(up, down);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        _log($"forwarder: {_e.LocalPort} conn: {ex.Message}");
                    }
                }
            }

            // net6.0-tizen8.0 is .NET 6; Stream.ReadExactlyAsync is .NET 7+.
            private static async Task ReadFullyAsync(NetworkStream s, byte[] buf, int count, CancellationToken ct)
            {
                int off = 0;
                while (off < count)
                {
                    int n = await s.ReadAsync(buf, off, count - off, ct);
                    if (n == 0) throw new IOException("unexpected EOF from socks");
                    off += n;
                }
            }

            private static async Task<int> ReadByteAsync(NetworkStream s, CancellationToken ct)
            {
                var b = new byte[1];
                await ReadFullyAsync(s, b, 1, ct);
                return b[0];
            }

            public void Dispose()
            {
                try { _cts.Cancel(); } catch { }
                try { _tcp?.Stop(); } catch { }
                try { _cts.Dispose(); } catch { }
            }
        }
    }
}
