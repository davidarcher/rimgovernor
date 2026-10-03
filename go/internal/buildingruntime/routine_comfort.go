package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Completed methods leave the active catalog but retain their bounded use budget.
// Look up only this goal epoch's known comfort methods; old epochs cannot lend time.
func comfortUseAllowance(ctx context.Context, journal *store.Store, goal domain.Goal, current domain.GenerationSnapshot, tick domain.Tick, foothold string) (uint32, error) {
	var ticks uint32
	for _, definition := range comfortDefinitions(foothold) {
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
		ticks = max(ticks, comfortNativeWorkTicks(plan, current, tick, foothold))
	}
	return ticks, nil
}

// comfortDefinitions are the furniture the comfort goals build: the table,
// the chair and the recreation foothold the catalog rows name.
func comfortDefinitions(foothold string) []string {
	return []string{"Table1x2c", "DiningChair", foothold}
}

// NewRoutineComfortPlanner furnishes a room whose native role can host
// dining or recreation; when no such room exists it stages a starter shell
// the same way shelter does, and furnishes it once the census reports it as
// a proper room. The typed room census is read in the same bracket.
func NewRoutineComfortPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineComfortPlanner: reviewer == nil || native == nil", ErrControl)
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, fmt.Errorf("%w: NewRoutineComfortPlanner: !ok", ErrControl)
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.EnsureComfort, phase: policy.ComfortRanked, definition: "Wall", shelter: true}, nil
}

// A definition needs one available pawn whose observed Construction setting
// is enabled and whose Construction skill meets the native minimum, in the
// same native observation bracket. The gate used to demand that the whole
// colony's settings match a Construction-only allocation, but the work
// planner applies a different allocation (bench and deficit work shift the
// owners), so the two only agreed by luck and a cooler could wait forever
// behind an unrelated pawn's Cooking checkbox (#66). This comparison never
// writes work settings.
func comfortBuilderAvailable(facts observation.ColonyProjection, definition string) bool {
	return comfortBuilderGate(facts, definition).IsZero()
}

// comfortBuilderGate is the verdict comfortBuilderAvailable reads: the zero
// Verdict when the definition is buildable and some colonist can build it,
// else the first thing missing, named.
func comfortBuilderGate(facts observation.ColonyProjection, definition string) Verdict {
	def, found := definitionRow(facts, definition)
	if !found {
		return fieldUnavailable(definition + "_definition")
	}
	if v := definitionAvailability(def); !v.IsZero() {
		return v
	}
	minimum, skillKnown := def.ConstructionSkill.Value()
	if !skillKnown || minimum < 0 {
		return fieldUnavailable(definition + "_construction_skill")
	}
	pawns, known := facts.WorkPawns.Value()
	if !known {
		return fieldUnavailable("work_pawns")
	}
	if builderAvailable(pawns, int(minimum)) {
		return Verdict{}
	}
	if clockDebug() {
		clockSchedulerLog("%s builder gate: no available pawn with Construction enabled at skill >= %d (%s)", definition, minimum, builderCensus(pawns))
	}
	need := "construction_work_enabled"
	if minimum > 0 {
		need = fmt.Sprintf("construction_skill_%d", minimum)
	}
	return refuse(RefusalNoWorker, "builder_for_"+definition, need)
}

// comfortAccessWait is the wait of a comfort facility that stands out of
// some colonists' reach: the dining one when dining is short, else
// recreation (the order both selectors check in).
func comfortAccessWait(dining policy.ComfortNeed) Verdict {
	if dining == policy.ComfortCapacity {
		return waitFor(WaitFacilityAccess, "dining")
	}
	return waitFor(WaitFacilityAccess, "recreation")
}

func (r *RoutineBuildingPlanner) selectComfort(facts observation.ColonyProjection, history policy.ComfortHistory) (*RoutineBuildingPlanner, Verdict, error) {
	v, known := facts.Facts.Comfort.Value()
	if !known {
		return nil, fieldUnavailable("comfort"), nil
	}
	review, err := policy.ReviewComfort(facts.Facts.Comfort, history, facts.Identity.Tick)
	if err != nil {
		return nil, Verdict{}, err
	}
	method, err := policy.SelectComfortMethod(v, review)
	if err != nil {
		return nil, Verdict{}, err
	}
	switch method {
	case policy.ComfortNoMethod:
		return nil, BuildingReasonNoDeficit, nil
	case policy.ComfortWait:
		return nil, BuildingComfortWait, nil
	case policy.ComfortAccessBlocked:
		return nil, comfortAccessWait(review.Dining), nil
	}
	resolved := *r
	resolved.definition = string(method)
	resolved.environment = policy.PlacementIndoors
	role := policy.RoomRoleDiningRoom
	if v.IsFoothold(method) {
		role = policy.RoomRoleRecRoom
	}
	facility, err := policy.Facility(role)
	if err != nil {
		return nil, Verdict{}, err
	}
	resolved.facility = &facility
	if method == policy.ComfortBuildChair {
		for _, s := range v.Surfaces {
			resolved.adjacent = append(resolved.adjacent, s.Adjacent...)
		}
	}
	resolved.stuff = facts.BuildStuff(resolved.definition)
	return &resolved, Verdict{}, nil
}

// Allow a finite interval for ordinary dining/recreation after this direction
// completed a comfort facility. Repeated observations cannot renew the budget.
func comfortNativeWorkTicks(plan store.PlanState, current domain.GenerationSnapshot, tick domain.Tick, foothold string) uint32 {
	if len(plan.Progress) != 1 {
		return 0
	}
	p := plan.Progress[0]
	b, ok := p.Action().Building()
	if !ok || !slices.Contains(comfortDefinitions(foothold), b.Definition()) {
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
// enabled in its observed settings and,
// when the definition needs one, a Construction skill at the native minimum.
// builderCensus is the diagnostic behind a "builder unavailable" wait
// (clock trace, serve --debug).
func builderAvailable(pawns []policy.WorkPawn, minimum int) bool {
	for _, pawn := range pawns {
		if pawn.Available != domain.Known(true) || pawn.Applies != domain.Known(true) {
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

// NewRoutineBasicComfortPlanner furnishes the starter hut at foothold
// priority: one table with a seat in any proper indoor room and one
// recreation source wherever colonists can reach it. It never stages a
// shell; while the initial shelter is owed it waits for that room.
func NewRoutineBasicComfortPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineBasicComfortPlanner: reviewer == nil || native == nil", ErrControl)
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, fmt.Errorf("%w: NewRoutineBasicComfortPlanner: !ok", ErrControl)
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.EnsureComfort, phase: policy.ComfortBasic}, nil
}

// selectBasicComfort resolves the next foothold facility from the unfiltered
// census. The table and seat go under a roof (any proper room, the starter
// hut included); the recreation source may stand outdoors, as a horseshoes
// pin ordinarily does, so placement is unrestricted and the native watch-cell
// preview decides reach.
func (r *RoutineBuildingPlanner) selectBasicComfort(facts observation.ColonyProjection) (*RoutineBuildingPlanner, Verdict, error) {
	v, known := facts.Facts.BasicComfort.Value()
	if !known {
		return nil, fieldUnavailable("basic_comfort"), nil
	}
	review, err := policy.ReviewBasicComfort(facts.Facts.BasicComfort)
	if err != nil {
		return nil, Verdict{}, err
	}
	method, err := policy.SelectBasicComfortMethod(v, review)
	if err != nil {
		return nil, Verdict{}, err
	}
	if method == policy.ComfortNoMethod && !review.VarietyKnown {
		return nil, fieldUnavailable("comfort_variety"), nil
	}
	if method == policy.ComfortNoMethod && review.MissingVariety > 0 {
		method = policy.SelectRecreationVariety(v, func(m policy.JoyBuildingMethod) bool {
			if m.PowerW > 0 {
				canSite := false
				for _, c := range facts.Cells {
					canSite = canSite || c.Roofed == domain.Known(true) && c.Indoors == domain.Known(true) && c.Occupied == domain.Known(false) && poweredRecreationCell(facts, c.Cell, m.PowerW)
				}
				if !canSite {
					return false
				}
			}
			return comfortBuilderAvailable(facts, m.Definition)
		})
	}
	switch method {
	case policy.ComfortNoMethod:
		return nil, BuildingReasonNoDeficit, nil
	case policy.ComfortAccessBlocked:
		return nil, comfortAccessWait(review.Dining), nil
	}
	resolved := *r
	resolved.definition = string(method)
	if v.Joy != nil {
		for _, m := range v.Joy.Methods {
			if m.Definition == resolved.definition {
				resolved.recreationPowerW = m.PowerW
			}
		}
	}
	resolved.environment = policy.PlacementIndoors
	if v.IsFoothold(method) {
		resolved.environment = policy.PlacementAnywhere
	}
	if method == policy.ComfortBuildChair {
		for _, s := range v.Surfaces {
			resolved.adjacent = append(resolved.adjacent, s.Adjacent...)
		}
	}
	resolved.stuff = facts.BuildStuff(resolved.definition)
	return &resolved, Verdict{}, nil
}

// A TV is sited within connector reach of a running generator on a network
// with observed surplus. A surplus on an unrelated network cannot lend power.
func poweredRecreationCell(facts observation.ColonyProjection, cell domain.Cell, watts float64) bool {
	topology, known := facts.PowerPlanning.Value()
	if !known || topology.Blackout != domain.Known(false) {
		return false
	}
	for _, b := range topology.Buildings {
		output, ok := b.OutputW.Value()
		net, nk := b.Network.Value()
		dx, dz := cell.X-b.Cell.X, cell.Z-b.Cell.Z
		if !ok || output <= 0 || !nk || b.Connected != domain.Known(true) || dx*dx+dz*dz > 36 {
			continue
		}
		for _, n := range topology.Networks {
			generation, gk := n.GenerationW.Value()
			consumption, ck := n.ConsumptionW.Value()
			if n.ID == net && gk && ck && generation-consumption >= watts {
				return true
			}
		}
	}
	return false
}
