package service

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
)

// The development case is issue #9's controller-side native acceptance for
// colony-wide development priorities. A resumed controller plays the
// prepared world autonomously while the case samples the development
// ranking every review records through GET /api/routines, then kills and
// restarts the controller on the same state and keeps sampling. It asserts
// what the controller replay can establish -- admission bounded by the
// project limit, the observed worker count and per-work-type free labor;
// an explicit reason on every deferred goal, with a censused bottleneck on
// labor deferrals; a measured (never unknown) research deficit when a
// research target is configured, proving the review-time research read;
// and waiting ages retained across the restart pair -- and records
// per-goal admission and deferral metrics. It does not establish pawn
// progress on any project: that is the sustained matrix's campaign
// evidence, gathered separately.
//
// A letter that pauses is a player interruption the controller waits on;
// the service's keep-alive acknowledges such holds through the player API
// (counted under the launch's keepalive entry, not asserted) so the
// ranking plays through the map's threats.
const (
	projectLimit   = 2
	minReviews     = 2
	researchTarget = "MicroelectronicsBasics"
	resourceTarget = "WoodLog:400"
	watch          = 6 * time.Minute
	afterRestart   = 3 * time.Minute
	poll           = 5 * time.Second
)

func init() {
	cases.Register(cases.Case{
		Name:  "service/development",
		Scope: "Development priorities (#9): a resumed controller's recorded ranking is sampled through /api/routines across a kill-and-restart pair; admission stays within the project limit, worker count and free labor, every deferral carries a reason, a configured research target is measured at review time, and waiting ages survive the restart. Pawn progress is out of scope.",
		Start: cases.Save{Name: sustained.BaselineSave},
		// Every family composes; the ranking under test is the whole ladder.
		Serve: &cases.ServeSpec{
			Resume: true, Prefix: "development", NativeTimeout: 15 * time.Second,
			Extra: []string{"--routine-project-limit", fmt.Sprint(projectLimit), "--routine-research-target", researchTarget, "--routine-resource-target", resourceTarget},
		},
		Budget: 15 * time.Minute,
		Run:    development,
	})
}

func development(ctx context.Context, s cases.Session) error {
	report := s.Report()
	output := s.Config().Output
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	firstPID := service.PID
	service.KeepAuthority(ctx)
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	first, diagnostics, err := service.WaitRoutineReview(ctx, journal, 180*time.Second)
	report["first_diagnostics"] = diagnostics
	if err != nil {
		return fmt.Errorf("controller never played autonomously: %w", err)
	}
	report["first_review_revision"] = first.Revision

	var samples []Sample
	var violations []string
	// sampleFor polls until window elapses or enough has been seen: the
	// windows are ceilings, so a healthy box finishes as soon as the clock
	// has produced the reviews the verdict needs.
	sampleFor := func(phase string, window time.Duration, enough func() bool) error {
		deadline := time.Now().Add(window)
		for time.Now().Before(deadline) && !enough() {
			status, code, err := service.API("GET", "/api/routines", nil, "")
			if err != nil {
				return fmt.Errorf("%s: /api/routines: %w", phase, err)
			}
			if code != 200 {
				return fmt.Errorf("%s: /api/routines status %d: %#v", phase, code, status)
			}
			sample, err := decodeSample(status, phase, time.Now())
			if err != nil {
				return err
			}
			samples = append(samples, sample)
			for _, v := range checkSample(sample, projectLimit, researchTarget) {
				violations = append(violations, fmt.Sprintf("%s sample %d (tick %d): %s", phase, len(samples), tickOf(sample), v))
			}
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(poll):
			}
		}
		return nil
	}
	distinctTicks := func(phase string, above int64) int {
		seen := map[int64]bool{}
		for _, sample := range samples {
			if sample.Phase == phase && sample.Development != nil && sample.Development.Tick > above {
				seen[sample.Development.Tick] = true
			}
		}
		return len(seen)
	}
	if err := sampleFor("before-restart", watch, func() bool { return distinctTicks("before-restart", -1) >= minReviews }); err != nil {
		return err
	}
	var before *Development
	for i := len(samples) - 1; i >= 0; i-- {
		if samples[i].Development != nil {
			before = samples[i].Development
			break
		}
	}
	if before == nil {
		return fmt.Errorf("no development ranking was recorded in %s of autonomous play", watch)
	}

	// Kill mid-play and restart on the same state; the service's own GABS
	// subprocess releases the slot shortly after, so attaching may retry.
	service.Stop()
	report["killed_pid"] = firstPID
	var restarted *na.ServiceProcess
	deadline := time.Now().Add(90 * time.Second)
	for {
		restarted, err = service.Restart(ctx)
		if err == nil {
			break
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("restarted controller never attached: %w", err)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(3 * time.Second):
		}
	}
	defer restarted.Stop()
	service = restarted
	service.KeepAuthority(ctx)
	report["restarted_pid"] = service.PID
	journal, err = service.Store(ctx)
	if err != nil {
		return err
	}
	deadline = time.Now().Add(180 * time.Second)
	second := first
	for second.Revision <= first.Revision || !second.Enabled {
		if time.Now().After(deadline) {
			return fmt.Errorf("restarted controller did not advance the review past revision %d", first.Revision)
		}
		second, _, err = service.WaitRoutineReview(ctx, journal, time.Until(deadline))
		if err != nil {
			return fmt.Errorf("restarted controller never resumed autonomous play: %w", err)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}
	report["restart_review_revisions"] = map[string]any{"before_kill": first.Revision, "after_restart": second.Revision}
	if err := sampleFor("after-restart", afterRestart, func() bool { return distinctTicks("after-restart", before.Tick) >= 1 }); err != nil {
		return err
	}
	var after *Development
	for i := range samples {
		if samples[i].Phase == "after-restart" && samples[i].Development != nil {
			after = samples[i].Development
			break
		}
	}
	if after == nil {
		return fmt.Errorf("no development ranking was recorded after the restart")
	}
	for _, v := range checkRestart(*before, *after) {
		violations = append(violations, "restart: "+v)
	}

	timeline, _ := json.MarshalIndent(samples, "", "  ")
	_ = os.WriteFile(filepath.Join(output, "timeline.json"), timeline, 0644)
	metrics := deriveMetrics(samples)
	report["metrics"] = metrics
	report["violations"] = violations
	report["before_restart"] = before
	report["after_restart"] = after
	if len(metrics.Goals) == 0 {
		return fmt.Errorf("no optional goal was ever ranked; the run is vacuous")
	}
	if metrics.Reviews < minReviews {
		return fmt.Errorf("only %d distinct review tick(s) sampled (minimum %d); the clock never advanced past the resumed review", metrics.Reviews, minReviews)
	}
	if !metrics.ResearchRanked {
		return fmt.Errorf("research target %q never produced a ranked EnsureResearch row", researchTarget)
	}
	if len(violations) > 0 {
		return fmt.Errorf("%d invariant violation(s); first: %s", len(violations), violations[0])
	}
	return nil
}

func tickOf(s Sample) int64 {
	if s.Development == nil {
		return -1
	}
	return s.Development.Tick
}
