package main

import (
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// Artifact states the status panel shows.
const (
	StateOK       = "ok"
	StateStale    = "stale"
	StateMissing  = "missing"
	StateBuilding = "building"
	StateFailed   = "failed"
	StatePending  = "pending"
)

// ModState is the installed production mod's state against the
// worktree's native source hash.
func ModState(m *na.PackageManifest, sourceTree string) (state, reason string) {
	switch {
	case m == nil:
		return StateMissing, "no installed build"
	case m.SourceTree != sourceTree:
		return StateStale, "installed build's sources differ from the worktree"
	case len(m.Fixtures) > 0:
		return StateStale, "installed build is a fixture build"
	}
	return StateOK, "installed build is current"
}
