// Spike: build the Tailscale engine as a CGO c-shared library and expose a
// single entry point the .NET app can P/Invoke, so tailscale runs IN-PROCESS
// (dlopen, not execve) and sidesteps the retail-TV seccomp block on spawning
// child processes.
//
// This file's only job right now is to prove two things in CI, before we touch
// the TV:
//   1. tailscale (here via tsnet) compiles as `-buildmode=c-shared` for
//      linux/arm (armv7, CGO_ENABLED=1) at the pinned v1.98.0.
//   2. There's a stable C ABI entry point (TsSpike) the app can call.
//
// The runtime half (does a dlopen'd Go .so actually start tailscale in-process
// under the 5.0 sandbox) is validated in step B, once this builds.
package main

/*
#include <stdlib.h>
*/
import "C"

import (
	"context"
	"fmt"
	"os"
	"time"

	"tailscale.com/tsnet"
)

// TsSpike starts a userspace tsnet node in-process. `cdir` is a writable state
// dir; `clog` is a file we append progress to (the .NET side tails it into the
// on-screen diag). Returns immediately with a status string; the node comes up
// on a background goroutine and logs its auth URL to the log file.
//
//export TsSpike
func TsSpike(cdir *C.char, clog *C.char) *C.char {
	dir := C.GoString(cdir)
	logPath := C.GoString(clog)

	lf, _ := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
	logf := func(format string, args ...any) {
		if lf == nil {
			return
		}
		fmt.Fprintf(lf, time.Now().UTC().Format("15:04:05")+" "+format+"\n", args...)
	}

	logf("TsSpike entered — Go runtime alive in-process; state dir=%s", dir)

	srv := &tsnet.Server{
		Dir:       dir,
		Hostname:  "tizen-tv-spike",
		Ephemeral: true,
		Logf:      logf,
	}

	go func() {
		ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
		defer cancel()
		st, err := srv.Up(ctx)
		if err != nil {
			logf("tsnet Up error: %v", err)
			return
		}
		logf("tsnet Up OK — BackendState=%s", st.BackendState)
	}()

	return C.CString("tsspike-started")
}

func main() {}
