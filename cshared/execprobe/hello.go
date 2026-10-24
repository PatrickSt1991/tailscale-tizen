// Tiny STATIC Go binary (CGO_ENABLED=0) for the execve probe. No libc, no
// dynamic interpreter, so execve maps it directly in the kernel — it never
// goes through the userspace ld.so file-backed PROT_EXEC mmap that step C
// proved is denied. If this runs as a subprocess, execve of app-bundled
// native code is allowed and a static tailscaled is viable on retail 5.0.
package main

import (
	"fmt"
	"os"
	"runtime"
)

func main() {
	fmt.Printf("hello from static Go exec: GOARCH=%s pid=%d argv=%v\n",
		runtime.GOARCH, os.Getpid(), os.Args)
}
