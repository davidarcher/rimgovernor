// Package gear is the gear/* acceptance area (#472, the acceptance of the
// gear planner epic #416): winter lookahead, tainted apparel under role
// policies, the soldier armor and weapon fit, and the twelve-pawn roster
// wave. Every case opens on the tribal baseline staged by
// test/gear_area_prepare (GearAreaFixture.cs), watches MaintainEquipment
// under a serve run composed of the families it needs, and audits the
// journal against the live census test/gear_area_probe reads.
// production/apparel keeps the single-shirt-from-leather path.
package gear

import (
	"context"
	"fmt"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	prepareOp = "test/gear_area_prepare"
	probeOp   = "test/gear_area_probe"
	// dayTicks is one in-game day.
	dayTicks uint64 = 60000
	// watch is the wall-clock ceiling of every case's window; the tick
	// window ends it sooner on a game that keeps pace.
	watch = 10 * time.Minute
)

// Families per case: gear plans the policies, bills and wear orders; work
// enables the bench work types; workshop stages a missing bench; equip
// runs the weapon wave.
const (
	dressingFamilies  = "work,gear"
	tailoringFamilies = "work,workshop,gear"
	soldierFamilies   = "work,workshop,gear,equip"
)

// start is the case's Start: the fixture mode over the tribal baseline.
func start(mode string, args map[string]any) cases.Fixture {
	all := map[string]any{"mode": mode}
	for k, v := range args {
		all[k] = v
	}
	return cases.Fixture{Op: prepareOp, Args: all, On: cases.Save{Name: sustained.BaselineSave}}
}

func serve(families, prefix string) *cases.ServeSpec {
	return &cases.ServeSpec{Families: []string{families}, NativeTimeout: 15 * time.Second, Prefix: prefix}
}

// recovered reports a sample whose MaintainEquipment goal is satisfied
// with its need recovered.
func recovered(sample map[string]any) bool {
	need, _ := sample["need"].(string)
	status, _ := sample["status"].(string)
	return domain.NeedState(need) == domain.NeedRecovered && domain.GoalStatus(status) == domain.GoalSatisfied
}

// firstRecoveredTick is the review tick of the first recovered sample, ok
// false when none recovered.
func firstRecoveredTick(timeline []map[string]any) (uint64, bool) {
	for _, sample := range timeline {
		if recovered(sample) {
			return asTick(sample["review_tick"]), true
		}
	}
	return 0, false
}

// asTick reads a tick the sample or a native reply carries, whichever
// integer type the JSON or the journal gave it.
func asTick(v any) uint64 {
	switch n := v.(type) {
	case uint64:
		return n
	case int64:
		return uint64(n)
	case int:
		return uint64(n)
	case float64:
		return uint64(n)
	}
	return 0
}

// worn is one garment the probe reports on a colonist.
type worn struct {
	ThingID, Definition, Stuff, Slot string
	Tainted                          bool
	HitPoints, MaxHitPoints          float64
}

// colonist is one row of the probe census.
type colonist struct {
	Pawn, Name, Policy, PolicyID            string
	Shooting                                int
	Ambient, ComfortableMin, ComfortableMax float64
	Hypothermia, Heatstroke                 float64
	PrimaryID, PrimaryDefinition            string
	Worn                                    []worn
}

func (c colonist) wears(definitions ...string) bool {
	for _, w := range c.Worn {
		for _, d := range definitions {
			if w.Definition == d {
				return true
			}
		}
	}
	return false
}

// census is the probe's reply: the colonists and the slot key of every
// definition the call asked about.
type census struct {
	Tick      uint64
	Colonists []colonist
	DefSlots  map[string]string
	Raw       map[string]any
}

// probe reads the live census through test/gear_area_probe; defs names the
// apparel definitions whose slot key the reply should carry.
func probe(ctx context.Context, h *na.Harness, label string, defs []string) (census, error) {
	reply, err := h.Call(ctx, label, probeOp, map[string]any{"defs": strings.Join(defs, ",")})
	if err != nil {
		return census{}, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return census{}, fmt.Errorf("%s refused: %v", probeOp, reply["reason"])
	}
	out := census{Tick: asTick(reply["tick"]), DefSlots: map[string]string{}, Raw: reply}
	if slots, ok := na.AsMap(reply["defSlots"]); ok {
		for k, v := range slots {
			out.DefSlots[k] = na.AsString(v)
		}
	}
	for _, raw := range na.AsSlice(reply["colonists"]) {
		row, _ := na.AsMap(raw)
		c := colonist{Pawn: na.AsString(row["pawn"]), Name: na.AsString(row["name"]), Policy: na.AsString(row["policy"]), PolicyID: na.AsString(row["policyId"]),
			Shooting: int(na.AsNumber(row["shooting"])), Ambient: na.AsNumber(row["ambient"]), ComfortableMin: na.AsNumber(row["comfortableMin"]), ComfortableMax: na.AsNumber(row["comfortableMax"]),
			Hypothermia: na.AsNumber(row["hypothermia"]), Heatstroke: na.AsNumber(row["heatstroke"])}
		if primary, ok := na.AsMap(row["primary"]); ok {
			c.PrimaryID, c.PrimaryDefinition = na.AsString(primary["thingId"]), na.AsString(primary["defName"])
		}
		for _, item := range na.AsSlice(row["worn"]) {
			w, _ := na.AsMap(item)
			tainted, _ := na.AsBool(w["tainted"])
			c.Worn = append(c.Worn, worn{ThingID: na.AsString(w["thingId"]), Definition: na.AsString(w["defName"]), Stuff: na.AsString(w["stuff"]), Slot: na.AsString(w["slot"]),
				Tainted: tainted, HitPoints: na.AsNumber(w["hitPoints"]), MaxHitPoints: na.AsNumber(w["maxHitPoints"])})
		}
		out.Colonists = append(out.Colonists, c)
	}
	if len(out.Colonists) == 0 {
		return out, fmt.Errorf("%s reported no colonists", probeOp)
	}
	return out, nil
}

// method is one action MaintainEquipment planned this epoch, as the
// journal holds it: the kind, its stage, and for wear and equip orders the
// pawn and the definition ordered.
type method struct {
	Kind             domain.ActionKind
	Stage            domain.Stage
	Pawn, Definition string
}

func (m method) completed() bool { return m.Stage == domain.Completed }

// equipmentMethods loads every action of every MaintainEquipment method in
// the goal's current epoch, active or retired.
func equipmentMethods(ctx context.Context, output string) ([]method, map[string]any, error) {
	journal, err := store.Open(ctx, filepath.Join(output, "service.sqlite"))
	if err != nil {
		return nil, nil, fmt.Errorf("reopen journal: %w", err)
	}
	defer journal.Close()
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return nil, nil, fmt.Errorf("load routine review: %w", err)
	}
	var goalID domain.GoalID
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainEquipment {
			goalID = binding.Goal
		}
	}
	if goalID == "" {
		return nil, nil, fmt.Errorf("MaintainEquipment was never bound in the routine review")
	}
	goal, err := journal.LoadGoal(ctx, goalID)
	if err != nil {
		return nil, nil, err
	}
	history, err := journal.LoadGoalMethods(ctx, goalID, goal.Goal.Epoch)
	if err != nil {
		return nil, nil, err
	}
	var methods []method
	kinds := map[string]int{}
	for _, m := range history {
		plan, err := journal.LoadPlan(ctx, m.Plan)
		if err != nil {
			continue
		}
		for _, p := range plan.Progress {
			action := p.Action()
			row := method{Kind: action.Kind(), Stage: p.View().Stage}
			if g, ok := action.GearReplace(); ok {
				row.Pawn, row.Definition = string(g.Pawn()), g.Definition()
			} else if e, ok := action.Equip(); ok {
				row.Pawn, row.Definition = string(e.Pawn()), e.Definition()
			} else if b, ok := action.ProductionBill(); ok {
				row.Definition = b.Recipe()
			}
			methods = append(methods, row)
			kinds[string(row.Kind)+"/"+string(row.Stage)]++
		}
	}
	summary := map[string]any{"status": string(goal.Goal.Status), "need": string(goal.Goal.Need), "epoch": goal.Goal.Epoch, "methods": len(history), "actions": kinds}
	return methods, summary, nil
}

// observe runs the case's serve window over MaintainEquipment: Prepare
// records the fixture reply and a baseline census, the window ends on
// recovery or after window ticks, and audit judges the timeline against
// the journal and a final census.
func observe(ctx context.Context, s cases.Session, window uint64, defs []string, audit func(ctx context.Context, h *na.Harness, report na.Report, timeline []map[string]any, final census) error) error {
	report := s.Report()
	report["fixture"] = s.Prepared()
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: watch, Window: window, Goal: policy.MaintainEquipment, Until: recovered},
		Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
			baseline, err := probe(ctx, h, "baseline-census", defs)
			report["baseline_census"] = baseline.Raw
			return err
		},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			final, err := probe(ctx, h, "final-census", defs)
			report["final_census"] = final.Raw
			if err != nil {
				return err
			}
			// Watch leaves the sampled timeline on the report before Audit.
			timeline, _ := report["timeline"].([]map[string]any)
			return audit(ctx, h, report, timeline, final)
		},
	})
	return err
}
