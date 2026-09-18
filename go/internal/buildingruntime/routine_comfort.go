package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"strings"
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

// A definition needs one available pawn whose observed Construction setting
// is enabled and whose Construction skill meets the native minimum, in the
// same native observation bracket. The gate used to demand that the whole
// colony's settings match a Construction-only allocation, but the work
// planner applies a different allocation (bench and deficit work shift the
// owners), so the two only agreed by luck and a cooler could wait forever
// behind an unrelated pawn's Cooking checkbox (#66). This comparison never
// writes work settings.
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
		available = builderAvailable(pawns, overrides, int(minimum))
		if clockSchedulerDebug && !available {
			clockSchedulerLog("%s builder gate: no available pawn with Construction enabled at skill >= %d (%s)", definition, minimum, builderCensus(pawns))
		}
		return available
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

// builderAvailable reports whether some available pawn has Construction
// enabled in its observed settings, no player override disabling it and,
// when the definition needs one, a Construction skill at the native minimum.
// builderCensus is the diagnostic behind a "builder unavailable" wait
// (RIMGOVERNOR_CLOCK_DEBUG=1 only).
func builderAvailable(pawns []policy.WorkPawn, overrides []policy.WorkOverride, minimum int) bool {
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
		if minimum > 0 {
			skills, known := pawn.Skills.Value()
			if !known {
				continue
			}
			qualified := false
			for _, skill := range skills {
				if skill.Name == "Construction" && !skill.Disabled && skill.Level >= minimum {
					qualified = true
				}
			}
			if !qualified {
				continue
			}
		}
		for _, setting := range settings {
			if setting.Work == "Construction" && !setting.Disabled && setting.Priority > 0 {
				return true
			}
		}
	}
	return false
}

func builderCensus(pawns []policy.WorkPawn) string {
	var out []string
	for _, pawn := range pawns {
		level := -1
		if skills, known := pawn.Skills.Value(); known {
			for _, skill := range skills {
				if skill.Name == "Construction" {
					level = skill.Level
				}
			}
		}
		priority := -1
		if settings, known := pawn.Work.Value(); known {
			for _, setting := range settings {
				if setting.Work == "Construction" {
					priority = setting.Priority
				}
			}
		}
		out = append(out, fmt.Sprintf("%s available=%v applies=%v construction=%d priority=%d", pawn.ID, pawn.Available, pawn.Applies, level, priority))
	}
	return strings.Join(out, "; ")
}
