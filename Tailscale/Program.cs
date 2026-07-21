using System;
using System.Diagnostics;
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
using Tizen.NUI.Components;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace Tailscale
{
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
        private Button _logInBtn;
        private Button _aboutBtn;
        private Button _exitBtn;

        // About widgets.
        private TextLabel _aboutVersionLabel;
        private Button _aboutBackBtn;
        private string _tailscaledVersion = "(unknown)";
        private string _vpnProbe = "(vpnsvc not probed)";

        // QR widgets.
        private TextLabel _qrStatusLabel;
        private TextLabel _qrUrlLabel;
        private ImageView _qrImageView;

        // Home view widgets.
        private View _statusDot;
        private TextLabel _stateLabel;
        private TextLabel _hostnameLabel;
        private TextLabel _ipv4Label;
        private TextLabel _ipv6Label;
        private Button _connectBtn;
        private Button _exitNodeBtn;
        private Button _signOutBtn;

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

        private readonly StringBuilder _diag = new();

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
            var v = NewVerticalStack();
            v.Add(NewLogo(720, 230));
            _loggedOutStatus = new TextLabel("Logged out")
            {
                TextColor = TextSecondary,
                PointSize = 28.0f,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            v.Add(_loggedOutStatus);

            _logInBtn = MakeButton("Log in", OnLogInClicked);
            _aboutBtn = MakeButton("About", (s, e) => ShowAbout(_loggedOutView));
            _exitBtn = MakeButton("Exit", (s, e) => Exit());
            v.Add(_logInBtn);
            v.Add(_aboutBtn);
            v.Add(_exitBtn);

            _logInBtn.DownFocusableView = _aboutBtn;
            _aboutBtn.UpFocusableView = _logInBtn;
            _aboutBtn.DownFocusableView = _exitBtn;
            _exitBtn.UpFocusableView = _aboutBtn;
            return v;
        }

        private View BuildAboutView()
        {
            var v = NewVerticalStack();
            v.Add(NewLogo(520, 166));

            var card = MakeCard();
            _aboutVersionLabel = new TextLabel
            {
                TextColor = TextSecondary,
                PointSize = 22.0f,
                HorizontalAlignment = HorizontalAlignment.Begin,
                MultiLine = true,
            };
            card.Add(_aboutVersionLabel);

            var blurb = new TextLabel
            {
                TextColor = TextSecondary,
                PointSize = 22.0f,
                HorizontalAlignment = HorizontalAlignment.Begin,
                MultiLine = true,
                Text = "This app advertises the TV as a Tailscale exit node so other devices on your tailnet can route traffic through it. It does not route the TV's own apps through Tailscale; it provides outbound networking to your tailnet, not VPN coverage for the TV.",
            };
            card.Add(blurb);

            v.Add(card);

            _aboutBackBtn = MakeButton("Back", (s, e) =>
            {
                if (_aboutBackTarget == _homeView)
                    RunOnUi(() => { ShowOnly(_homeView); FocusManager.Instance.SetCurrentFocusView(_connectBtn); });
                else
                    RunOnUi(() => { ShowOnly(_loggedOutView); FocusManager.Instance.SetCurrentFocusView(_logInBtn); });
            });
            v.Add(_aboutBackBtn);
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
            var v = NewVerticalStack();
            v.Add(NewLogo(520, 166));

            _qrStatusLabel = new TextLabel("Tailscale is ready. Please log in at:")
            {
                TextColor = TextPrimary,
                PointSize = 26.0f,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            v.Add(_qrStatusLabel);

            _qrImageView = new ImageView
            {
                WidthSpecification = 520,
                HeightSpecification = 520,
            };
            v.Add(_qrImageView);

            _qrUrlLabel = new TextLabel
            {
                TextColor = TextSecondary,
                FontFamily = "monospace",
                PointSize = 18.0f,
                HorizontalAlignment = HorizontalAlignment.Center,
                MultiLine = true,
            };
            v.Add(_qrUrlLabel);
            return v;
        }

        private View BuildHomeView()
        {
            var v = NewVerticalStack();
            ((LinearLayout)v.Layout).VerticalAlignment = VerticalAlignment.Top;
            ((LinearLayout)v.Layout).Padding = new Extents(0, 0, 80, 80);

            v.Add(NewLogo(440, 140));

            // Status dot + state name on the same row.
            var statusRow = new View
            {
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    CellPadding = new Size2D(20, 0),
                },
            };
            _statusDot = new View
            {
                WidthSpecification = 26,
                HeightSpecification = 26,
                BackgroundColor = GrayDot,
                CornerRadius = new Vector4(13, 13, 13, 13),
            };
            statusRow.Add(_statusDot);
            _stateLabel = new TextLabel("Connecting…")
            {
                TextColor = TextPrimary,
                PointSize = 30.0f,
            };
            statusRow.Add(_stateLabel);
            v.Add(statusRow);

            var idCard = MakeCard();
            _hostnameLabel = NewRowLabel("", 32.0f, TextPrimary);
            _ipv4Label = NewRowLabel("", 22.0f, TextSecondary);
            _ipv6Label = NewRowLabel("", 22.0f, TextSecondary);
            idCard.Add(_hostnameLabel);
            idCard.Add(_ipv4Label);
            idCard.Add(_ipv6Label);
            v.Add(idCard);

            _connectBtn = MakeButton("Disconnect", OnConnectClicked);
            v.Add(_connectBtn);

            _exitNodeBtn = MakeButton("Advertise as exit node", OnExitNodeClicked);
            v.Add(_exitNodeBtn);

            _signOutBtn = MakeButton("Sign out", OnSignOutClicked);
            v.Add(_signOutBtn);

            var aboutFromHome = MakeButton("About", (s, e) => ShowAbout(_homeView));
            v.Add(aboutFromHome);

            _connectBtn.DownFocusableView = _exitNodeBtn;
            _exitNodeBtn.UpFocusableView = _connectBtn;
            _exitNodeBtn.DownFocusableView = _signOutBtn;
            _signOutBtn.UpFocusableView = _exitNodeBtn;
            _signOutBtn.DownFocusableView = aboutFromHome;
            aboutFromHome.UpFocusableView = _signOutBtn;
            return v;
        }

        private View NewVerticalStack() => new View
        {
            WidthResizePolicy = ResizePolicyType.FillToParent,
            HeightResizePolicy = ResizePolicyType.FillToParent,
            Layout = new LinearLayout
            {
                LinearOrientation = LinearLayout.Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CellPadding = new Size2D(0, 24),
            },
        };

        private ImageView NewLogo(int w, int h) => new ImageView(GetResourcePath("tailscale-logo-white.svg"))
        {
            WidthSpecification = w,
            HeightSpecification = h,
        };

        private View MakeCard()
        {
            return new View
            {
                BackgroundColor = CardColor,
                CornerRadius = new Vector4(24, 24, 24, 24),
                Padding = new Extents(48, 48, 36, 36),
                WidthSpecification = 1100,
                Layout = new LinearLayout
                {
                    LinearOrientation = LinearLayout.Orientation.Vertical,
                    CellPadding = new Size2D(0, 14),
                },
            };
        }

        private TextLabel NewRowLabel(string text, float size, Color color) => new TextLabel(text)
        {
            TextColor = color,
            PointSize = size,
            HorizontalAlignment = HorizontalAlignment.Begin,
        };

        private Button MakeButton(string text, EventHandler<ClickedEventArgs> onClicked)
        {
            var btn = new Button
            {
                Text = text,
                Size = new Size(1100, 120),
                CornerRadius = 22,
                BackgroundColor = CardColor,
                Focusable = true,
            };
            btn.TextLabel.TextColor = TextPrimary;
            btn.TextLabel.PointSize = 26.0f;
            btn.FocusGained += (s, e) => { btn.BackgroundColor = CardColorFocus; };
            btn.FocusLost += (s, e) => { btn.BackgroundColor = CardColor; };
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
            if (name == "XF86Back" || name == "Escape") Exit();
        }

        // ---- Backend ----------------------------------------------------------

        private async Task BackendMain()
        {
            try
            {
                StageBinaries();
                StartTailscaled();
                await Task.Delay(750);
                _api = new LocalApi(_socket);
                // Always Start the engine so we'll receive accurate state
                // updates (Stopped vs NeedsLogin vs Running). We do NOT call
                // StartLoginInteractive here -- the user has to ask for it via
                // the "Log in" button.
                try { await _api.Start(); } catch (Exception ex) { Diag("Start: " + ex.Message); }
                await WatchBus();
            }
            catch (Exception ex)
            {
                Diag("BackendMain error: " + ex);
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
            string installDir = Tizen.Applications.Application.Current.DirectoryInfo.Resource;
            string dataDir = Tizen.Applications.Application.Current.DirectoryInfo.Data;
            _stateDir = Path.Combine(dataDir, "state");
            Directory.CreateDirectory(_stateDir);
            _socket = Path.Combine(dataDir, "tailscaled.sock");
            _tailscaledExe = Path.Combine(dataDir, "tailscaled");
            File.Copy(Path.GetFullPath(Path.Combine(installDir, "..", "lib", "tailscaled")), _tailscaledExe, overwrite: true);
            chmod(_tailscaledExe, 0x1ED); // 0755
            Diag("staged tailscaled to " + _tailscaledExe);
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
            psi.ArgumentList.Add("--tun=userspace-networking");
            psi.ArgumentList.Add("--statedir=" + _stateDir);
            psi.ArgumentList.Add("--socket=" + _socket);
            psi.ArgumentList.Add("--state=" + Path.Combine(_stateDir, "tailscaled.state"));
            psi.ArgumentList.Add("--verbose=1");
            _tailscaledProc = Process.Start(psi);
            _tailscaledProc.OutputDataReceived += (s, e) => { if (e.Data != null) Diag("tailscaled> " + e.Data); };
            _tailscaledProc.ErrorDataReceived += (s, e) => { if (e.Data != null) Diag("tailscaled> " + e.Data); };
            _tailscaledProc.BeginOutputReadLine();
            _tailscaledProc.BeginErrorReadLine();
            Diag("tailscaled started pid=" + _tailscaledProc.Id);
        }

        private async Task WatchBus()
        {
            Diag("opening watch-ipn-bus");

            await foreach (var notify in _api.WatchIPNBus(mask: 7))
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
            }
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
                _statusDot.BackgroundColor = name == "Running" ? GreenDot : GrayDot;
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

        private async void OnLogInClicked(object sender, ClickedEventArgs e)
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

        private async void OnConnectClicked(object sender, ClickedEventArgs e)
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

        private async void OnExitNodeClicked(object sender, ClickedEventArgs e)
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

        private async void OnSignOutClicked(object sender, ClickedEventArgs e)
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

        private static string StateName(int s) => s switch
        {
            0 => "NoState",
            1 => "InUseOtherUser",
            2 => "NeedsLogin",
            3 => "NeedsMachineAuth",
            4 => "Stopped",
            5 => "Starting",
            6 => "Running",
            _ => s.ToString(),
        };

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
