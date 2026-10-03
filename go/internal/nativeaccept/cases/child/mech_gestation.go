// The mech/gestation case (#1692, epic #1667) proves a mechanitor gestates a
// mech inside its bandwidth and the controller gives it a work order: the
// MaintainMechs goal (#1686) queues a gestation bill on the gestator, the game
// forms the mech, and the routine's mech control (#1687) sets the mech's
// control group to the mode its kind calls for.
package child

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

const (
	mechPrepareTool = "test/mech_gestation_prepare"
	mechInspectTool = "test/mech_gestation_inspect"
	// mechCeiling bounds each journal wait; the stall budget, whose
	// signature carries the game day, ends it earlier when nothing moves.
	mechCeiling = 12 * time.Minute
)

func init() {
	cases.Register(cases.Case{
		Name: "child/mech-gestation",
		Scope: "Issue #1692: a mechanitor with a powered gestator is owed a mech by MaintainMechs, which bills the gestator " +
			"inside the mechanitor's free bandwidth; the game hauls the ingredients and forms the mech, and the routine's mech " +
			"control gives its control group the work mode its kind calls for. A Go snapshot over recorded facts cannot cover " +
			"it: the chain is vanilla's own (the Bill_Mech bandwidth rule, hauling, gestation cycles under power, the mech's " +
			"birth into an overseer's control group), asserted on the live mechanitor and mech.",
		Start:       cases.Fixture{Op: mechPrepareTool, On: cases.LabStart()},
		Expansions:  []string{"ludeon.rimworld.biotech"},
		NoKeep:      true,
		Quiet:       na.QuietRequired,
		RequiredOps: []string{mechPrepareTool, mechInspectTool},
		// mechs bills the gestator and, with work, runs the mech control; the
		// shelter family keeps supervised windows running and dialog answers
		// the letters (the birth raises one).
		Serve: &cases.ServeSpec{
			Families: []string{"mechs", "work", "shelter", "dialog"}, NativeTimeout: 15 * time.Second, Prefix: "child-mech-gestation",
		},
		Budget: 25 * time.Minute,
		Run:    runMechGestation,
	})
}

// mechBillPlaced reports whether the journal holds a completed bill on the
// gestator.
func mechBillPlaced(ctx context.Context, st *store.Store, gestator string) (bool, error) {
	plans, err := st.PlanHistoryWithMethods(ctx, 256, "bill-*")
	if err != nil {
		return false, err
	}
	for _, p := range plans {
		for _, progress := range p.Progress {
			bill, ok := progress.Action().ProductionBill()
			if ok && bill.Bench() == gestator && progress.View().Stage == domain.Completed {
				return true, nil
			}
		}
	}
	return false, nil
}

// mechModeWritten reports whether the journal holds a completed mech work
// mode setting.
func mechModeWritten(ctx context.Context, st *store.Store) (bool, error) {
	plans, err := st.PlanHistoryWithMethods(ctx, 256, "work-*")
	if err != nil {
		return false, err
	}
	for _, p := range plans {
		for _, progress := range p.Progress {
			settings, ok := progress.Action().PawnSettings()
			if !ok {
				continue
			}
			if _, mode := settings.MechWorkMode(); mode && progress.View().Stage == domain.Completed {
				return true, nil
			}
		}
	}
	return false, nil
}

func runMechGestation(ctx context.Context, s cases.Session) error {
	report, prepared := s.Report(), s.Prepared()
	mechanitor, gestator := na.AsString(prepared["mechanitorId"]), na.AsString(prepared["gestatorId"])
	free := na.AsNumber(prepared["freeBandwidth"])
	if mechanitor == "" || gestator == "" || free <= 0 || len(na.AsSlice(prepared["recipes"])) == 0 {
		return fmt.Errorf("fixture: unexpected mech gestation staging: %#v", prepared)
	}
	report["mechanitor"], report["gestator"], report["fixture"] = mechanitor, gestator, prepared

	if _, err := na.ConfirmColonyNames(ctx, s.Harness(), report); err != nil {
		return err
	}
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	if _, err := service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	st, err := service.Store(ctx)
	if err != nil {
		return err
	}
	defer st.Close()

	// until polls the journal; the signature carries the game day so a
	// long gestation does not read as a stall and a broken phase does.
	until := func(what string, done func(ctx context.Context) (bool, error)) error {
		err := na.WaitProgress(ctx, na.Wait{Ceiling: mechCeiling, Stall: na.StallBudget(), Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
			review, err := st.LoadRoutineReview(ctx)
			if err != nil {
				return na.Signature("no-review"), false, nil
			}
			ok, err := done(ctx)
			return na.Signature(ok, review.Tick/na.TicksPerDay), ok, err
		})
		if err != nil {
			return fmt.Errorf("%s: %w", what, err)
		}
		return nil
	}
	// The colony's answer to a mechanitor with bandwidth to spare: a
	// gestation bill on its gestator, then the work order for the mech.
	if err := until("gestation bill", func(ctx context.Context) (bool, error) { return mechBillPlaced(ctx, st, gestator) }); err != nil {
		return err
	}
	if err := until("mech work mode", func(ctx context.Context) (bool, error) { return mechModeWritten(ctx, st) }); err != nil {
		return err
	}
	report["run_keepalive"] = service.Stop()

	// Native postconditions after the service releases the game slot.
	h, err := s.Reattach(ctx)
	if err != nil {
		return fmt.Errorf("reopen session after service stop: %w", err)
	}
	reply, err := h.Call(ctx, "mech-inspect", mechInspectTool, map[string]any{"mechanitorId": mechanitor, "gestatorId": gestator})
	if err != nil {
		return err
	}
	report["inspect"] = reply
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return fmt.Errorf("mech inspect refused: %v", reply)
	}
	if dead, _ := na.AsBool(reply["mechanitorDead"]); dead {
		return fmt.Errorf("the mechanitor died: %v", reply)
	}
	mechs := na.AsSlice(reply["mechs"])
	if len(mechs) == 0 {
		return fmt.Errorf("the mechanitor controls no mech: %v", reply)
	}
	var cost float64
	for _, raw := range mechs {
		mech, _ := na.AsMap(raw)
		if dead, _ := na.AsBool(mech["dead"]); dead {
			return fmt.Errorf("mech %v is dead", mech["id"])
		}
		if spawned, _ := na.AsBool(mech["spawned"]); !spawned {
			return fmt.Errorf("mech %v is not on the map", mech["id"])
		}
		if na.AsString(mech["overseerId"]) != mechanitor {
			return fmt.Errorf("mech %v is overseen by %v, not the mechanitor %s", mech["id"], mech["overseerId"], mechanitor)
		}
		// A work mech's group works, any other kind's escorts (policy.PlanMechControl).
		want := policy.MechModeEscort
		if work, _ := na.AsBool(mech["workMech"]); work {
			want = policy.MechModeWork
		}
		if got := na.AsString(mech["mode"]); got != want {
			return fmt.Errorf("mech %v (%v) runs mode %q in group %v, want %q", mech["id"], mech["kind"], got, mech["controlGroup"], want)
		}
		cost += na.AsNumber(mech["bandwidthCost"])
	}
	total, used, gestating := na.AsNumber(reply["totalBandwidth"]), na.AsNumber(reply["usedBandwidth"]), na.AsNumber(reply["usedBandwidthFromGestation"])
	report["bandwidth"] = map[string]any{"total": total, "used": used, "gestation": gestating, "mechCost": cost}
	if used+gestating > total {
		return fmt.Errorf("used bandwidth %v plus gestation %v exceeds the total %v", used, gestating, total)
	}
	if used < cost {
		return fmt.Errorf("used bandwidth %v is below the controlled mechs' cost %v", used, cost)
	}
	return nil
}
