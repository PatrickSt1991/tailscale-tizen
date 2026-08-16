SHELL := /bin/bash
UNAME := $(shell uname)

# Optional .env.local for credentials (TS_AUTHOR_PW, TS_DIST_PW). Gitignored.
-include .env.local

# Tizen Studio CLI entrypoints.
TIZEN_STUDIO ?= $(HOME)/tizen-studio
TZ           := $(TIZEN_STUDIO)/tools/tizen-core/tz
SDB          := $(TIZEN_STUDIO)/tools/sdb

# .NET 8 SDK with the Tizen workload installed (see NOTES.md).
DOTNET ?= $(HOME)/.dotnet-local/dotnet
export DOTNET_ROOT := $(dir $(DOTNET))

# Signing profile and the target device's sdb serial (`sdb devices`).
SIGN_PROFILE   ?= tailscale-tv
DEVICE         ?= emulator-26101
TS_AUTHOR_P12  ?= $(HOME)/tizen-studio-data/keystore/tv-samsung/author.p12
TS_DIST_P12    ?= $(HOME)/tizen-studio-data/keystore/tv-samsung/distributor.p12
# TS_AUTHOR_PW and TS_DIST_PW are required for `pack`; supply via environment
# or .env.local. They are exported into a fresh dbus session below -- we don't
# rely on a persistent keyring, since headless boxes don't have one.

APP_ID    := com.tailscale.tailscale
APP_VER   := 0.1.0

# Which project/TFM to build. Defaults reproduce the original Tizen 8 build
# exactly. For the Tizen 5.0 TV (Samsung RU7020, armv7):
#   make install PROJECT=Tailscale5 TFM=tizen50 GOARCH=arm DEVICE=192.168.2.23:26101
PROJECT ?= Tailscale
TFM     ?= net6.0-tizen8.0
# Recursively-expanded (=) and find-based so it resolves after `build` has run,
# and copes with the classic tizen50 build landing its .tpk under a differently
# named output dir (bin/Debug/tizen50 vs bin/Debug/netcoreapp2.0) than the
# net6.0-tizen8.0 build.
TPK_BUILT = $(shell find $(PROJECT)/bin -name '$(APP_ID)-$(APP_VER).tpk' 2>/dev/null | head -n 1)
TPK_OUT   := Tailscale.signed.tpk

# Target CPU for the tailscaled binary. Default arm64 (aarch64) = Tizen 8 TVs.
# Overrides: `make build GOARCH=arm` (32-bit ARMv7 TVs, incl. the Tizen 5.0
# RU7020), `GOARCH=amd64` (x86_64 TV emulator), `GOARCH=386` (32-bit x86 TV
# emulator).
GOARCH ?= arm64
GOARM  ?= 7
ifeq ($(GOARCH),arm)
GO_ENV := CGO_ENABLED=0 GOOS=linux GOARCH=arm GOARM=$(GOARM)
else
GO_ENV := CGO_ENABLED=0 GOOS=linux GOARCH=$(GOARCH)
endif

.PHONY: all tailscaled build pack install run uninstall clean help check-deps

all: pack

# check-deps walks a checklist of every dependency that `make install` needs
# and prints OK/MISSING for each. Exits non-zero if anything's missing.
check-deps:
	@ok=1; \
	check() { name="$$1"; shift; \
	  if "$$@" >/dev/null 2>&1; then printf "  [OK]      %s\n" "$$name"; \
	  else                            printf "  [MISSING] %s\n" "$$name"; ok=0; fi; }; \
	echo "Tools:"; \
	check "go (host toolchain)"            command -v go; \
	check "openssl"                        command -v openssl; \
	check "Tizen Studio at $(TIZEN_STUDIO)"  test -d "$(TIZEN_STUDIO)"; \
	check "tz CLI ($(TZ))"                 test -x "$(TZ)"; \
	check "sdb CLI ($(SDB))"               test -x "$(SDB)"; \
	check "dotnet ($(DOTNET))"             test -x "$(DOTNET)"; \
	if test -x "$(DOTNET)"; then \
	  check "dotnet Tizen workload"        bash -c '$(DOTNET) workload list 2>/dev/null | grep -q "^tizen "'; \
	fi; \
	if [ "$(UNAME)" = "Linux" ]; then \
	  echo; echo "Linux-only tools (cert password is stored via libsecret):"; \
	  check "dbus-run-session"             command -v dbus-run-session; \
	  check "gnome-keyring-daemon"         command -v gnome-keyring-daemon; \
	fi; \
	echo; echo "Samsung certs (DUID-bound, from Tizen Cert Manager):"; \
	check "author.p12 ($(TS_AUTHOR_P12))"  test -f "$(TS_AUTHOR_P12)"; \
	check "distributor.p12 ($(TS_DIST_P12))"  test -f "$(TS_DIST_P12)"; \
	check "TS_AUTHOR_PW set"               test -n "$(TS_AUTHOR_PW)"; \
	check "TS_DIST_PW set"                 test -n "$(TS_DIST_PW)"; \
	if test -f "$(TS_AUTHOR_P12)" && test -n "$(TS_AUTHOR_PW)"; then \
	  check "TS_AUTHOR_PW unlocks author.p12"  openssl pkcs12 -in "$(TS_AUTHOR_P12)" -nokeys -passin "pass:$(TS_AUTHOR_PW)" -legacy; \
	fi; \
	if test -f "$(TS_DIST_P12)" && test -n "$(TS_DIST_PW)"; then \
	  check "TS_DIST_PW unlocks distributor.p12"  openssl pkcs12 -in "$(TS_DIST_P12)" -nokeys -passin "pass:$(TS_DIST_PW)" -legacy; \
	fi; \
	echo; echo "Device:"; \
	if test -x "$(SDB)" && "$(SDB)" devices 2>/dev/null | grep -q "^$(DEVICE)\b"; then \
	  printf "  [OK]      %s\n" "DEVICE=$(DEVICE) attached"; \
	else \
	  printf "  [WARN]    %s\n" "DEVICE=$(DEVICE) not attached (run 'sdb connect <TV_IP>' before 'make install')"; \
	fi; \
	echo; \
	if [ "$$ok" = "1" ]; then echo "All build prerequisites satisfied."; \
	else echo "Some prerequisites missing -- see README.md."; exit 1; fi

help:
	@echo "Targets:"
	@echo "  check-deps  Verify Tizen Studio, .NET workload, certs, etc. are in place"
	@echo "  tailscaled  Cross-compile tailscaled (ARMv7) into Tailscale/lib/"
	@echo "  build       dotnet build (produces an unsigned tpk)"
	@echo "  pack        Re-sign the tpk with SIGN_PROFILE → $(TPK_OUT)"
	@echo "  install     sdb-install $(TPK_OUT) on DEVICE"
	@echo "  run         Launch $(APP_ID) on DEVICE"
	@echo "  uninstall   Remove $(APP_ID) from DEVICE"
	@echo "  clean       Drop build outputs"
	@echo
	@echo "Variables:"
	@echo "  SIGN_PROFILE=$(SIGN_PROFILE)"
	@echo "  DEVICE=$(DEVICE)"

# Spike: Tailscale engine as a CGO c-shared library for in-process (dlopen)
# use, sidestepping the retail-TV seccomp block on execve. c-shared forces
# CGO_ENABLED=1. CC / CGO_CFLAGS / CGO_LDFLAGS MUST match the target ABI and are
# supplied by the environment (CI). Tizen 5.0 armv7 = glibc 2.24 + SOFT-FLOAT
# (softfp), triple armv7l-tizen-linux-gnueabi — CI exports a soft-float CC
# (arm-linux-gnueabi-gcc, NOT -gnueabihf) with `--sysroot` pointed at an
# extracted Tizen 5.0 glibc-2.24 rootstrap and `-mfloat-abi=softfp`. Go's
# linux/arm cgo output is already soft-float, so this matches the TV loader;
# a hard-float (-gnueabihf) build is what produced the earlier dlopen "internal
# error". See .github/workflows/spike-tpk-tizen5.yml.
CSHARED_OUT ?= cshared/libtsspike.so
.PHONY: cshared
cshared:
	CGO_ENABLED=1 GOOS=linux GOARCH=arm GOARM=7 \
	  go build -buildmode=c-shared -ldflags='-s -w' -o $(CSHARED_OUT) ./cshared

tailscaled: Tailscale/lib/tailscaled

# Cross-compile tailscaled at the version pinned in go.mod. `go build` from
# this module fetches the source via the public proxy and writes the binary
# straight into Tailscale/lib/, where the .NET project picks it up.
Tailscale/lib/tailscaled: go.mod
	mkdir -p Tailscale/lib
	$(GO_ENV) go build -trimpath -ldflags='-s -w' \
	  -o $(CURDIR)/Tailscale/lib/tailscaled tailscale.com/cmd/tailscaled

build: tailscaled
	@if [ "$(PROJECT)" = "Tailscale5" ]; then \
	  mkdir -p Tailscale5/shared/res; \
	  cp Tailscale/lib/tailscaled Tailscale5/shared/res/tailscaled; \
	  echo "staged tailscaled into Tailscale5/shared/res"; \
	fi
	cd $(PROJECT) && $(DOTNET) build

# Re-sign the dotnet-produced tpk with our Samsung-issued cert. tz/tizen-core
# stashes the PKCS#12 password via the system secret store: libsecret on
# Linux, Keychain on macOS. macOS's Keychain Just Works; on Linux we have to
# spin up a transient D-Bus session + isolated gnome-keyring so we don't
# touch (or depend on) the user's login keyring.
ifeq ($(UNAME),Linux)
pack: build
	@if [ -z "$(TS_AUTHOR_PW)" ] || [ -z "$(TS_DIST_PW)" ]; then \
	  echo >&2 "Set TS_AUTHOR_PW and TS_DIST_PW in your environment or .env.local."; exit 1; \
	fi
	@kr=$$(mktemp -d); \
	rm -f $(HOME)/tizen-studio-data/profile/profiles.xml; \
	XDG_DATA_HOME=$$kr dbus-run-session -- bash -c '\
	  eval "$$(printf "x\nx\n" | gnome-keyring-daemon --unlock --components=secrets 2>/dev/null)" || true; \
	  eval "$$(gnome-keyring-daemon --start --components=secrets 2>/dev/null)" || true; \
	  $(TZ) security-profiles add -n $(SIGN_PROFILE) -A \
	    -a $(TS_AUTHOR_P12) -p "$(TS_AUTHOR_PW)" \
	    -d $(TS_DIST_P12)   -P "$(TS_DIST_PW)" >/dev/null && \
	  $(TZ) pack -b $(TPK_BUILT) -t tpk -s $(SIGN_PROFILE) -o $(CURDIR)/$(TPK_OUT)'; \
	rc=$$?; rm -rf $$kr; exit $$rc
else
pack: build
	@if [ -z "$(TS_AUTHOR_PW)" ] || [ -z "$(TS_DIST_PW)" ]; then \
	  echo >&2 "Set TS_AUTHOR_PW and TS_DIST_PW in your environment or .env.local."; exit 1; \
	fi
	rm -f $(HOME)/tizen-studio-data/profile/profiles.xml
	$(TZ) security-profiles add -n $(SIGN_PROFILE) -A \
	  -a $(TS_AUTHOR_P12) -p "$(TS_AUTHOR_PW)" \
	  -d $(TS_DIST_P12)   -P "$(TS_DIST_PW)" >/dev/null
	$(TZ) pack -b $(TPK_BUILT) -t tpk -s $(SIGN_PROFILE) -o $(CURDIR)/$(TPK_OUT)
endif

install: pack
	$(TZ) install -p $(CURDIR)/$(TPK_OUT) -e $(DEVICE)

run:
	$(TZ) run -p $(APP_ID) -e $(DEVICE)

uninstall:
	$(SDB) uninstall $(APP_ID)

clean:
	rm -rf Tailscale/bin Tailscale/obj Tailscale/lib/tailscaled $(TPK_OUT)
