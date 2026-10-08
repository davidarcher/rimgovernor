package defense

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	// barnTimeout bounds the wait for the roamer's first barn write, paddockTimeout
	// the wait from the closed ring to the marker plan finishing.
	barnTimeout    = 60 * time.Minute
	paddockTimeout = 30 * time.Minute
	// paddockTicks bounds the native ticks the roamer takes to walk into the
	// pen once its area is cleared and the marker is built: three game days.
	paddockTicks = 3 * 60000
)

// defense/paddock is the perimeter campaign with a roamer in the colony (epic
// #2229, #2236): the core ring comes first and the roamer is held in the
// barn while it is open, the closed ring with its lane fenced lets the one
// pen marker claim the yard and the roamer graze in it, and the edge raid
// still funnels through the fenced killbox lane.
func init() {
	herd := &paddockHerd{}
	v := variant{strategy: "ImmediateAttack", arrival: "EdgeWalkIn", threat: "raid", herd: herd, layoutBuilt: herd.laneFenced}
	families := append(append([]routinefamily.Family{}, perimeterFamilies...), routinefamily.AnimalContainment, routinefamily.Husbandry, routinefamily.Sheltering)
	cases.Register(cases.Case{
		Name: "defense/paddock",
		Scope: "Paddock inside the defensive wall (#2229): with a roamer owned the core ring is built first and the roamer is kept in the barn (its allowed area, " +
			"no pen) while the ring is open; once the ring stands and the killbox lane is fenced one pen marker claims the yard, the roamer's area is cleared and it " +
			"is contained in the marker's pen; a real RaidEnemy edge assault still walks the fenced lane (traps sprung, hold formed) and the roamer survives. " +
			"Native: AnimalPenEnclosureCalculator, Fence pass-through and vanilla raid pathing.",
		Start: cases.Save{Name: sustained.BaselineSave}, Serve: &cases.ServeSpec{Families: families, Prefix: "defense-paddock"},
		Budget: 3 * time.Hour,
		Crew:   cases.Crew{Size: 3}, Reason: "the ring's build, the barn-bound roamer, the pen marker and the raid are one native campaign",
		Run: func(ctx context.Context, s cases.Session) error { return run(ctx, s, v) },
	})
}

// paddockHerd is defense/paddock's roamer: its phases hang off the perimeter
// campaign's hooks.
type paddockHerd struct {
	roamer string
}

// phaseEnv is what a paddock phase needs from the campaign run: the world and
// journal, and how to take the game back from, and hand it again to, a service.
type phaseEnv struct {
	ctx       context.Context
	world     store.World
	statePath string
	identity  map[string]any
	report    na.Report
	reopen    func() (*na.Harness, error)
	launch    func(name string) (*service, error)
}

// stage spawns the colony's roamer, a cow, on the construction site before the
// layout service starts.
func (p *paddockHerd) stage(ctx context.Context, h *na.Harness, x, z int, report na.Report) error {
	id, _, err := na.LabSpawn(ctx, h, na.LabThing{Def: "Cow", X: x, Z: z})
	if err != nil {
		return err
	}
	p.roamer = id
	report["roamer"] = id
	return nil
}

// ringClosed is the stored layout's yard closed: every core ring section and
// the killbox fence section stand (the plan-free reading of the planner's
// paddockClosed).
func ringClosed(record store.DefenseLayoutRecord) bool {
	core := 0
	for _, t := range record.Tiers {
		if t.Remove {
			continue
		}
		switch {
		case policy.IsCorePerimeterTier(t.Name):
			core++
		case strings.HasPrefix(string(t.Name), policy.TierFencePrefix):
		default:
			continue
		}
		if !t.Built {
			return false
		}
	}
	return core > 0
}

// laneFenced holds the built layout to a fenced killbox lane: a fence section
// stands, so the roamer's way out is shut while the raiders' lane is open.
func (p *paddockHerd) laneFenced(layout store.DefenseLayoutRecord, _ map[string]any) error {
	for _, t := range layout.Tiers {
		if !strings.HasPrefix(string(t.Name), policy.TierFencePrefix) || len(t.Buildings) == 0 {
			continue
		}
		if !t.Built {
			return fmt.Errorf("killbox fence section %s not built", t.Name)
		}
		for _, b := range t.Buildings {
			if b.Definition != "Fence" {
				return fmt.Errorf("killbox fence section %s holds %s, not a Fence", t.Name, b.Definition)
			}
		}
		return nil
	}
	return fmt.Errorf("the layout fences no killbox lane: %+v", layout.Tiers)
}

// areaWrites are the roamer's allowed-area writes in the order the husbandry
// planner committed them: the barn's area id, then "" once the paddock lets it
// out.
func (p *paddockHerd) areaWrites(ctx context.Context, st *store.Store) ([]string, error) {
	plans, err := st.PlanHistoryWithMethods(ctx, 128, "allowed_area-*")
	if err != nil {
		return nil, err
	}
	var writes []string
	for i := len(plans) - 1; i >= 0; i-- {
		for _, a := range plans[i].Spec.Actions() {
			if w, ok := a.Husbandry(); ok && w.Method() == domain.HusbandryAllowedArea && string(w.Animal()) == p.roamer {
				writes = append(writes, w.Argument())
			}
		}
	}
	return writes, nil
}

// roamerState reads the roamer's animal state from the colony facts.
func (p *paddockHerd) roamerState(ctx context.Context, h *na.Harness, label string, identity map[string]any) (map[string]any, error) {
	reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
	if err != nil {
		return nil, err
	}
	_, snapshot, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	section, _ := na.AsMap(snapshot["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return nil, fmt.Errorf("%s: upkeep section unavailable: %w", label, err)
	}
	for _, raw := range na.AsSlice(upkeep["animals"]) {
		row, _ := na.AsMap(raw)
		if na.PawnRef(row) != p.roamer {
			continue
		}
		if err := h.JoinPawn(ctx, label, identity, row); err != nil {
			return nil, err
		}
		pawn, _ := na.AsMap(row["pawn"])
		state, _ := na.AsMap(pawn["animalState"])
		return state, nil
	}
	return nil, fmt.Errorf("%s: roamer %s not in the animal census", label, p.roamer)
}

// barnBound runs the layout service until the husbandry planner writes the
// roamer's barn area while the ring is still open, then stops it and reads the
// game: the roamer holds the barn area, is in no pen, and the stored ring is
// open. A fresh service continues the build.
func (p *paddockHerd) barnBound(e phaseEnv, svc *service) (*service, *na.Harness, error) {
	if p.roamer == "" {
		return nil, nil, fmt.Errorf("no roamer staged (a resumed run skips the staging)")
	}
	barn := ""
	err := na.WaitProgress(e.ctx, svc.wait(barnTimeout), func(ctx context.Context) (string, bool, error) {
		writes, err := p.areaWrites(ctx, svc.store)
		if err != nil {
			return "", false, err
		}
		for _, w := range writes {
			if w != "" {
				barn = w
				return "", true, nil
			}
		}
		record, ok, err := svc.store.LoadDefenseLayout(ctx, e.world)
		if err != nil {
			return "", false, err
		}
		if ok && ringClosed(record) {
			return "", false, fmt.Errorf("the ring closed before the roamer was written into the barn")
		}
		review, err := svc.store.LoadRounds(ctx)
		if err != nil {
			return "", false, err
		}
		return na.Signature(len(writes), review.Tick), false, nil
	})
	if err != nil {
		return nil, nil, fmt.Errorf("no barn write for the roamer: %w", err)
	}
	svc.stop()
	e.report["barn_area"] = barn
	h, err := e.reopen()
	if err != nil {
		return nil, nil, err
	}
	stored, err := store.Open(e.ctx, e.statePath)
	if err != nil {
		return nil, nil, err
	}
	record, ok, err := stored.LoadDefenseLayout(e.ctx, e.world)
	if closeErr := stored.Close(); err == nil {
		err = closeErr
	}
	if err != nil {
		return nil, nil, err
	}
	if ok && ringClosed(record) {
		return nil, nil, fmt.Errorf("the ring was closed when the roamer was barn-bound: %+v", record.Tiers)
	}
	state, err := p.roamerState(e.ctx, h, "roamer-barn", e.identity)
	if err != nil {
		return nil, nil, err
	}
	if got := na.AsString(state["allowedAreaId"]); got != barn {
		return nil, nil, fmt.Errorf("roamer holds area %q, want the barn %q", got, barn)
	}
	if penID := na.AsString(state["penId"]); penID != "" {
		return nil, nil, fmt.Errorf("roamer is in pen %q while the ring is open", penID)
	}
	e.report["roamer_barn_bound"] = true
	next, err := e.launch("barn")
	return next, h, err
}

// awaitPaddock keeps the layout service running past the layout's completion
// until the roamer's barn area is cleared and the one pen marker's plan has
// finished: the closed ring with its lane fenced is the paddock.
func (p *paddockHerd) awaitPaddock(e phaseEnv, svc *service) error {
	markerSeen := false
	err := na.WaitProgress(e.ctx, svc.wait(paddockTimeout), func(ctx context.Context) (string, bool, error) {
		writes, err := p.areaWrites(ctx, svc.store)
		if err != nil {
			return "", false, err
		}
		cleared := len(writes) > 0 && writes[len(writes)-1] == ""
		plans, err := svc.store.LoadPlans(ctx)
		if err != nil {
			return "", false, err
		}
		open := false
		for _, plan := range plans {
			for _, a := range plan.Spec.Actions() {
				if b, ok := a.Building(); ok && b.Definition() == policy.PenMarkerDefinition {
					markerSeen, open = true, open || store.PlanOpen(plan)
				}
			}
		}
		review, err := svc.store.LoadRounds(ctx)
		if err != nil {
			return "", false, err
		}
		if cleared && markerSeen && !open {
			return "", true, nil
		}
		return na.Signature(len(writes), cleared, markerSeen, open, review.Tick), false, nil
	})
	if err != nil {
		return fmt.Errorf("the paddock was not claimed (marker plan seen: %v): %w", markerSeen, err)
	}
	e.report["paddock_marker_planned"] = true
	return nil
}

// paddocked runs the game natively until the roamer, its area cleared, is
// contained in the marker's pen.
func (p *paddockHerd) paddocked(e phaseEnv, h *na.Harness) error {
	var state map[string]any
	if _, err := na.RunUntil(e.ctx, h, "observe-paddock", paddockTicks, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		row, err := p.roamerState(ctx, h, "paddock-poll", e.identity)
		if err != nil {
			return "", false, err
		}
		state = row
		contained, _ := na.AsBool(row["contained"])
		return "", contained && na.AsString(row["penId"]) != "" && na.AsString(row["allowedAreaId"]) == "", nil
	}); err != nil {
		return fmt.Errorf("roamer did not graze in the pen (last state %#v): %w", state, err)
	}
	e.report["roamer_pen_id"] = na.AsString(state["penId"])
	return nil
}

// survived holds the roamer to the census after the raid: it is alive.
func (p *paddockHerd) survived(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) error {
	if _, err := p.roamerState(ctx, h, "roamer-after-raid", identity); err != nil {
		return err
	}
	report["roamer_survived_raid"] = true
	return nil
}
