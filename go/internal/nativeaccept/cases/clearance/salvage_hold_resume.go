package clearance

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Safety revalidation, explicit holds and duplicate-free resume for remote
// work (#525), on the #523 salvage fixture: a hostile staged beside the ruin
// after the review selected it holds the salvage with threat_present and
// nothing is designated while it stands; once it is gone the same target is
// designated again, exactly once, and completes. The service is restarted
// on its journal at each fixture step, so both restarts land mid-work and
// the native designation count on the ruin never exceeds one.
func init() {
	cases.Register(cases.Case{Name: "clearance/salvage-hold-resume", Scope: "A hostile staged beside a selected remote ruin holds its salvage with threat_present and no stale designation; after it clears, and across two service restarts mid-work, the ruin is designated once and deconstructed.",
		Start:       cases.Fixture{Op: "test/storage_haul_prepare", Args: map[string]any{"itemCount": 1}, On: cases.FlatDebugStart()},
		RequiredOps: []string{"test/loot_remote_drop", "test/salvage_remote"}, Quiet: na.QuietRequired, QuietWorld: true, Budget: 8 * time.Minute, Stall: 60 * time.Second,
		Serve: &cases.ServeSpec{Families: []string{"clearance", "supply", "resource"}, Extra: []string{"--routine-resource-target", "Steel:2000"}}, Run: runSalvageHoldResume})
}

func runSalvageHoldResume(ctx context.Context, s cases.Session) error {
	drop, err := s.Harness().Call(ctx, "remote-cell", "test/loot_remote_drop", map[string]any{})
	if err != nil {
		return err
	}
	if !boolean(drop["success"]) {
		return fmt.Errorf("remote fixture: %v", drop)
	}
	staged, err := s.Harness().Call(ctx, "salvage-ready", "test/salvage_remote", map[string]any{"prepare": true})
	if err != nil {
		return err
	}
	s.Report()["prepared"] = staged
	if !boolean(staged["success"]) || !boolean(staged["homeUnchanged"]) {
		return fmt.Errorf("salvage fixture: %v", staged)
	}
	target := na.AsString(staged["target"])
	report := s.Report()
	maxDesignations := 0.0

	// audit reads the fixture on the reattached slot between service runs
	// and records the native designation count on the ruin, which must
	// never exceed one.
	audit := func(label string, args map[string]any) (map[string]any, error) {
		out, err := s.Harness().Call(ctx, label, "test/salvage_remote", args)
		if err != nil {
			return nil, err
		}
		if !boolean(out["success"]) {
			return nil, fmt.Errorf("%s: %v", label, out)
		}
		report[label] = out
		maxDesignations = max(maxDesignations, na.AsNumber(out["designations"]))
		if !boolean(out["homeUnchanged"]) {
			return nil, fmt.Errorf("%s: home changed: %v", label, out)
		}
		return out, nil
	}

	// openPlans counts the clearance plans still open on the target; more
	// than one at once is a duplicate order.
	openPlans := func(ctx context.Context, journal *store.Store) (open, completed int, err error) {
		plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-clearance-", 256)
		if err != nil {
			return 0, 0, err
		}
		for _, p := range plans {
			for _, progress := range p.Progress {
				action, ok := progress.Action().Deconstruction()
				if !ok || action.Target() != target {
					continue
				}
				switch progress.View().Stage {
				case domain.Completed:
					completed++
				case domain.Cancelled, domain.Unsuccessful:
				default:
					open++
				}
			}
		}
		if open > 1 {
			return open, completed, fmt.Errorf("%d clearance plans open on %s at once", open, target)
		}
		return open, completed, nil
	}

	// Selection: the review admits the ruin and a clearance plan carries it.
	service, err := start(ctx, s)
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	err = na.WaitProgress(ctx, wait(service), func(ctx context.Context) (string, bool, error) {
		open, completed, err := openPlans(ctx, journal)
		if err != nil {
			return "", false, err
		}
		if completed > 0 {
			return "", false, fmt.Errorf("salvage completed before the threat could be staged")
		}
		if open > 0 {
			return "salvage selected", true, nil
		}
		return "waiting for salvage selection", false, nil
	})
	if err != nil {
		return err
	}
	service.Stop()
	if err = reattach(ctx, s); err != nil {
		return err
	}

	// A hostile appears beside the ruin after selection.
	threat, err := audit("threat-spawn", map[string]any{"threat": "spawn"})
	if err != nil {
		return err
	}
	if !boolean(threat["threatPresent"]) {
		return fmt.Errorf("threat not staged: %v", threat)
	}
	service, err = start(ctx, s)
	if err != nil {
		return err
	}
	defer service.Stop()
	if journal, err = service.Store(ctx); err != nil {
		return err
	}
	err = na.WaitProgress(ctx, wait(service), func(ctx context.Context) (string, bool, error) {
		if _, _, err := openPlans(ctx, journal); err != nil {
			return "", false, err
		}
		review, err := journal.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		for _, hold := range review.ClearanceHolds {
			if hold.Target == target && hold.Reason == policy.RemoteHoldThreat {
				report["hold"] = map[string]any{"reason": hold.Reason, "tick": uint64(review.Tick)}
				return "held threat_present", true, nil
			}
		}
		if review.SalvageTarget == target {
			return "", false, fmt.Errorf("review still selects %s under a threat", target)
		}
		return "waiting for the threat hold", false, nil
	})
	if err != nil {
		return err
	}
	// Under the hold no designation is dispatched: a pending action keeps
	// its unsafe_threat hold, and a designation placed before the threat
	// was released with the first service's authority, so the ruin carries
	// none while the threat stands.
	held, err := heldDeconstruction(ctx, journal, target)
	if err != nil {
		return err
	}
	report["held_action"] = held
	service.Stop()
	if err = reattach(ctx, s); err != nil {
		return err
	}
	if out, err := audit("threat-held", map[string]any{}); err != nil {
		return err
	} else if na.AsNumber(out["designations"]) != 0 {
		return fmt.Errorf("stale designation under a threat: %v (journal %v)", out, held)
	}

	// The threat clears; the restarted service resumes the salvage once.
	if _, err = audit("threat-clear", map[string]any{"threat": "clear"}); err != nil {
		return err
	}
	service, err = start(ctx, s)
	if err != nil {
		return err
	}
	defer service.Stop()
	if journal, err = service.Store(ctx); err != nil {
		return err
	}
	err = na.WaitProgress(ctx, wait(service), func(ctx context.Context) (string, bool, error) {
		_, completed, err := openPlans(ctx, journal)
		if err != nil {
			return "", false, err
		}
		if completed > 0 {
			return "salvage completed", true, nil
		}
		return "waiting for salvage", false, nil
	})
	if err != nil {
		return err
	}
	service.Stop()
	if err = reattach(ctx, s); err != nil {
		return err
	}
	for i := 0; i < 8; i++ {
		if _, err = audit(fmt.Sprintf("salvage-audit-%d", i), map[string]any{}); err != nil {
			return err
		}
		if _, err = s.Advance(ctx, 1000); err != nil {
			return err
		}
		out := report[fmt.Sprintf("salvage-audit-%d", i)].(map[string]any)
		if !boolean(out["present"]) && na.AsNumber(out["delivered"]) > 0 {
			report["max_designations"] = maxDesignations
			if maxDesignations > 1 {
				return fmt.Errorf("duplicate designations on the ruin: %v", maxDesignations)
			}
			return nil
		}
	}
	return fmt.Errorf("salvage yield not delivered within 8000 ticks")
}

// heldDeconstruction reports the hold reasons on the open clearance action
// for the target: a pending action must carry the threat (or the unsafe
// route it causes), an action dispatched before the threat is in flight
// until the restarted service observes its release, and no open action at
// all means the released plan ended and the held review admits no new one.
func heldDeconstruction(ctx context.Context, journal *store.Store, target string) (map[string]any, error) {
	plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-clearance-", 256)
	if err != nil {
		return nil, err
	}
	for _, p := range plans {
		for _, progress := range p.Progress {
			action, ok := progress.Action().Deconstruction()
			if !ok || action.Target() != target {
				continue
			}
			view := progress.View()
			switch view.Stage {
			case domain.Pending, domain.Prepared:
				reasons, held := view.FreshHeldReason()
				if !held {
					return nil, fmt.Errorf("pending salvage action %s carries no hold under a threat", p.Spec.ID())
				}
				for _, reason := range reasons {
					if reason == domain.HeldUnsafeThreat || reason == domain.HeldUnsafeRoute {
						return map[string]any{"plan": string(p.Spec.ID()), "stage": string(view.Stage), "reasons": reasons}, nil
					}
				}
				return nil, fmt.Errorf("pending salvage action held for %v, not the threat", reasons)
			case domain.Dispatched, domain.AwaitingObservation:
				return map[string]any{"plan": string(p.Spec.ID()), "stage": string(view.Stage), "reasons": "dispatched before the threat"}, nil
			}
		}
	}
	return map[string]any{"plan": "", "stage": "", "reasons": "released"}, nil
}
