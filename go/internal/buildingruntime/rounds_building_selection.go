package buildingruntime

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// pendingWork is open work on one action, counting an applied building
// whose blueprint or frame may still stand (#856): only census-aware plan
// retirement settles it.
func pendingWork(progress domain.Progress) bool {
	return domain.StandardWorkOpen([]domain.Progress{progress}) || policy.AppliedBuildingOpen(progress, domain.Unknown[policy.CurrentConstruction]())
}

func pendingFacility(progress domain.Progress, definition string) bool {
	building, ok := progress.Action().Building()
	return ok && building.Definition() == definition && pendingWork(progress)
}

func NewRoundsCookingPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsCookingPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.EnsureCooking, definition: "Campfire", environment: policy.PlacementAnywhere}, nil
}

func NewRoundsButcherPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsButcherPlanner: reviewer == nil || native == nil", ErrControl)
	}
	if _, ok := native.(observation.RoundsSource); !ok {
		return nil, fmt.Errorf("%w: NewRoundsButcherPlanner: !ok", ErrControl)
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainButcherSpot, definition: "ButcherSpot", environment: policy.PlacementAnywhere}, nil
}
func (r *RoundsBuildingPlanner) selection(facts observation.ColonyProjection) (int64, domain.MethodID, Verdict) {
	count, known := facts.Facts.Colonists.Value()
	if !known || count <= 0 {
		return 0, "", fieldUnavailable("colonists")
	}
	switch r.concern {
	case policy.MaintainButcherSpot:
		if r.definition != "ButcherSpot" {
			return 0, "", fieldUnavailable("butcher_spot_definition")
		}
		// The spot is free and instant, and the butcher bill is the hunt
		// row's precondition (#260): it is owed whenever the goal is, not on
		// the food runway (the colony has the spot standing before it runs
		// short) and not on anyone being armed yet. Whether a colonist can
		// hunt is native's rule on the hunt row, after the equip family arms
		// them.
		benches, bk := facts.ButcheringBenches.Value()
		if !bk {
			return 0, "", fieldUnavailable("butchering_benches")
		}
		if len(benches) > 0 {
			// A butcher bench that shares a room with a cooking bench keeps
			// the colony fed but not clean (issue #6 slice 2): when every
			// bench is co-located and the census can say so, a separate
			// butcher spot is admitted outside. Deconstructing the shared
			// one needs a generic deconstruct action (follow-up).
			if !butchersAllColocated(benches, facts.Rooms) {
				if _, retire := standingButcherSpot(facts); retire {
					return 1, "butcher-spot-retire", Verdict{}
				}
				if butcherTableWanted(facts, benches) {
					return 1, "butcher-table", Verdict{}
				}
				return 0, "", BuildingExistingFacility
			}
			return 1, "butcher-spot-separated", Verdict{}
		}
		return 1, "butcher-spot", Verdict{}

	case policy.EnsureBasicDefense:
		// Reached only from RoundsEquipPlanner once no loose weapon and no
		// bench's weapon recipe can arm an unarmed colonist.
		if r.definition != "CraftingSpot" {
			return 0, "", fieldUnavailable("crafting_spot_definition")
		}
		return 1, "crafting-spot", Verdict{}
	case policy.EnsureTemperatureSafety:
		if r.temperature == nil {
			return 0, "", fieldUnavailable("temperature_proposal")
		}
		return 1, r.temperature.Key, Verdict{}
	case policy.MaintainRefrigeration:
		if r.refrigeration == nil || r.refrigeration.Method != policy.RefrigerationBuild {
			return 0, "", fieldUnavailable("refrigeration_proposal")
		}
		return 1, r.refrigeration.Key, Verdict{}
	case policy.MaintainLighting:
		if r.lighting == nil || r.lighting.Method != policy.LightingBuild {
			return 0, "", fieldUnavailable("lighting_proposal")
		}
		return 1, r.lighting.Key, Verdict{}
	case policy.MaintainFlooring:
		if r.flooring == nil || r.flooring.Method != policy.FlooringBuild {
			return 0, "", fieldUnavailable("flooring_proposal")
		}
		return int64(len(r.flooring.Cells)), r.flooring.Key, Verdict{}
	case policy.MaintainRoutes:
		if r.routes == nil || r.routes.Method != policy.RoutesBuild {
			return 0, "", fieldUnavailable("routes_proposal")
		}
		return 1, r.routes.Key, Verdict{}
	case policy.EnsureBasicPower:
		if r.power == nil {
			return 0, "", fieldUnavailable("power_proposal")
		}
		if r.power.Method == policy.PowerConnect {
			return int64(len(r.power.Cells)), r.power.Key, Verdict{}
		}
		if r.power.Method == policy.PowerGenerate || r.power.Method == policy.PowerStore {
			return 1, r.power.Key, Verdict{}
		}
		if r.power.Method == policy.PowerShelter {
			return int64(2*r.power.Room.Width + 2*r.power.Room.Height - 4), r.power.Key, Verdict{}
		}
		return 0, "", fieldUnavailable("power_proposal")
	case policy.EnsureComfort:
		if r.phase == policy.ComfortBasic {
			return 1, domain.MethodID("basic-comfort-" + r.definition), Verdict{}
		}
		if r.shelter {
			return 32, "comfort-shell", Verdict{}
		}
		return 1, domain.MethodID("comfort-" + r.definition), Verdict{}
	case policy.MaintainResource, policy.MaintainEquipment:
		if r.shelter {
			return 32, "workshop-shell", Verdict{}
		}
		return 1, domain.MethodID("workshop-" + r.definition), Verdict{}
	case policy.MaintainMedicalReserves:
		if r.shelter {
			return 32, "hospital-shell", Verdict{}
		}
		return 1, domain.MethodID("hospital-" + r.definition), Verdict{}
	case policy.EnsureResearch:
		if r.shelter {
			return 32, "laboratory-shell", Verdict{}
		}
		return 1, domain.MethodID("laboratory-" + r.definition), Verdict{}
	case policy.MaintainHousing:
		// The three housing phases share the goal's epoch, so each names
		// its own methods: the starter shell and its bunks, then the
		// bedrooms, then the spare expansion room.
		if r.phase == policy.HousingSleeping {
			if r.shelter {
				return 32, "sleeping-shell", Verdict{}
			}
			// One method per bed still owed: the count falls once a staged
			// bed is assigned, so the next bed is a new method in the same
			// epoch.
			if r.sleeping == nil {
				return 0, "", fieldUnavailable("sleeping_proposal")
			}
			return 1, domain.MethodID(fmt.Sprintf("sleeping-%s-%d", r.definition, r.sleeping.Unhoused)), Verdict{}
		}
		capacity, known := facts.Facts.IndoorCapacity.Value()
		if !known {
			return 0, "", fieldUnavailable("indoor_capacity")
		}
		prefix := ""
		if r.phase == policy.HousingExpansion {
			if plan, known := facts.Facts.FoodPlan.Value(); known && plan.GapPerDay > 0 {
				return 0, "", awaitingFoodPlan("housing_expansion")
			}
			if count >= 1<<63-1 {
				return 0, "", fieldUnavailable("colonist_count")
			}
			count++
			prefix = "expansion-"
		} else if target, known := facts.Facts.HousingTarget.Value(); known {
			count = max(count, target)
		}
		missing := count - capacity
		if missing <= 0 {
			return 0, "", BuildingReasonNoDeficit
		}
		if missing > 64 {
			return 0, "", noSpace("housing_bound")
		}
		if r.shelter {
			return 32, domain.MethodID(prefix + "shelter-shell" + shelterShellSuffix(facts)), Verdict{}
		}
		return missing, domain.MethodID(fmt.Sprintf("%sindoor-sleeping-%d-%d", prefix, count, missing)), Verdict{}
	case policy.EnsureCooking:
		if len(r.paste) > 0 {
			return int64(len(r.paste)), "nutrient-paste", Verdict{}
		}
		if !foodPlanSupport(facts.Facts.FoodPlan, policy.FoodCook, "cooking-capacity") {
			return 0, "", awaitingFoodPlan("cooking-capacity")
		}
		ready, known := facts.Facts.Cooking.Value()
		if !known {
			return 0, "", fieldUnavailable("cooking")
		}
		if ready {
			return 0, "", BuildingReasonNoDeficit
		}
		benches, known := facts.CookingBenches.Value()
		if !known {
			return 0, "", fieldUnavailable("cooking_benches")
		}
		unknown := false
		for _, bench := range benches {
			usable, known := bench.Usable.Value()
			if bench.Definition == "Campfire" || known && usable {
				return 0, "", BuildingExistingFacility
			}
			unknown = unknown || !known
		}
		if unknown {
			return 0, "", fieldUnavailable("cooking_bench_bills")
		}
		if campfireIntentStanding(facts.Facts.ConstructionClaims, facts.Facts.CurrentConstruction) {
			return 0, "", BuildingExistingFacility
		}
		return 1, "campfire", Verdict{}
	default:
		return 0, "", fieldUnavailable("concern_selection")
	}
}

// butchersAllColocated is true only when the room census is known and every
// butcher bench stands in a room that also holds a cooking bench. An unknown
// room keeps the bench counted as a facility.
func butchersAllColocated(benches []observation.CookingBench, rooms domain.Fact[policy.RoomObservation]) bool {
	shared, known := policy.KitchenSeparation(rooms).Value()
	if !known || len(benches) == 0 {
		return false
	}
	colocated := map[string]bool{}
	for _, room := range shared {
		colocated[room.ID] = true
	}
	for _, bench := range benches {
		room, known := bench.Room.Value()
		if !known || !colocated[room] {
			return false
		}
	}
	return true
}

// campfireIntentStanding is true when a complete census shows a campfire
// blueprint or frame placed by any recorded claim (#1534). The cooking bench
// census holds only built benches and the pending-work gate reads only open
// plans, so a retired plan's standing blueprint otherwise let the planner
// stage another campfire, up to sleepingBedsPerEpoch of them.
func campfireIntentStanding(claims domain.Fact[[]policy.ConstructionClaim], observed domain.Fact[policy.CurrentConstruction]) bool {
	census, known := observed.Value()
	if !known || !census.Colony {
		return false
	}
	history, _ := claims.Value()
	// A built campfire the cooking census does not list (the kitchen ring
	// closed around it before its door, so the bench is unreachable) still
	// stands: another would be a second, third campfire in the same room.
	heat := heatCampfireCells(history)
	for _, b := range census.Buildings {
		if b.Building.Definition() == "Campfire" && len(b.Cells) > 0 && !heat[b.Cells[0]] {
			return true
		}
	}
	for _, claim := range history {
		if claim.Building.Definition() == "Campfire" && policy.WorkOpen(claim.Building, observed) == policy.BuildingOpen {
			return true
		}
	}
	return false
}

// butcherTableWanted is true once a butcher spot stands apart and the colony
// can raise the real table: the table is built from stuff, so the wood must
// be in stock, and a builder able to raise it must be at work.
func butcherTableWanted(facts observation.ColonyProjection, benches []observation.CookingBench) bool {
	for _, b := range benches {
		if b.Definition == "TableButcher" {
			return false
		}
	}
	wood, known := facts.Facts.Wood.Value()
	return known && wood >= policy.ButcherTableWood && comfortBuilderAvailable(facts, "TableButcher")
}

// standingButcherSpot is a standing butcher spot, found in the building
// census, once a butcher table stands apart as well: the stand-in spot is
// deconstructed then.
func standingButcherSpot(facts observation.ColonyProjection) (policy.CurrentBuilding, bool) {
	benches, bk := facts.ButcheringBenches.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !bk || !ck {
		return policy.CurrentBuilding{}, false
	}
	table := false
	for _, bench := range benches {
		table = table || bench.Definition == "TableButcher"
	}
	if !table {
		return policy.CurrentBuilding{}, false
	}
	for _, bench := range benches {
		if bench.Definition != "ButcherSpot" {
			continue
		}
		for _, b := range census.Buildings {
			if b.ID == bench.ID && len(b.Cells) > 0 {
				return b, true
			}
		}
	}
	return policy.CurrentBuilding{}, false
}
