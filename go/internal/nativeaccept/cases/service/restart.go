// Package service holds the cases about the controller process itself
// rather than any planner: here the kill-and-restart acceptance for SIMP06.
// A controller launched with --resume runs the bot for the observed world
// with no HTTP write at all, is killed mid-play, and a second controller
// reopening the same SQLite state resumes autonomous play for the same
// world -- again without a dashboard step -- continuing the routine review
// past the revision the killed process left. Nothing here backs up or
// restores the database; the plan is re-derived from observation.
package service

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
)

func init() {
	cases.Register(cases.Case{
		Name:  "service/restart",
		Scope: "Kill-and-restart: serve --resume runs the bot for the observed world with no HTTP write, is killed, and a restarted controller on the same state resumes autonomous play for the same world and advances the routine review, again with no player step.",
		Start: cases.Save{Name: sustained.BaselineSave},
		// "work" is the lightest family that still produces a routine review
		// with a bound goal; the point is autonomy, not any particular
		// planner.
		Serve:  &cases.ServeSpec{Families: []string{"work"}, Resume: true, Prefix: "restart"},
		Budget: 10 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	identity := s.Identity()
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	firstPID := service.PID
	// The baseline save loads with pending letters that hold the clock; the
	// keep-alive acknowledges them so the resumed controller plays (#166).
	service.KeepAuthority(ctx)
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	first, diagnostics, err := service.WaitRoutineReview(ctx, journal, 120*time.Second)
	report["first_diagnostics"] = diagnostics
	if err != nil {
		return fmt.Errorf("first controller never played autonomously: %w", err)
	}
	firstData, _ := json.Marshal(first)
	report["first_review"] = json.RawMessage(firstData)
	reviewed := map[string]any{"colonyId": string(first.Snapshot.Colony), "loadToken": string(first.Snapshot.Load), "mapId": float64(first.Snapshot.Map)}
	if !first.Enabled || !na.MatchesIdentity(reviewed, identity) {
		return fmt.Errorf("first review is not an enabled review of the observed world: %#v", first)
	}

	// Kill mid-play. The game keeps running; the service's own GABS
	// subprocess releases the slot shortly after, so the restart may need a
	// few attempts to attach.
	service.Stop()
	report["killed_pid"] = firstPID
	deadline := time.Now().Add(90 * time.Second)
	var restarted *na.ServiceProcess
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
	if restarted.PID == firstPID {
		return fmt.Errorf("restart did not produce a new process: pid %d", restarted.PID)
	}
	report["restarted_pid"] = restarted.PID
	restarted.KeepAuthority(ctx)
	journal, err = restarted.Store(ctx)
	if err != nil {
		return err
	}
	deadline = time.Now().Add(120 * time.Second)
	second := first
	for second.Revision <= first.Revision || !second.Enabled {
		if time.Now().After(deadline) {
			return fmt.Errorf("restarted controller reached automate but the review did not advance past revision %d: %#v", first.Revision, second)
		}
		var diagnostics []map[string]any
		second, diagnostics, err = restarted.WaitRoutineReview(ctx, journal, time.Until(deadline))
		report["second_diagnostics"] = diagnostics
		if err != nil {
			return fmt.Errorf("restarted controller never resumed autonomous play: %w", err)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}
	secondData, _ := json.Marshal(second)
	report["second_review"] = json.RawMessage(secondData)
	// The native generation legitimately advances (the stale Auto is revoked
	// and a fresh grant issued); the world and root plan must not.
	if second.Snapshot.Colony != first.Snapshot.Colony || second.Snapshot.Load != first.Snapshot.Load || second.Snapshot.Map != first.Snapshot.Map || second.Snapshot.Plan != first.Snapshot.Plan {
		return fmt.Errorf("restart changed the reviewed world: %#v -> %#v", first.Snapshot, second.Snapshot)
	}
	if second.Snapshot.Native <= first.Snapshot.Native {
		return fmt.Errorf("restart did not reclaim authority at a fresh native generation: %#v -> %#v", first.Snapshot, second.Snapshot)
	}
	report["review_revisions"] = map[string]any{"before_kill": first.Revision, "after_restart": second.Revision}
	return nil
}
