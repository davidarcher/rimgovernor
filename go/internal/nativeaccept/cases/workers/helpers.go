package workers

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// workers/helpers (#653): a skilled builder and two colonists under the
// Construction floor beside six wood wall blueprints. Two reviews' worth of
// planning (the first sees spare capacity, the second assigns) enables both
// sub-floor pawns at 4 beside the builder's 1; the matrix goes through
// native PatchPawn, the builder is drafted, and the case passes only when
// a helper's native ThingsConstructed record rises with a wall finished.
func init() {
	cases.Register(cases.Case{
		Name: "workers/helpers",
		Scope: "Construction helpers (#653): idle colonists under the Construction floor are enabled at 4 while " +
			"six wood walls are ready, the skilled builder stays at 1, and with the builder drafted a helper builds a wall natively.",
		Start:       cases.DebugStart{},
		RequiredOps: []string{"test/workers_setup", "test/workers_helpers"},
		Budget:      6 * time.Minute,
		Run:         runHelpers,
	})
}

const helperWallTicks = 250

func runHelpers(ctx context.Context, s cases.Session) error {
	report := s.Report()
	if !na.Contains(s.Names(), "rimgovernor/operations_execute") {
		return fmt.Errorf("missing rimgovernor/operations_execute in discovery")
	}
	fixture, err := seed(ctx, s, "helpers")
	if err != nil {
		return err
	}
	builder, a, b := policy.PawnID(fixture.IDs[0]), policy.PawnID(fixture.IDs[1]), policy.PawnID(fixture.IDs[2])
	for _, id := range fixture.IDs {
		if hasDisabled(fixture, id, policy.WorkConstruction) {
			return fmt.Errorf("setup: fixture pawn %s cannot build: %v", id, fixture.Disabled)
		}
	}
	pawns, err := readPawns(ctx, s, "before", fixture.IDs)
	if err != nil {
		return err
	}
	// The ready work the routine review would record for six open wall
	// actions: each runnable, one worker each.
	ready := &policy.ReadyWorkReport{}
	snapshot := domain.GenerationSnapshot{}
	for i := 0; i < 6; i++ {
		ready.Candidates = append(ready.Candidates, policy.ReadyWork{Stage: "building:Wall", Work: policy.LaborProfile{policy.WorkConstruction}, State: policy.ReadyRunnable, Parallelism: 1, Adapter: policy.ReadyMigrated, Claims: []policy.ReadyClaim{{Kind: "cell", Key: fmt.Sprint(i)}}})
	}
	plan := func(label string, tick domain.Tick, previous *policy.ConstructionHelpRecord) (policy.WorkDecision, error) {
		help := policy.ConstructionHelpDemand(ready, snapshot, tick, []string{"Wall"}, previous)
		d, err := policy.PlanWork(pawns, nil, nil, policy.WorkDemand{Construction: true, Help: &help})
		if err == nil && d.Help == nil {
			err = fmt.Errorf("%s: no helper record", label)
		}
		report["plan_"+label] = d
		return d, err
	}
	first, err := plan("first", 1, nil)
	if err != nil {
		return err
	}
	decision, err := plan("second", 2, first.Help)
	if err != nil {
		return err
	}
	if priority(decision, builder, policy.WorkConstruction) != 1 || priority(decision, a, policy.WorkConstruction) != 4 || priority(decision, b, policy.WorkConstruction) != 4 {
		return fmt.Errorf("plan: want builder 1 and helpers 4, got %+v (help %+v, first %+v)", decision.Assignments, decision.Help, first.Help)
	}
	if _, err := na.GrantAuto(ctx, s.Harness().WireFunc(), "acquire", s.Identity()); err != nil {
		return err
	}
	if _, err := dispatch(ctx, s, "write", pawns, decision, nil); err != nil {
		return err
	}
	if _, err := probeHelpers(ctx, s, "occupy", string(builder)); err != nil {
		return err
	}
	base, err := probeHelpers(ctx, s, "read", "")
	if err != nil {
		return err
	}
	report["helpers_before"] = base
	for step := 0; step < 40; step++ {
		if _, err := s.Advance(ctx, helperWallTicks); err != nil {
			return err
		}
		now, err := probeHelpers(ctx, s, "read", "")
		if err != nil {
			return err
		}
		report["helpers_after"] = now
		built := now.walls > base.walls
		helped := now.constructed[string(a)] > base.constructed[string(a)] || now.constructed[string(b)] > base.constructed[string(b)]
		if now.constructed[string(builder)] > base.constructed[string(builder)] {
			return fmt.Errorf("the drafted builder constructed: %+v", now)
		}
		if built && helped {
			return nil
		}
	}
	return fmt.Errorf("no helper finished a wall in %d ticks", 40*helperWallTicks)
}

type helperProbe struct {
	walls       int
	constructed map[string]float64
}

func probeHelpers(ctx context.Context, s cases.Session, action, pawn string) (helperProbe, error) {
	reply, err := s.Harness().Call(ctx, "helpers-"+action, "test/workers_helpers", map[string]any{"action": action, "pawn": pawn})
	if err != nil {
		return helperProbe{}, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return helperProbe{}, fmt.Errorf("helpers %s refused: %#v", action, reply)
	}
	walls, _ := reply["walls"].(float64)
	out := helperProbe{walls: int(walls), constructed: map[string]float64{}}
	for _, raw := range na.AsSlice(reply["pawns"]) {
		row, _ := na.AsMap(raw)
		v, _ := row["constructed"].(float64)
		out.constructed[na.AsString(row["id"])] = v
	}
	return out, nil
}
