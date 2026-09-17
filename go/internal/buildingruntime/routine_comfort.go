package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const BuildingComfortWait RoutineBuildingReason = "waiting_for_native_comfort_use"

// Completed methods leave the active catalog but retain their bounded use budget.
// Look up only this goal epoch's known comfort methods; old epochs cannot lend time.
func comfortUseAllowance(ctx context.Context, journal *store.Store, goal domain.Goal, current domain.GenerationSnapshot, tick domain.Tick) (uint32, error) {
	var ticks uint32
	for _, definition := range []string{"Table1x2c", "DiningChair", "HorseshoesPin"} {
		method, err := journal.LoadGoalMethod(ctx, goal.ID, goal.Epoch, domain.MethodID("comfort-"+definition))
		if errors.Is(err, store.ErrNotFound) {
			continue
		}
		if err != nil {
			return 0, err
		}
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return 0, err
		}
		ticks = max(ticks, comfortNativeWorkTicks(plan, current, tick))
	}
	return ticks, nil
}

// NewRoutineComfortPlanner furnishes a room whose native role can host
// dining or recreation; when no such room exists it stages a starter shell
// the same way shelter does, and furnishes it once the census reports it as
// a proper room. The typed room census is read in the same bracket.
func NewRoutineComfortPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	if _, ok := native.(observation.TemperatureSource); !ok {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.EnsureComfort, definition: "Wall", shelter: true}, nil
}

// Skilled furniture must have a qualified, assigned builder in the same native
// observation bracket. Unskilled furniture (native construction minimum 0,
// such as a crafting spot) only needs one available pawn whose observed
// Construction setting is enabled: waiting for the whole colony's settings to
// match the allocator would hold a bench behind an unrelated pawn whose
// settings cannot be applied (a mental break, an unobservable pawn). This
// comparison never writes work settings.
func comfortBuilderAvailable(facts observation.ColonyProjection, definition string, overrides []policy.WorkOverride) bool {
	for _, d := range facts.Definitions {
		if d.Name != definition {
			continue
		}
		available, known := d.Available.Value()
		minimum, skillKnown := d.ConstructionSkill.Value()
		if !known || !available || !skillKnown || minimum < 0 {
			return false
		}
		pawns, known := facts.WorkPawns.Value()
		if !known {
			return false
		}
		if minimum == 0 {
			return unskilledBuilderAvailable(pawns, overrides)
		}
		decision, err := policy.AssignWork(pawns, []policy.WorkRequirement{{Work: "Construction", Skill: "Construction", Minimum: int(minimum)}}, overrides)
		if clockSchedulerDebug {
			clockSchedulerLog("%s builder gate: err=%v capacity=%v matches=%v mismatches=%v", definition, err, decision.Capacity, decision.Matches, workMismatches(pawns, decision))
		}
		return err == nil && decision.Capacity == domain.Known(true) && decision.Matches == domain.Known(true)
	}
	return false
}

func (r *RoutineBuildingPlanner) selectComfort(facts observation.ColonyProjection, history policy.ComfortHistory) (*RoutineBuildingPlanner, RoutineBuildingReason, error) {
	v, known := facts.Facts.Comfort.Value()
	if !known {
		return nil, BuildingMethodUnknown, nil
	}
	review, err := policy.ReviewComfort(facts.Facts.Comfort, history, facts.Identity.Tick)
	if err != nil {
		return nil, "", err
	}
	method, err := policy.SelectComfortMethod(v, review)
	if err != nil {
		return nil, "", err
	}
	switch method {
	case policy.ComfortNoMethod:
		return nil, BuildingMethodNoDeficit, nil
	case policy.ComfortWait:
		return nil, BuildingComfortWait, nil
	case policy.ComfortAccessBlocked:
		return nil, BuildingExistingFacility, nil
	}
	resolved := *r
	resolved.definition = string(method)
	resolved.environment = policy.PlacementIndoors
	role := policy.RoomRoleDiningRoom
	if method == policy.ComfortBuildRecreation {
		role = policy.RoomRoleRecRoom
	}
	facility, err := policy.Facility(role)
	if err != nil {
		return nil, "", err
	}
	resolved.facility = &facility
	if method == policy.ComfortBuildChair {
		for _, s := range v.Surfaces {
			resolved.adjacent = append(resolved.adjacent, s.Adjacent...)
		}
	}
	for _, d := range facts.Definitions {
		if d.Name == resolved.definition {
			if stuff, known := d.Stuff.Value(); known {
				resolved.stuff = stuff
			}
		}
	}
	return &resolved, "", nil
}

// Allow a finite interval for ordinary dining/recreation after this direction
// completed a comfort facility. Repeated observations cannot renew the budget.
func comfortNativeWorkTicks(plan store.PlanState, current domain.GenerationSnapshot, tick domain.Tick) uint32 {
	if len(plan.Progress) != 1 {
		return 0
	}
	p := plan.Progress[0]
	b, ok := p.Action().Building()
	if !ok || b.Definition() != "Table1x2c" && b.Definition() != "DiningChair" && b.Definition() != "HorseshoesPin" {
		return 0
	}
	current.Plan, current.Revision = plan.Spec.ID(), plan.Spec.Revision()
	v := p.View()
	effect, known := v.Effect.Value()
	if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted || !v.Snapshot.Matches(current) || tick < v.Tick || tick-v.Tick >= 10000 {
		return 0
	}
	// Dining jobs can finish inside a normal construction window. Observe use
	// frequently while preserving the original, non-renewable completion deadline.
	return min(uint32(120), uint32(10000-(tick-v.Tick)))
}

// workMismatches lists pawn/work pairs whose observed enablement differs from
// the allocator's decision, the diagnostic behind a "builder unavailable"
// wait (RIMGOVERNOR_CLOCK_DEBUG=1 only).
// unskilledBuilderAvailable reports whether some available pawn has
// Construction enabled in its observed settings and no player override
// disabling it. Skill levels are irrelevant: the definition needs none.
func unskilledBuilderAvailable(pawns []policy.WorkPawn, overrides []policy.WorkOverride) bool {
	for _, pawn := range pawns {
		if pawn.Available != domain.Known(true) || pawn.Applies != domain.Known(true) {
			continue
		}
		disabled := false
		for _, override := range overrides {
			if override.Pawn == pawn.ID && override.Work == "Construction" && override.Priority == 0 {
				disabled = true
			}
		}
		if disabled {
			continue
		}
		settings, known := pawn.Work.Value()
		if !known {
			continue
		}
		for _, setting := range settings {
			if setting.Work == "Construction" && !setting.Disabled && setting.Priority > 0 {
				return true
			}
		}
	}
	return false
}

func workMismatches(pawns []policy.WorkPawn, decision policy.WorkDecision) []string {
	observed := map[policy.PawnID]map[policy.WorkType]policy.WorkPriority{}
	for _, pawn := range pawns {
		settings, _ := pawn.Work.Value()
		observed[pawn.ID] = map[policy.WorkType]policy.WorkPriority{}
		for _, setting := range settings {
			observed[pawn.ID][setting.Work] = setting
		}
	}
	var out []string
	for _, assignment := range decision.Assignments {
		for _, want := range assignment.Priorities {
			if have, ok := observed[assignment.Pawn][want.Work]; ok && (have.Priority > 0) != (want.Priority > 0) {
				out = append(out, fmt.Sprintf("%s/%s observed=%d wanted=%d", assignment.Pawn, want.Work, have.Priority, want.Priority))
			}
		}
	}
	return out
}
