package main

import (
	"io/fs"
	"os"
	"path/filepath"
	"time"

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

// dashboardInputs are the dashboard files a build reads, relative to
// dashboard/; directories count by their newest file.
var dashboardInputs = []string{"src", "public", "index.html", "timeline.html", "package.json", "pnpm-lock.yaml", "vite.config.ts", "tsconfig.json"}

// DashboardState compares dist/index.html with the newest input.
func DashboardState(dashboard string) (state, reason string) {
	built, err := os.Stat(filepath.Join(dashboard, "dist", "index.html"))
	if err != nil {
		return StateMissing, "dist/index.html is missing"
	}
	newest, name := newestMTime(dashboard, dashboardInputs)
	if newest.After(built.ModTime()) {
		return StateStale, name + " is newer than the build"
	}
	return StateOK, "built after its sources"
}

// newestMTime is the newest modification time under the named paths of
// root, and the path that holds it.
func newestMTime(root string, names []string) (time.Time, string) {
	var newest time.Time
	var at string
	for _, n := range names {
		filepath.WalkDir(filepath.Join(root, n), func(path string, d fs.DirEntry, err error) error {
			if err != nil || d.IsDir() {
				return nil
			}
			if info, err := d.Info(); err == nil && info.ModTime().After(newest) {
				newest, at = info.ModTime(), path
			}
			return nil
		})
	}
	if rel, err := filepath.Rel(root, at); err == nil {
		at = filepath.ToSlash(rel)
	}
	return newest, at
}
