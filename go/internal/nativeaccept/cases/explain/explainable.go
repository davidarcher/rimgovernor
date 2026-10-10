// Package explain holds the nightly check that the bot can explain itself
// (#2702, epic #2690): every refusal or wait a planner reports on a running
// colony has a plain-language concern_transition row in the explanation ring.
package explain

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// windowTicks is the observed stretch: half a game day on the baseline
// colony, long enough for several review rounds and the first refusals.
const windowTicks int64 = 30000

func init() {
	cases.Register(cases.Case{
		Name: "explain/every-concern",
		Scope: "Epic #2690 (#2702): on a running baseline colony every concern or planner that reports a refusal or wait has at least one concern_transition row " +
			"in explain.jsonl whose cause the shared wording table (policy.Wording, the one the launcher renders) names in plain language, and no row carries " +
			"free text. A concern first seen clear writes no row by design and is reported as silent, not failed. Nightly tier. The launcher's timeline model " +
			"lives in package main and cannot be imported, so the case reads the rows through bridge.MergedTimelineReader; a Go snapshot cannot prove that " +
			"the live planners keep emitting.",
		Start:  cases.Save{Name: sustained.BaselineSave},
		Keep:   []string{string(na.LiveNeeds)},
		Serve:  &cases.ServeSpec{NativeTimeout: 15 * time.Second, StepStall: 90 * time.Second, Prefix: "explain-every-concern"},
		Budget: 20 * time.Minute,
		Reason: "half a game day on the baseline colony with every routine family live",
		Crew:   cases.Crew{Size: 3},
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	stopped := false
	stop := func() {
		if !stopped {
			stopped = true
			service.Stop()
		}
	}
	defer stop()
	if _, err = service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	st, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	defer st.Close()

	// The active set: every concern the review held a progress record for at
	// any poll of the window, with the last planner cause filed on it.
	active := map[string]policy.Cause{}
	var start int64
	w := na.Wait{Stall: na.StallBudget(), Interval: 2 * time.Second, Terminal: service.Exited}
	err = na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := st.LoadRounds(ctx)
		if err != nil || review.Tick == 0 {
			return na.Signature("no-review", err), false, nil
		}
		tick := int64(review.Tick)
		if start == 0 {
			start = tick
		}
		for _, p := range review.Progress {
			id := string(p.Concern)
			if _, seen := active[id]; !seen || p.Planner != "" {
				active[id] = p.Planner
			}
		}
		return na.Signature(tick, len(active)), tick-start >= windowTicks, nil
	})
	if err != nil {
		return fmt.Errorf("observed %d active concerns before the window closed: %w", len(active), err)
	}
	stop()

	rows, err := bridge.NewMergedTimelineReader(service.FlightPath).Read()
	if err != nil {
		return fmt.Errorf("read flight and explanation rings: %w", err)
	}
	result, auditErr := Audit(rows, active)
	report["window_ticks"] = windowTicks
	report["explainable"] = result
	return auditErr
}
