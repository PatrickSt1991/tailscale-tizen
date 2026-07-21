using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using QRCoder;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

// Tizen 5.0 port of the NUI UI. See LocalApi.cs for the transport rewrite.
//
// Why this file diverges from Tailscale/Program.cs: on Tizen 5.0 (TizenFX API
// level 5) the NUI layout system (LinearLayout / View.Layout) is internal, the
// Tizen.NUI.Components.Button control does not exist, and View.CornerRadius was
// not added until 6.0. So the UI is hand-laid-out (absolute Position2D/Size2D)
// with custom View-based focusable "buttons" (TvButton) activated from the OK
// key in Window.KeyEvent. Everything below the "Backend" banner is unchanged
// from the Tizen 8 build -- it only touches Process, HttpListener, LocalApi and
// System.Text.Json, all of which run fine on the 5.0 .NET Core runtime.
namespace Tailscale
{
    // A focusable rounded-less button: a colored View with a centered label that
    // swaps background on focus and raises Clicked when the OK key is pressed
    // while it holds focus (see Program.OnKeyEvent). Replaces
    // Tizen.NUI.Components.Button, absent on 5.0.
    sealed class TvButton : View
    {
        private readonly TextLabel _label;
        private readonly Color _bg;
        private readonly Color _bgFocus;

        public event EventHandler Clicked;

        public TvButton(string text, Color bg, Color bgFocus, Color textColor, float pointSize)
        {
            _bg = bg;
            _bgFocus = bgFocus;
            BackgroundColor = bg;
            Focusable = true;
            _label = new TextLabel(text)
            {
                TextColor = textColor,
                PointSize = pointSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
            };
            Add(_label);
            FocusGained += (s, e) => BackgroundColor = _bgFocus;
            FocusLost += (s, e) => BackgroundColor = _bg;
        }

        public string Text
        {
            get => _label.Text;
            set => _label.Text = value;
        }

        public void RaiseClick() => Clicked?.Invoke(this, EventArgs.Empty);
    }

    // A vertical stack that fills the window and centers its children as a block
    // (horizontally per-item, vertically as a group). Replaces the LinearLayout
    // used on 8.0. Call DoLayout() once after all Put() calls.
    sealed class VStack : View
    {
        private readonly int _gap;
        private readonly List<(View v, int w, int h)> _items = new List<(View, int, int)>();

        public VStack(int winW, int winH, int gap)
        {
            Size2D = new Size2D(winW, winH);
            _gap = gap;
        }

        public T Put<T>(T v, int w, int h) where T : View
        {
            v.Size2D = new Size2D(w, h);
            _items.Add((v, w, h));
            Add(v);
            return v;
        }

        public void DoLayout()
        {
            int total = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                total += _items[i].h;
                if (i > 0) total += _gap;
            }
            int winW = Size2D.Width, winH = Size2D.Height;
            int y = (winH - total) / 2;
            if (y < 0) y = 0;
            foreach (var it in _items)
            {
                it.v.Position2D = new Position2D((winW - it.w) / 2, y);
                y += it.h + _gap;
            }
        }
    }

    class Program : NUIApplication
    {
        // Palette.
        private static readonly Color BgColor = new Color(30f / 255, 30f / 255, 30f / 255, 1.0f);
        private static readonly Color CardColor = new Color(45f / 255, 45f / 255, 45f / 255, 1.0f);
        private static readonly Color CardColorFocus = new Color(70f / 255, 70f / 255, 70f / 255, 1.0f);
        private static readonly Color TextPrimary = new Color(0.96f, 0.96f, 0.96f, 1.0f);
        private static readonly Color TextSecondary = new Color(0.65f, 0.65f, 0.65f, 1.0f);
        private static readonly Color GreenDot = new Color(0.30f, 0.82f, 0.41f, 1.0f);
        private static readonly Color GrayDot = new Color(0.55f, 0.55f, 0.55f, 1.0f);

        // Window size, read once; layout is computed against it.
        private int _winW = 1920;
        private int _winH = 1080;

        // Top-level views; exactly one is visible at a time.
        private View _loggedOutView;
        private View _qrView;
        private View _homeView;
        private View _aboutView;

        // Where we came from when the user opened About -- so Back returns the
        // user to the right place.
        private View _aboutBackTarget;

        // Logged-out widgets.
        private TextLabel _loggedOutStatus;
        private TvButton _logInBtn;
        private TvButton _aboutBtn;
        private TvButton _exitBtn;

        // About widgets.
        private TextLabel _aboutVersionLabel;
        private TvButton _aboutBackBtn;
        private string _tailscaledVersion = "(unknown)";
        private string _vpnProbe = "(vpnsvc not probed)";

        // QR widgets.
        private TextLabel _qrStatusLabel;
        private TextLabel _qrUrlLabel;
        private ImageView _qrImageView;

        // Home view widgets.
        private TextLabel _stateLabel;
        private TextLabel _hostnameLabel;
        private TextLabel _ipv4Label;
        private TextLabel _ipv6Label;
        private TvButton _connectBtn;
        private TvButton _exitNodeBtn;
        private TvButton _signOutBtn;

        private SynchronizationContext _uiCtx;

        private string _tailscaledExe;
        private string _socket;
        private string _stateDir;
        private Process _tailscaledProc;

        private LocalApi _api;

        // Backend state we track to drive the UI.
        private string _state = "";          // last seen IPN state name
        private string _authUrl;             // most recent BrowseToURL
        private bool _wantRunning;
        private bool _exitNodeOn;
        // Set true while a user-initiated login is pending; used to decide
        // between the "Logged out" view and the QR view when the bus says
        // NeedsLogin.
        private bool _userWantsQR;

        private readonly StringBuilder _diag = new StringBuilder();

        protected override void OnCreate()
        {
            base.OnCreate();
            _uiCtx = SynchronizationContext.Current;
            Diag("OnCreate uiCtx=" + (_uiCtx?.GetType().Name ?? "null"));
            try { BuildUI(); Diag("UI built"); }
            catch (Exception ex) { Diag("UI build error: " + ex); }
            ProbeVpnService();
            Task.Run(BackendMain);
            Task.Run(StartDiagServer);
        }

        // ---- Diagnostics ------------------------------------------------------

        private void Diag(string s)
        {
            string line = DateTime.UtcNow.ToString("HH:mm:ss") + " " + s;
            lock (_diag) _diag.AppendLine(line);
            try { Tizen.Log.Info("Tailscale", s); } catch { }
        }

        private void StartDiagServer()
        {
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add("http://+:8081/");
                l.Start();
                Diag("diag listener on :8081");
                while (true)
                {
                    var ctx = l.GetContext();
                    string body;
                    lock (_diag) body = _diag.ToString();
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentType = "text/plain";
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
            }
            catch (Exception ex) { Diag("diag listener: " + ex.Message); }
        }

        // ---- UI construction --------------------------------------------------

        private void BuildUI()
        {
            var ws = Window.Instance.WindowSize;
            if (ws.Width > 0 && ws.Height > 0) { _winW = ws.Width; _winH = ws.Height; }
            Window.Instance.BackgroundColor = BgColor;
            Window.Instance.KeyEvent += OnKeyEvent;

            _loggedOutView = BuildLoggedOutView();
            _qrView = BuildQRView();
            _homeView = BuildHomeView();
            _aboutView = BuildAboutView();

            Window.Instance.GetDefaultLayer().Add(_loggedOutView);
            Window.Instance.GetDefaultLayer().Add(_qrView);
            Window.Instance.GetDefaultLayer().Add(_homeView);
            Window.Instance.GetDefaultLayer().Add(_aboutView);

            // Default state: nothing has come back from tailscaled yet -- show
            // logged-out so the user isn't staring at a blank screen.
            ShowOnly(_loggedOutView);
            FocusManager.Instance.SetCurrentFocusView(_logInBtn);
        }

        private View BuildLoggedOutView()
        {
            var v = new VStack(_winW, _winH, 24);
            v.Put(NewLogo(), 600, 192);
            _loggedOutStatus = v.Put(new TextLabel("Logged out")
            {
                TextColor = TextSecondary,
                PointSize = 28.0f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }, 900, 60);

            _logInBtn = v.Put(MakeButton("Log in", OnLogInClicked), 900, 110);
            _aboutBtn = v.Put(MakeButton("About", (s, e) => ShowAbout(_loggedOutView)), 900, 110);
            _exitBtn = v.Put(MakeButton("Exit", (s, e) => Exit()), 900, 110);

            _logInBtn.DownFocusableView = _aboutBtn;
            _aboutBtn.UpFocusableView = _logInBtn;
            _aboutBtn.DownFocusableView = _exitBtn;
            _exitBtn.UpFocusableView = _aboutBtn;
            v.DoLayout();
            return v;
        }

        private View BuildAboutView()
        {
            var v = new VStack(_winW, _winH, 24);
            v.Put(NewLogo(), 430, 138);

            _aboutVersionLabel = v.Put(new TextLabel
            {
                TextColor = TextSecondary,
                PointSize = 22.0f,
                HorizontalAlignment = HorizontalAlignment.Center,
                MultiLine = true,
            }, 1200, 140);

            v.Put(new TextLabel
            {
                TextColor = TextSecondary,
                PointSize = 22.0f,
                HorizontalAlignment = HorizontalAlignment.Center,
                MultiLine = true,
                Text = "This app advertises the TV as a Tailscale exit node so other devices on your tailnet can route traffic through it. It does not route the TV's own apps through Tailscale; it provides outbound networking to your tailnet, not VPN coverage for the TV.",
            }, 1200, 220);

            _aboutBackBtn = v.Put(MakeButton("Back", (s, e) =>
            {
                if (_aboutBackTarget == _homeView)
                    RunOnUi(() => { ShowOnly(_homeView); FocusManager.Instance.SetCurrentFocusView(_connectBtn); });
                else
                    RunOnUi(() => { ShowOnly(_loggedOutView); FocusManager.Instance.SetCurrentFocusView(_logInBtn); });
            }), 900, 110);
            v.DoLayout();
            return v;
        }

        private void ShowAbout(View backTarget)
        {
            _aboutBackTarget = backTarget;
            RunOnUi(() =>
            {
                _aboutVersionLabel.Text =
                    "App version: 0.1.0\n" +
                    "Tailscale: " + _tailscaledVersion + "\n" +
                    _vpnProbe;
                ShowOnly(_aboutView);
                FocusManager.Instance.SetCurrentFocusView(_aboutBackBtn);
            });
        }

        private View BuildQRView()
        {
            var v = new VStack(_winW, _winH, 24);
            v.Put(NewLogo(), 430, 138);

            _qrStatusLabel = v.Put(new TextLabel("Tailscale is ready. Please log in at:")
            {
                TextColor = TextPrimary,
                PointSize = 26.0f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }, 1200, 60);

            _qrImageView = v.Put(new ImageView(), 520, 520);

            _qrUrlLabel = v.Put(new TextLabel
            {
                TextColor = TextSecondary,
                FontFamily = "monospace",
                PointSize = 18.0f,
                HorizontalAlignment = HorizontalAlignment.Center,
                MultiLine = true,
            }, 1400, 80);
            v.DoLayout();
            return v;
        }

        private View BuildHomeView()
        {
            var v = new VStack(_winW, _winH, 18);
            v.Put(NewLogo(), 400, 128);

            // Connection state as a color-coded label. (On 8.0 this was a
            // rounded dot + label row; 5.0 has no CornerRadius, so we color the
            // text instead -- same signal, no layout gymnastics.)
            _stateLabel = v.Put(new TextLabel("Connecting…")
            {
                TextColor = GrayDot,
                PointSize = 30.0f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }, 1000, 60);

            _hostnameLabel = v.Put(NewRowLabel("", 32.0f, TextPrimary), 1200, 52);
            _ipv4Label = v.Put(NewRowLabel("", 22.0f, TextSecondary), 1200, 40);
            _ipv6Label = v.Put(NewRowLabel("", 22.0f, TextSecondary), 1200, 40);

            _connectBtn = v.Put(MakeButton("Disconnect", OnConnectClicked), 900, 110);
            _exitNodeBtn = v.Put(MakeButton("Advertise as exit node", OnExitNodeClicked), 900, 110);
            _signOutBtn = v.Put(MakeButton("Sign out", OnSignOutClicked), 900, 110);
            var aboutFromHome = v.Put(MakeButton("About", (s, e) => ShowAbout(_homeView)), 900, 110);

            _connectBtn.DownFocusableView = _exitNodeBtn;
            _exitNodeBtn.UpFocusableView = _connectBtn;
            _exitNodeBtn.DownFocusableView = _signOutBtn;
            _signOutBtn.UpFocusableView = _exitNodeBtn;
            _signOutBtn.DownFocusableView = aboutFromHome;
            aboutFromHome.UpFocusableView = _signOutBtn;
            v.DoLayout();
            return v;
        }

        // Center-aligned so text is readable on the hand-laid-out home screen.
        private ImageView NewLogo() => new ImageView(GetResourcePath("tailscale-logo-white.svg"));

        private TextLabel NewRowLabel(string text, float size, Color color) => new TextLabel(text)
        {
            TextColor = color,
            PointSize = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private TvButton MakeButton(string text, EventHandler onClicked)
        {
            var btn = new TvButton(text, CardColor, CardColorFocus, TextPrimary, 26.0f);
            btn.Clicked += onClicked;
            return btn;
        }

        private static string GetResourcePath(string name)
        {
            string dir = Tizen.Applications.Application.Current.DirectoryInfo.SharedResource;
            return Path.Combine(dir, name);
        }

        private void OnKeyEvent(object sender, Window.KeyEventArgs e)
        {
            if (e.Key.State != Key.StateType.Down) return;
            string name = e.Key.KeyPressedName;
            // OK / Enter activates the focused button (no built-in click on a
            // plain View). D-pad navigation is handled by FocusManager via the
            // *FocusableView links we wired above.
            if (name == "Return" || name == "Enter" || name == "KP_Enter" || name == "Select")
            {
                var f = FocusManager.Instance.GetCurrentFocusView();
                if (f is TvButton tb) tb.RaiseClick();
                return;
            }
            if (name == "XF86Back" || name == "Escape") Exit();
        }

        // ---- Backend ----------------------------------------------------------

        // SPIKE build: instead of spawning tailscaled (execve → EPERM on retail
        // 5.0), load the CGO c-shared library IN-PROCESS so tailscale runs via
        // dlopen (not execve) and sidesteps the seccomp block. Success = the Go
        // runtime comes alive and tsnet logs its auth URL, visible at :8081.
        //
        // We dlopen by ABSOLUTE PATH rather than DllImport-by-name: the Tizen
        // .NET launcher doesn't search bin/ for native libs, so a bare
        // DllImport("libtsspike.so") fails with DllNotFound. dlopen also gives
        // us dlerror() for real diagnostics (noexec / Smack / missing dep).
        [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
        private static extern IntPtr dlopen(string filename, int flags);
        [DllImport("libdl.so.2", EntryPoint = "dlsym", CharSet = CharSet.Ansi)]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);
        [DllImport("libdl.so.2", EntryPoint = "dlerror")]
        private static extern IntPtr dlerror();

        private const int RTLD_NOW = 0x002;
        private const int RTLD_GLOBAL = 0x100;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr TsSpikeDelegate(
            [MarshalAs(UnmanagedType.LPStr)] string dir,
            [MarshalAs(UnmanagedType.LPStr)] string logPath);

        private IntPtr TryDlopen(string path)
        {
            if (!File.Exists(path)) { Diag("dlopen: not present at " + path); return IntPtr.Zero; }
            dlerror(); // clear any prior error
            IntPtr h = dlopen(path, RTLD_NOW | RTLD_GLOBAL);
            if (h == IntPtr.Zero)
            {
                IntPtr e = dlerror();
                Diag("dlopen FAILED " + path + " -> " + (Marshal.PtrToStringAnsi(e) ?? "(no dlerror)"));
            }
            else Diag("dlopen OK " + path);
            return h;
        }

        private async Task BackendMain()
        {
            try
            {
                string dataDir = Tizen.Applications.Application.Current.DirectoryInfo.Data;
                string sharedRes = Tizen.Applications.Application.Current.DirectoryInfo.SharedResource;
                string tsdir = Path.Combine(dataDir, "tsnet");
                Directory.CreateDirectory(tsdir);
                string logPath = Path.Combine(dataDir, "spike.log");
                try { File.WriteAllText(logPath, ""); } catch { }

                Diag("SPIKE: loading libtsspike.so via dlopen …");
                RunOnUi(() => _loggedOutStatus.Text = "Spike: loading in-process engine… (see :8081)");

                // Try the RO image first; fall back to a chmod'd copy in the
                // writable data dir (in case shared/res isn't exec-mmap'able).
                string roPath = Path.Combine(sharedRes, "libtsspike.so");
                IntPtr h = TryDlopen(roPath);
                if (h == IntPtr.Zero)
                {
                    try
                    {
                        string dst = Path.Combine(dataDir, "libtsspike.so");
                        File.Copy(roPath, dst, true);
                        chmod(dst, 0x1ED); // 0755
                        Diag("SPIKE: copied .so to data dir, retrying dlopen");
                        h = TryDlopen(dst);
                    }
                    catch (Exception ex) { Diag("SPIKE: data-dir copy fallback: " + ex.Message); }
                }

                if (h == IntPtr.Zero)
                {
                    RunOnUi(() => _loggedOutStatus.Text = "Spike FAILED: dlopen (see :8081)");
                    return;
                }

                IntPtr sym = dlsym(h, "TsSpike");
                if (sym == IntPtr.Zero)
                {
                    Diag("SPIKE: dlsym(TsSpike) failed: " + (Marshal.PtrToStringAnsi(dlerror()) ?? "?"));
                    RunOnUi(() => _loggedOutStatus.Text = "Spike FAILED: dlsym (see :8081)");
                    return;
                }

                try
                {
                    var fn = Marshal.GetDelegateForFunctionPointer<TsSpikeDelegate>(sym);
                    IntPtr r = fn(tsdir, logPath);
                    Diag("SPIKE: TsSpike returned: " + (Marshal.PtrToStringAnsi(r) ?? "(null)"));
                    RunOnUi(() => _loggedOutStatus.Text = "Spike: engine loaded — see :8081");
                }
                catch (Exception ex)
                {
                    Diag("SPIKE: TsSpike invoke FAILED: " + ex);
                    RunOnUi(() => _loggedOutStatus.Text = "Spike FAILED (invoke): " + ex.Message);
                    return;
                }

                // Tail spike.log (written by the Go side) into the diag so the
                // auth URL / errors show up at :8081.
                long pos = 0;
                while (true)
                {
                    await Task.Delay(1500);
                    try
                    {
                        if (!File.Exists(logPath)) continue;
                        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        fs.Seek(pos, SeekOrigin.Begin);
                        using var sr = new StreamReader(fs);
                        string chunk = sr.ReadToEnd();
                        pos = fs.Position;
                        foreach (var line in chunk.Split('\n'))
                            if (line.Trim().Length > 0) Diag("tsnet> " + line.Trim());
                    }
                    catch (Exception ex) { Diag("SPIKE tail: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                Diag("BackendMain(spike) error: " + ex);
                RunOnUi(() => _loggedOutStatus.Text = "Error: " + ex.Message);
            }
        }

        // ---- vpnservice probe (Partner-privilege validation) -----------------
        // capi-vpnsvc is the native Tizen VPN client. Its APIs need
        // http://tizen.org/privilege/vpnservice, a partner-level privilege — so vpnsvc_init
        // returning 0 (VPNSVC_ERROR_NONE) proves the running package actually unlocked the
        // privilege. Shown on the About screen as the acceptance signal.
        [DllImport("libcapi-vpnsvc.so.0", EntryPoint = "vpnsvc_init")]
        private static extern int vpnsvc_init(string name, out IntPtr handle);

        [DllImport("libcapi-vpnsvc.so.0", EntryPoint = "vpnsvc_deinit")]
        private static extern int vpnsvc_deinit(IntPtr handle);

        private void ProbeVpnService()
        {
            try
            {
                int rc = vpnsvc_init("tailscale0", out IntPtr handle);
                _vpnProbe = rc == 0
                    ? "vpnsvc_init: VPNSVC_ERROR_NONE (0) — privilege granted"
                    : "vpnsvc_init: error " + rc + " — privilege NOT granted";
                if (rc == 0 && handle != IntPtr.Zero)
                    vpnsvc_deinit(handle);
            }
            catch (Exception ex)
            {
                _vpnProbe = "vpnsvc probe failed to load: " + ex.Message;
            }
            Diag("VPNSVC " + _vpnProbe);
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, uint mode);

        private void StageBinaries()
        {
            string dataDir = Tizen.Applications.Application.Current.DirectoryInfo.Data;
            _stateDir = Path.Combine(dataDir, "state");
            Directory.CreateDirectory(_stateDir);
            _socket = Path.Combine(dataDir, "tailscaled.sock");
            _tailscaledExe = Path.Combine(dataDir, "tailscaled");
            // tailscaled is shipped in shared/res (the classic Tizen.NET.Sdk
            // reliably packages that folder; it does NOT package a lib/ Content
            // item pointing outside the project dir). Copy it out to the
            // writable data dir and mark it executable before launching.
            string srcTailscaled = Path.Combine(
                Tizen.Applications.Application.Current.DirectoryInfo.SharedResource, "tailscaled");
            File.Copy(srcTailscaled, _tailscaledExe, overwrite: true);
            chmod(_tailscaledExe, 0x1ED); // 0755
            Diag("staged tailscaled from " + srcTailscaled + " to " + _tailscaledExe);
        }

        private void StartTailscaled()
        {
            var psi = new ProcessStartInfo
            {
                FileName = _tailscaledExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // ProcessStartInfo.ArgumentList (.NET Core 2.1+) is not in the
            // tizen50 compile surface, so build a quoted Arguments string. The
            // Tizen data dir has no spaces, but quote the paths defensively.
            psi.Arguments =
                "--tun=userspace-networking " +
                "--statedir=\"" + _stateDir + "\" " +
                "--socket=\"" + _socket + "\" " +
                "--state=\"" + Path.Combine(_stateDir, "tailscaled.state") + "\" " +
                "--verbose=1";
            _tailscaledProc = Process.Start(psi);
            _tailscaledProc.OutputDataReceived += (s, e) => { if (e.Data != null) Diag("tailscaled> " + e.Data); };
            _tailscaledProc.ErrorDataReceived += (s, e) => { if (e.Data != null) Diag("tailscaled> " + e.Data); };
            _tailscaledProc.BeginOutputReadLine();
            _tailscaledProc.BeginErrorReadLine();
            Diag("tailscaled started pid=" + _tailscaledProc.Id);
        }

        private Task WatchBus()
        {
            Diag("opening watch-ipn-bus");

            return _api.WatchIPNBus(7, notify =>
            {
                string browseUrl = notify["BrowseToURL"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(browseUrl) && browseUrl != _authUrl)
                {
                    _authUrl = browseUrl;
                    Diag("BrowseToURL = " + browseUrl);
                    if (_userWantsQR) ShowQR(browseUrl);
                }

                if (notify["Prefs"] is JsonNode prefsNode) ApplyPrefs(prefsNode);

                if (notify["State"] is JsonNode stateNode)
                {
                    int s = stateNode.GetValue<int>();
                    string name = StateName(s);
                    Diag("ipn State=" + name + " (" + s + ")");
                    _state = name;
                    HandleState(name);
                }
            });
        }

        private void ApplyPrefs(JsonNode prefs)
        {
            try
            {
                _wantRunning = prefs["WantRunning"]?.GetValue<bool>() ?? false;
                bool exit = false;
                if (prefs["AdvertiseRoutes"] is JsonArray routes)
                {
                    foreach (var r in routes)
                    {
                        string s = r?.GetValue<string>();
                        if (s == "0.0.0.0/0" || s == "::/0") { exit = true; break; }
                    }
                }
                _exitNodeOn = exit;
                Diag($"prefs WantRunning={_wantRunning} exitNodeOn={_exitNodeOn}");
                RunOnUi(UpdateButtonsForPrefs);
            }
            catch (Exception ex) { Diag("ApplyPrefs: " + ex.Message); }
        }

        private async void HandleState(string name)
        {
            switch (name)
            {
                case "NoState":
                case "NeedsLogin":
                    _userWantsQR = false; // sign-out / fresh start clears the QR intent
                    _authUrl = null;
                    RunOnUi(() =>
                    {
                        _loggedOutStatus.Text = "Logged out";
                        ShowOnly(_loggedOutView);
                        FocusManager.Instance.SetCurrentFocusView(_logInBtn);
                    });
                    break;

                case "Starting":
                case "Stopped":
                case "Running":
                    _userWantsQR = false;
                    RunOnUi(() => ShowOnly(_homeView));
                    UpdateStatusDot(name);
                    if (name == "Running" || name == "Stopped") await RefreshIdentity();
                    RunOnUi(() => FocusManager.Instance.SetCurrentFocusView(_connectBtn));
                    break;
            }
        }

        private void UpdateStatusDot(string name)
        {
            RunOnUi(() =>
            {
                _stateLabel.TextColor = name == "Running" ? GreenDot : GrayDot;
                _stateLabel.Text = name;
                _connectBtn.Text = name == "Running" ? "Disconnect" : "Connect";
            });
        }

        private async Task RefreshIdentity()
        {
            try
            {
                JsonNode status = await _api.Status();
                string ver = status["Version"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(ver)) _tailscaledVersion = ver;
                JsonNode self = status["Self"];
                string host = self?["HostName"]?.GetValue<string>() ?? "";
                string dns = self?["DNSName"]?.GetValue<string>() ?? "";
                string v4 = "", v6 = "";
                if (self?["TailscaleIPs"] is JsonArray ips)
                {
                    foreach (var ip in ips)
                    {
                        string s = ip?.GetValue<string>();
                        if (string.IsNullOrEmpty(s)) continue;
                        if (s.Contains(":")) v6 = s; else v4 = s;
                    }
                }
                string trimmed = !string.IsNullOrEmpty(dns) ? dns.TrimEnd('.') : host;
                RunOnUi(() =>
                {
                    _hostnameLabel.Text = trimmed;
                    _ipv4Label.Text = string.IsNullOrEmpty(v4) ? "" : v4;
                    _ipv6Label.Text = string.IsNullOrEmpty(v6) ? "" : v6;
                });
            }
            catch (Exception ex) { Diag("RefreshIdentity: " + ex.Message); }
        }

        private void UpdateButtonsForPrefs()
        {
            _connectBtn.Text = _wantRunning ? "Disconnect" : "Connect";
            _exitNodeBtn.Text = _exitNodeOn ? "Stop advertising as exit node" : "Advertise as exit node";
        }

        // ---- Button handlers ---------------------------------------------------

        private async void OnLogInClicked(object sender, EventArgs e)
        {
            try
            {
                Diag("log in button");
                _userWantsQR = true;
                RunOnUi(() => _loggedOutStatus.Text = "Requesting login URL…");
                var prefs = new JsonObject
                {
                    ["WantRunning"] = true,
                    ["WantRunningSet"] = true,
                    ["Hostname"] = "tizen-tv",
                    ["HostnameSet"] = true,
                    ["ControlURL"] = "https://controlplane.tailscale.com",
                    ["ControlURLSet"] = true,
                };
                await _api.EditPrefs(prefs);
                await _api.StartLoginInteractive();
            }
            catch (Exception ex)
            {
                Diag("OnLogInClicked: " + ex);
                RunOnUi(() => _loggedOutStatus.Text = "Error: " + ex.Message);
            }
        }

        private async void OnConnectClicked(object sender, EventArgs e)
        {
            try
            {
                bool desired = !_wantRunning;
                Diag("connect button: WantRunning=" + desired);
                var prefs = new JsonObject
                {
                    ["WantRunning"] = desired,
                    ["WantRunningSet"] = true,
                };
                await _api.EditPrefs(prefs);
            }
            catch (Exception ex) { Diag("OnConnectClicked: " + ex); }
        }

        private async void OnExitNodeClicked(object sender, EventArgs e)
        {
            try
            {
                bool desired = !_exitNodeOn;
                Diag("exit node button: advertising=" + desired);
                JsonArray routes = desired
                    ? new JsonArray("0.0.0.0/0", "::/0")
                    : new JsonArray();
                var prefs = new JsonObject
                {
                    ["AdvertiseRoutes"] = routes,
                    ["AdvertiseRoutesSet"] = true,
                };
                await _api.EditPrefs(prefs);
            }
            catch (Exception ex) { Diag("OnExitNodeClicked: " + ex); }
        }

        private async void OnSignOutClicked(object sender, EventArgs e)
        {
            try
            {
                Diag("sign out button");
                await _api.Logout();
            }
            catch (Exception ex) { Diag("OnSignOutClicked: " + ex); }
        }

        // ---- View transitions --------------------------------------------------

        private void ShowOnly(View v)
        {
            _loggedOutView.Hide();
            _qrView.Hide();
            _homeView.Hide();
            _aboutView.Hide();
            v.Show();
        }

        private void ShowQR(string url)
        {
            try
            {
                byte[] qrPng = MakeQR(url);
                string qrPath = Path.Combine(_stateDir, "auth-qr.png");
                File.WriteAllBytes(qrPath, qrPng);
                Diag($"qr generated {qrPng.Length}B at {qrPath}");
                RunOnUi(() =>
                {
                    _qrImageView.SetImage(qrPath);
                    _qrUrlLabel.Text = url;
                    ShowOnly(_qrView);
                });
            }
            catch (Exception ex) { Diag("ShowQR: " + ex); }
        }

        private static byte[] MakeQR(string text)
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            using var qr = new PngByteQRCode(data);
            return qr.GetGraphic(20, new byte[] { 0x14, 0x14, 0x14 }, new byte[] { 0xFF, 0xFF, 0xFF }, true);
        }

        private static string StateName(int s)
        {
            switch (s)
            {
                case 0: return "NoState";
                case 1: return "InUseOtherUser";
                case 2: return "NeedsLogin";
                case 3: return "NeedsMachineAuth";
                case 4: return "Stopped";
                case 5: return "Starting";
                case 6: return "Running";
                default: return s.ToString();
            }
        }

        private void RunOnUi(Action a)
        {
            if (_uiCtx == null) { try { a(); } catch (Exception ex) { Diag("ui (no ctx): " + ex); } return; }
            _uiCtx.Post(_ =>
            {
                try { a(); } catch (Exception ex) { Diag("ui post: " + ex); }
            }, null);
        }

        static void Main(string[] args)
        {
            var app = new Program();
            app.Run(args);
        }
    }
}
