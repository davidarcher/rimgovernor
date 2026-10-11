package main

import (
	"context"
	"log/slog"
	"os"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// observeSnapshotEvery is how often --observe-snapshot refreshes the copy.
const observeSnapshotEvery = 5 * time.Second

// startObserveSnapshots runs runObserveSnapshots for the --observe-snapshot
// path, if any, and returns the function that stops it. Call the stop before
// closing database.
func startObserveSnapshots(ctx context.Context, config serveConfig, database *store.Store) (stop func()) {
	if config.observeSnapshot == "" {
		return func() {}
	}
	ctx, cancel := context.WithCancel(ctx)
	done := make(chan struct{})
	go func() {
		defer close(done)
		runObserveSnapshots(ctx, database, config.observeSnapshot, observeSnapshotEvery)
	}()
	return func() { cancel(); <-done }
}

// runObserveSnapshots keeps path at a recent whole-store copy until ctx ends,
// then takes a last one. The copy feeds the optional Grafana SQLite panels
// (docs/developers/observability.md); a failed copy is logged and retried, and
// never reaches the controller.
func runObserveSnapshots(ctx context.Context, database *store.Store, path string, every time.Duration) {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		slog.Warn("observe snapshot disabled", telemetry.ComponentKey, "serve", telemetry.KindKey, "observe_snapshot_failed", "path", path, "err", err)
		return
	}
	take := func(ctx context.Context) {
		if err := database.SnapshotReplace(ctx, path); err != nil {
			slog.Warn("observe snapshot failed", telemetry.ComponentKey, "serve", telemetry.KindKey, "observe_snapshot_failed", "path", path, "err", err)
		}
	}
	take(ctx)
	ticker := time.NewTicker(every)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			take(context.WithoutCancel(ctx))
			return
		case <-ticker.C:
			take(ctx)
		}
	}
}
