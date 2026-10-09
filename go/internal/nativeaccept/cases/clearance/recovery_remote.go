package clearance

import (
	"context"
	"fmt"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Full-map recovery acceptance. The cases extend the
// clearance/salvage-remote fixtures (test/loot_remote_drop,
// test/salvage_remote); each proves one decision of the one recovery queue.
func init() {
	recoveryCase("clearance/recovery-covered", "A far-edge ruin whose Steel is not short (stock above the floor) is still recovered; the queue ranks it in the last tier.",
		func(ctx context.Context, s cases.Session) error {
			return runSalvage(ctx, s, salvageRun{scenario: "covered", covered: true})
		})
	recoveryCase("clearance/recovery-cluster", "A cluster of roof-holding ruins under a thin roof is recovered roof-first in one batch: roofs come off, every ruin goes, no roof collapses.",
		func(ctx context.Context, s cases.Session) error {
			return runSalvage(ctx, s, salvageRun{scenario: "cluster", cluster: true})
		})
	recoveryCase("clearance/recovery-loot-covered", "A forbidden far-edge Steel stack is released and delivered although Steel stock covers demand.", runLootCovered)
	recoveryCase("clearance/recovery-hostile", "A ruin with a hostile beside it stays held in the queue; nothing is designated and nothing is delivered.", runRecoveryHostile)
	recoveryCase("clearance/recovery-foreign-ids", "A foreign building claim and a plant cut apply by the Go-built Thing_<Def><n> id, which resolves through native RefIndex.Thing (#2293).", runForeignIDs)
}

func runLootCovered(ctx context.Context, s cases.Session) error {
	drop, err := s.Harness().Call(ctx, "remote-cell", "test/loot_remote_drop", map[string]any{"covered": true})
	if err != nil {
		return err
	}
	s.Report()["drop"] = drop
	stack := na.AsString(drop["id"])
	if !boolean(drop["success"]) || stack == "" || na.AsNumber(drop["stock"]) <= 200 {
		return fmt.Errorf("covered loot fixture: %v", drop)
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
	err = na.WaitProgress(ctx, wait(service), func(ctx context.Context) (string, bool, error) {
		released := false
		if err := completedActions(ctx, journal, func(a domain.Action) {
			if allow, ok := a.SupplyAllow(); ok && !allow.Forbidden() && allow.Thing() == stack {
				released = true
			}
		}); err != nil {
			return "", false, err
		}
		if entry, ok, err := queueEntry(ctx, journal, stack); err != nil {
			return "", false, err
		} else if ok && entry.Status == policy.RecoveryHeld {
			return "", false, fmt.Errorf("covered loot held: %s", entry.Reason)
		}
		return "loot release", released, nil
	})
	if err != nil {
		return err
	}
	service.Stop()
	if err = reattach(ctx, s); err != nil {
		return err
	}
	for i := 0; i < 8; i++ {
		if _, err = s.Advance(ctx, 1000); err != nil {
			return err
		}
		audit, err := s.Harness().Call(ctx, fmt.Sprintf("loot-audit-%d", i), "test/loot_remote_drop", map[string]any{"audit": true})
		if err != nil {
			return err
		}
		s.Report()["audit"] = audit
		if boolean(audit["moved"]) && na.AsNumber(audit["delivered"]) > 0 {
			return nil
		}
	}
	return fmt.Errorf("covered loot not delivered within 8000 ticks")
}

// holdWords are the per-thing safety holds a hostile beside a ruin may produce:
// the census-wide threat and the route verdict past it.
var holdWords = []string{policy.RemoteHoldThreat, policy.RemoteHoldRouteUnsafe}

func runRecoveryHostile(ctx context.Context, s cases.Session) error {
	staged, err := prepareRemote(ctx, s, "")
	if err != nil {
		return err
	}
	target := na.AsString(staged["target"])
	threat, err := s.Harness().Call(ctx, "threat-spawn", "test/salvage_remote", map[string]any{"threat": "spawn"})
	if err != nil {
		return err
	}
	s.Report()["threat"] = threat
	if !boolean(threat["success"]) || !boolean(threat["threatPresent"]) {
		return fmt.Errorf("hostile fixture: %v", threat)
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
	err = na.WaitProgress(ctx, wait(service), func(ctx context.Context) (string, bool, error) {
		entry, ok, err := queueEntry(ctx, journal, target)
		if err != nil || !ok {
			return "waiting for the ruin's queue row", false, err
		}
		if entry.Status != policy.RecoveryHeld {
			return "", false, fmt.Errorf("hostile-adjacent ruin %s: %s", entry.Status, entry.Reason)
		}
		for _, word := range holdWords {
			if entry.Reason == word {
				s.Report()["hold"] = entry.Reason
				return "held", true, nil
			}
		}
		return "", false, fmt.Errorf("hostile-adjacent ruin held for %q, want one of %v", entry.Reason, holdWords)
	})
	if err != nil {
		return err
	}
	service.Stop()
	if err = reattach(ctx, s); err != nil {
		return err
	}
	if _, err = s.Advance(ctx, 1000); err != nil {
		return err
	}
	audit, err := s.Harness().Call(ctx, "hostile-audit", "test/salvage_remote", map[string]any{})
	if err != nil {
		return err
	}
	s.Report()["audit"] = audit
	if !boolean(audit["present"]) || na.AsNumber(audit["designations"]) != 0 || na.AsNumber(audit["delivered"]) != 0 {
		return fmt.Errorf("held ruin was worked: %v", audit)
	}
	return nil
}

// goBuiltID rebuilds a native thing id the way the Go planners do and
// requires it to equal the id native reported: the form RefIndex.Thing resolves.
func goBuiltID(native, def string) (string, error) {
	n, err := strconv.ParseUint(strings.TrimPrefix(native, "Thing_"+def), 10, 64)
	if err != nil {
		return "", fmt.Errorf("thing id %q is not Thing_%s<n>: %w", native, def, err)
	}
	if built := (policy.Thing{ID: n, Def: def}).LoadID(); built != native {
		return "", fmt.Errorf("go-built id %q differs from native %q", built, native)
	}
	return (policy.Thing{ID: n, Def: def}).LoadID(), nil
}

func runForeignIDs(ctx context.Context, s cases.Session) error {
	staged, err := prepareRemote(ctx, s, "probe")
	if err != nil {
		return err
	}
	ruin, err := goBuiltID(na.AsString(staged["target"]), "Battery")
	if err != nil {
		return err
	}
	plant, err := goBuiltID(na.AsString(staged["plant"]), "Plant_Bush")
	if err != nil {
		return err
	}
	cell, _ := na.AsMap(staged["plantCell"])
	if cell == nil {
		return fmt.Errorf("probe fixture reported no plant cell: %v", staged)
	}
	h := s.Harness()
	if _, err = h.Call(ctx, "initial-pause", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	clock := &na.ScenarioClock{Wire: h.WireFunc(), Identity: s.Identity(), Owner: "recovery-foreign-ids-acceptance", Report: s.Report()}
	if _, err = clock.Acquire(ctx, "acquire"); err != nil {
		return err
	}
	apply := func(key string, action map[string]any) error {
		action["key"] = key
		reply, err := h.Wire(ctx, key, "operations_apply", map[string]any{"identity": s.Identity(), "actions": []any{action}})
		if err != nil {
			return err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return fmt.Errorf("%s: expected one result: %#v", key, reply)
		}
		result, _ := na.AsMap(results[0])
		if _, ok := na.AsMap(result["applied"]); !ok {
			return fmt.Errorf("%s: %s did not resolve through native: %#v", key, ruin+" / "+plant, result)
		}
		return nil
	}
	if err = apply("foreign-claim", map[string]any{"buildingPatch": map[string]any{"thingId": ruin, "claim": map[string]any{}}}); err != nil {
		return err
	}
	if err = apply("foreign-cut", map[string]any{"designate": map[string]any{"designation": "THING_DESIGNATION_CUT_PLANT",
		"target": map[string]any{"id": plant}, "cell": map[string]any{"x": int(na.AsNumber(cell["x"])), "z": int(na.AsNumber(cell["z"]))}}}); err != nil {
		return err
	}
	audit, err := h.Call(ctx, "foreign-audit", "test/salvage_remote", map[string]any{})
	if err != nil {
		return err
	}
	s.Report()["audit"] = audit
	if !boolean(audit["claimed"]) || !boolean(audit["plantCut"]) {
		return fmt.Errorf("claim or cut did not take effect: %v", audit)
	}
	return nil
}
