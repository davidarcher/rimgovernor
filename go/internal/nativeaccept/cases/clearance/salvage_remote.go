package clearance

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"time"
)

// recoveryCase registers one case over the remote-recovery fixtures
// (test/loot_remote_drop, test/salvage_remote). The cases share one start and
// one serve spec; they differ in the fixture scenario and in what they assert.
func recoveryCase(name, scope string, run func(context.Context, cases.Session) error) {
	cases.Register(cases.Case{Name: name, Scope: scope,
		Start:       cases.Fixture{Op: "test/storage_haul_prepare", Args: map[string]any{"itemCount": 1}, On: cases.LabStart()},
		RequiredOps: []string{"test/loot_remote_drop", "test/salvage_remote"}, Quiet: na.QuietRequired, QuietWorld: true, Budget: 4 * time.Minute, Crew: cases.Crew{Size: 3}, Stall: 60 * time.Second,
		Serve: &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Clearance, routinefamily.Supply, routinefamily.Resource}}, Run: run})
}

func init() {
	recoveryCase("clearance/salvage-remote", "A useful far-edge ruin is deconstructed and its steel delivered without adding Home.",
		func(ctx context.Context, s cases.Session) error { return runSalvage(ctx, s, salvageRun{}) })
}

// salvageRun varies the salvage flow: the fixture scenario ("covered" stocks
// Steel above its floor, "cluster" stands three roof-holding walls under a
// thin roof) and what the run must observe besides the delivery.
type salvageRun struct {
	scenario string
	// covered: the ruin's Steel is not short, so the queue must rank it in the
	// last tier and still recover it (demand never holds a thing).
	covered bool
	// cluster: every wall goes, roof first, and no roof collapses.
	cluster bool
}

// prepareRemote drops the remote loot and stages the salvage fixture for a scenario.
func prepareRemote(ctx context.Context, s cases.Session, scenario string) (map[string]any, error) {
	drop, err := s.Harness().Call(ctx, "remote-cell", "test/loot_remote_drop", map[string]any{})
	if err != nil {
		return nil, err
	}
	s.Report()["drop"] = drop
	if !boolean(drop["success"]) {
		return nil, fmt.Errorf("remote fixture: %v", drop)
	}
	staged, err := s.Harness().Call(ctx, "salvage-ready", "test/salvage_remote", map[string]any{"prepare": true, "scenario": scenario})
	if err != nil {
		return nil, err
	}
	s.Report()["prepared"] = staged
	if !boolean(staged["success"]) || !boolean(staged["homeUnchanged"]) {
		return nil, fmt.Errorf("salvage fixture: %v", staged)
	}
	return staged, nil
}

// queueEntry is the journaled recovery queue's row for id, if the latest review has one.
func queueEntry(ctx context.Context, journal *store.Store, id string) (policy.RecoveryEntry, bool, error) {
	review, err := journal.LoadRounds(ctx)
	if err != nil || review.RecoveryQueue == nil {
		return policy.RecoveryEntry{}, false, err
	}
	for _, e := range review.RecoveryQueue.Entries {
		if e.ID == id {
			return e, true, nil
		}
	}
	return policy.RecoveryEntry{}, false, nil
}

// completedActions walks every plan in the journal's window and calls visit
// for each completed action.
func completedActions(ctx context.Context, journal *store.Store, visit func(domain.Action)) error {
	plans, err := journal.PlanHistoryWithMethods(ctx, 256, "*")
	if err != nil {
		return err
	}
	for _, p := range plans {
		for i, action := range p.Spec.Actions() {
			if p.Progress[i].View().Stage == domain.Completed {
				visit(action)
			}
		}
	}
	return nil
}

func runSalvage(ctx context.Context, s cases.Session, run salvageRun) error {
	staged, err := prepareRemote(ctx, s, run.scenario)
	if err != nil {
		return err
	}
	target := na.AsString(staged["target"])
	if run.covered && na.AsNumber(staged["stock"]) <= 200 {
		return fmt.Errorf("covered fixture must stock Steel above its 200 floor: %v", staged)
	}
	if run.cluster && len(na.AsSlice(staged["cluster"])) < 3 {
		return fmt.Errorf("cluster fixture must stand at least three ruins: %v", staged)
	}
	service, err := start(ctx, s)
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	defer journal.Close()
	rankedRest, roofOff := false, false
	err = na.WaitProgress(ctx, wait(service), func(ctx context.Context) (string, bool, error) {
		entry, ok, err := queueEntry(ctx, journal, target)
		if err != nil {
			return "", false, err
		}
		if ok && entry.Tier == policy.RecoveryRest && len(entry.Short) == 0 && entry.Status != policy.RecoveryHeld {
			rankedRest = true
		}
		done := false
		if err = completedActions(ctx, journal, func(a domain.Action) {
			if d, ok := a.Deconstruction(); ok && d.Target() == target {
				done = true
			}
			if _, ok := a.RemoveRoof(); ok {
				roofOff = true
			}
		}); err != nil {
			return "", false, err
		}
		if run.covered && !rankedRest {
			return "waiting for a covered ranking", false, nil
		}
		if done {
			return "salvage completed", true, nil
		}
		return na.Signature(ok, entry.Status, roofOff), false, nil
	})
	if err != nil {
		return err
	}
	service.Stop()
	if err = reattach(ctx, s); err != nil {
		return err
	}
	s.Report()["ranked_covered"], s.Report()["roof_removed_first"] = rankedRest, roofOff
	for i := 0; i < 8; i++ {
		if _, err = s.Advance(ctx, 1000); err != nil {
			return err
		}
		audit, err := s.Harness().Call(ctx, fmt.Sprintf("salvage-audit-%d", i), "test/salvage_remote", map[string]any{})
		if err != nil {
			return err
		}
		s.Report()["audit"] = audit
		if !boolean(audit["homeUnchanged"]) {
			return fmt.Errorf("home changed: %v", audit)
		}
		if run.cluster {
			if clusterDone(audit) {
				if !roofOff {
					return fmt.Errorf("cluster removed without a roof-off action: %v", audit)
				}
				if na.AsNumber(audit["rubble"]) != 0 {
					return fmt.Errorf("a roof collapsed over the cluster: %v", audit)
				}
				return nil
			}
			continue
		}
		if !boolean(audit["present"]) && na.AsNumber(audit["delivered"]) > 0 {
			return nil
		}
	}
	return fmt.Errorf("salvage yield not delivered within 8000 ticks")
}

// clusterDone: every cluster ruin is gone and its steel arrived.
func clusterDone(audit map[string]any) bool {
	for _, raw := range na.AsSlice(audit["cluster"]) {
		row, _ := na.AsMap(raw)
		if boolean(row["present"]) {
			return false
		}
	}
	return len(na.AsSlice(audit["cluster"])) > 0 && na.AsNumber(audit["delivered"]) > 0
}
