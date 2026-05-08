# Tailscale for Samsung Tizen TV (Experiment)

[![status: experimental](https://img.shields.io/badge/status-experimental-blue)](https://tailscale.com/kb/1167/release-stages/#experimental)

A Tailscale exit-node app for Samsung Smart TVs running Tizen.

The TV joins your tailnet and (optionally) advertises itself as an
exit node, so other devices on the tailnet can route traffic through
it. The TV's own apps continue to use the local network -- this app
does **not** provide VPN coverage for the TV itself.

## Tracking Issue

See https://github.com/tailscale/tailscale/issues/10955

## How it works

```
┌────────────────── Tizen TV ──────────────────┐
│                                              │
│  .NET (NUI) UI ──── localapi/v0 ────┐        │
│  com.tailscale.tailscale            │        │
│                                  Unix sock   │
│                                     │        │
│  tailscaled (Go, --tun=userspace)───┘        │
│      │                                       │
│      └── magicsock / DERP / WireGuard        │
│                                              │
└──────────────────────────────────────────────┘
```

The .NET app launches a cross-compiled `tailscaled` binary as a
subprocess, then drives the IPN state machine over the Unix-socket
LocalAPI. tailscaled runs in `--tun=userspace-networking` mode and
uses gVisor netstack to terminate/redial peer traffic. No TUN device
is required.

## Build prerequisites

- **Tizen Studio 5.x or newer** with the TV CLI tools.
  See [Installing TV SDK](https://developer.samsung.com/smarttv/develop/getting-started/setting-up-sdk/installing-tv-sdk.html).
  We use `tools/tizen-core/tz` (the newer Go-based CLI) and `tools/sdb`.
- **.NET 8 SDK** with the **Tizen workload**.
  Install the SDK from your distro or
  [microsoft.com](https://dotnet.microsoft.com/), then the workload:
  ```sh
  curl -sSL https://raw.githubusercontent.com/Samsung/Tizen.NET/main/workload/scripts/workload-install.sh | bash
  ```
  (See the [Tizen.NET wiki](https://github.com/Samsung/Tizen.NET/wiki/Installing-Tizen-.NET-Workload) for details and platform-specific notes.)
- **Go** (any recent version). The build cross-compiles `tailscaled`
  from the public source at the version pinned in `go.mod`; you do
  not need a separate tailscale.com checkout.
- **(Linux only) gnome-keyring + dbus-run-session**, used by
  `make pack` because Tizen's `tz` CLI stashes PKCS#12 passwords via
  libsecret. On macOS the system Keychain handles this; nothing extra
  to install.
  ```sh
  sudo apt-get install gnome-keyring libsecret-tools
  ```

## The Samsung certificate dance

Samsung TVs reject unsigned, generically-signed, or `tizen-public-distributor`-signed
apps. You need a **Samsung-issued distributor certificate bound to the TV's
DUID** before any installable tpk will load. This is a one-time setup per
TV.

1. Enable Developer Mode on the TV.
   See [Samsung's instructions](https://developer.samsung.com/smarttv/develop/getting-started/using-sdk/tv-device.html#Connecting-the-TV-and-SDK).
2. Connect to the TV with sdb so you can read its DUID:
   ```sh
   ~/tizen-studio/tools/sdb connect <TV_IP>
   ~/tizen-studio/tools/sdb shell 0 getduid
   ```
   Save that DUID -- it goes into the cert.
3. Open Tizen Studio's **Certificate Manager** (or the VS Code Tizen
   Extension, "Create Certificate" pane). Pick **Samsung** (not
   Tizen), profile name e.g. `tailscale-tv`, privilege level
   **Public**, and paste the DUID when prompted. You'll be asked to
   sign in with a Samsung account.
4. The output lands at `~/SamsungCertificate/<profile_name>/`:
   ```
   author.p12
   distributor.p12
   author.crt   distributor.crt
   author.csr   distributor.csr
   author.pri   distributor.pri
   device-profile.xml
   ```
5. Copy the two `.p12` files somewhere stable and remember the
   password(s) you set during creation. The Makefile defaults assume:
   ```
   ~/tizen-studio-data/keystore/tv-samsung/author.p12
   ~/tizen-studio-data/keystore/tv-samsung/distributor.p12
   ```

## Building and installing

Configure the build with environment variables (or a gitignored
`.env.local` file at the repo root):

```sh
cat > .env.local <<EOF
TS_AUTHOR_PW=<your author p12 password>
TS_DIST_PW=<your distributor p12 password>
EOF
```

Other variables you can override (defaults in [Makefile](./Makefile)):

- `TIZEN_STUDIO` -- path to Tizen Studio (default `~/tizen-studio`).
- `DOTNET` -- path to `dotnet` with the Tizen workload installed.
- `SIGN_PROFILE` -- security profile name to (re-)create at pack time.
- `DEVICE` -- sdb serial of the target TV (`sdb devices`). Default `emulator-26101`.
- `TS_AUTHOR_P12` / `TS_DIST_P12` -- paths to your Samsung-issued certs.

Then:

```sh
make check-deps   # verify Tizen Studio, .NET workload, certs, sdb device, etc.
make tailscaled   # cross-compiles tailscaled for ARMv7 into Tailscale/lib/
make build        # dotnet build → unsigned tpk
make pack         # signs with the Samsung profile → Tailscale.signed.tpk
make install      # sdb-installs onto $DEVICE
make run          # launches the app
```

`make install` chains the previous steps, so `make install run` from
a clean checkout does everything end-to-end. Run `make check-deps`
first if you're not sure whether your environment is set up.

### Connecting to the TV

```sh
~/tizen-studio/tools/sdb connect <TV_IP>
~/tizen-studio/tools/sdb devices
# emulator-26101  device  UN65xxxxxxxx
```

> sdb prints "failed to connect" even on a successful connection; ignore it
> and check `sdb devices` instead.

If the TV is on a separate network and only reachable through some
tunnel, you'll need to terminate the tunnel locally and `sdb connect`
to a local port.

### Using the app

Once installed, launch from the TV's **Apps** screen.

1. **Logged out** view: pick **Log in**.
2. The app asks tailscaled for a login URL, generates a QR code, and
   shows it. Scan with your phone (or open the URL on a laptop) to
   authenticate against your tailnet.
3. **Connected** view: hostname, IPv4/IPv6, plus buttons for
   Disconnect, Advertise as exit node, Sign out, About.

Once "Advertise as exit node" is on, peers on your tailnet can pick
the TV under their **Exit nodes** menu and route their traffic
through it.

## Privileges

The app requests:

- `http://tizen.org/privilege/internet` -- to dial DERP and any
  exit-node-routed destinations.
- `http://tizen.org/privilege/network.get` -- to query interface info.

No raw sockets, no TUN, no root.

## Repo layout

```
Tailscale/                .NET (NUI) Tizen TV app
├── Tailscale.csproj      net6.0-tizen8.0
├── tizen-manifest.xml    package id, privileges, app type
├── Program.cs            UI, tailscaled subprocess, view transitions
├── LocalApi.cs           HTTP-over-AF_UNIX client for tailscaled
├── lib/                  cross-compiled tailscaled lands here at build (gitignored)
└── shared/res/           logo + icon assets
Makefile
NOTES.md
```

## Limitations

- The app advertises the TV as an exit node but does **not** route
  the TV's apps' traffic through Tailscale. (Tizen's app sandbox
  doesn't grant the privileges that would require.)
- The app icon and login screen are minimal -- this is a starter
  version.
- Tested on a 32" Samsung Full HD F6000F TV running Tizen 8.0 (ARMv7).
  Other Samsung TV models in dev mode should work but are unverified.
