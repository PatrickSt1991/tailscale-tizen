A Tailscale exit-node app for Samsung Tizen TVs.

## Layout

`Tailscale/` is the Tizen .NET (NUI) app. `tailscaled` (Go, ARMv7
static) ships in `lib/` and runs as a subprocess. The .NET side
speaks LocalAPI over a Unix socket and renders the UI.

## Building

The TV runs Tizen 8.0 / ARMv7 (`platform_version:8.0`,
`cpu_arch:armv7` per `sdb capability`).

Cross-compile `tailscaled` at the version pinned in `go.mod`:

    CGO_ENABLED=0 GOOS=linux GOARCH=arm GOARM=7 \
      go build -trimpath -ldflags="-s -w" \
      -o Tailscale/lib/tailscaled tailscale.com/cmd/tailscaled

Build the .NET app (requires the Samsung Tizen .NET workload -- see
https://github.com/Samsung/Tizen.NET/wiki/Installing-Tizen-.NET-Workload):

    cd Tailscale && dotnet build

Sign and install with the Samsung-issued, DUID-bound certs (Samsung
TVs reject the SDK's public distributor cert):

    tz pack -b Tailscale/bin/Debug/net6.0-tizen8.0/com.tailscale.tailscale-0.1.0.tpk \
            -t tpk -s tailscale-tv -o Tailscale.signed.tpk
    tz install -p Tailscale.signed.tpk -e <serial>
    tz run -p com.tailscale.tailscale -e <serial>
