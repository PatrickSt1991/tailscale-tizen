// This file pins the tailscaled binary that's cross-compiled into the
// .NET app's lib/. The build tag keeps it out of any real build; the
// blank import is there so `go mod tidy` doesn't drop the dep.
//
//go:build tools

package tools

import (
	_ "tailscale.com/cmd/tailscaled"
)
