// Package combatdispatch runs a combat lab sweep's seeds on at most two
// workers and reduces the results in seed order (#2839).
//
// Seeds are independent, so two workers (each with its own game root, or its
// own remote job) may run them concurrently. Two is the ceiling: four
// concurrent remote jobs cancelled three at the 15 minute mark, and a loaded
// box cuts runs and distorts native timing. The reduction sorts by seed, never
// by completion order, so scheduling cannot change the output. The Report
// records the concurrency as evidence; any metric that depends on wall time
// (WallMs, per-run step latency) is excluded from comparisons between reports.
package combatdispatch

import (
	"context"
	"fmt"
	"os/exec"
	"slices"
	"strings"
	"sync"
	"time"
)

// MaxWorkers is the most runs that may be in flight at once.
const MaxWorkers = 2

// Run is one seed's outcome.
type Run[T any] struct {
	Seed   int    `json:"seed"`
	Worker int    `json:"worker"`
	Value  T      `json:"value"`
	Error  string `json:"error,omitempty"`
}

// Report is the seed-ordered reduction plus the concurrency it ran under.
type Report[T any] struct {
	Workers int `json:"workers"`
	// Load notes the box load at dispatch (see LoadNote).
	Load string `json:"load"`
	// WallMs is the whole dispatch's wall time; do not compare it across
	// reports taken at different concurrency or load.
	WallMs int64    `json:"wall_ms"`
	Runs   []Run[T] `json:"runs"`
}

// Dispatch runs fn once per seed on the given number of workers and returns
// the runs sorted by seed. fn receives the worker index (0..workers-1), which
// maps to that worker's game root; one worker never runs two seeds at once. A
// failing run is recorded, does not stop the others, and is returned in the
// error (the first failure by seed) beside the full report. workers above
// MaxWorkers, below 1, or duplicate seeds are refused before anything runs.
func Dispatch[T any](ctx context.Context, seeds []int, workers int, load string, fn func(ctx context.Context, worker, seed int) (T, error)) (Report[T], error) {
	report := Report[T]{Workers: workers, Load: load}
	if workers < 1 || workers > MaxWorkers {
		return report, fmt.Errorf("combat lab dispatch: %d workers, want 1 to %d", workers, MaxWorkers)
	}
	ordered := slices.Sorted(slices.Values(seeds))
	if len(slices.Compact(slices.Clone(ordered))) != len(ordered) {
		return report, fmt.Errorf("combat lab dispatch: duplicate seeds in %v", seeds)
	}
	start := time.Now()
	report.Runs = make([]Run[T], len(ordered))
	next := make(chan int)
	var wg sync.WaitGroup
	for w := range workers {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for i := range next {
				run := Run[T]{Seed: ordered[i], Worker: w}
				if value, err := fn(ctx, w, ordered[i]); err != nil {
					run.Error = err.Error()
				} else {
					run.Value = value
				}
				report.Runs[i] = run // each index has one writer
			}
		}()
	}
	for i := range ordered {
		next <- i
	}
	close(next)
	wg.Wait()
	report.WallMs = time.Since(start).Milliseconds()
	for _, run := range report.Runs {
		if run.Error != "" {
			return report, fmt.Errorf("combat lab dispatch: seed %d: %s", run.Seed, run.Error)
		}
	}
	return report, ctx.Err()
}

// LoadNote describes the box load for the report: the RimWorld processes
// running now. Call it before dispatching, so the workers are not counted.
// Native timing degrades with peers, so the count is the evidence a reader
// weighs a timing-dependent metric against.
func LoadNote() string {
	out, err := exec.Command("tasklist", "/FI", "IMAGENAME eq RimWorldWin64.exe", "/FO", "CSV", "/NH").Output()
	if err != nil {
		return "rimworld processes: unknown"
	}
	return fmt.Sprintf("rimworld processes before dispatch: %d", strings.Count(string(out), "\"RimWorldWin64.exe\""))
}
