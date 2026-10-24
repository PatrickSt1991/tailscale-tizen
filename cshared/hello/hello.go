// Tiny Go c-shared control library for the spike. Same toolchain, same
// sysroot, same linking as libtsspike.so, but ~2MB with a small .bss. If this
// dlopens on the TV and libtsspike.so doesn't, the problem is the size/layout
// of the big image, not "Go runtime in-process" and not platform policy.
package main

import "C"

//export HelloGo
func HelloGo() *C.char {
	return C.CString("hello from Go, in-process")
}

func main() {}
