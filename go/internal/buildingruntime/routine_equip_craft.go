package buildingruntime

import (
	"context"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// craftingSpotDefinition is the free, unpowered bench every tech level can
// place at once; it hosts the club and short bow recipes a tribal start
// arms itself with.
const craftingSpotDefinition = "CraftingSpot"

// BuildingNoWeaponBench: an unarmed colonist has no loose weapon and no bench
// hosts a weapon recipe, and no crafting spot planner is wired to place one.
const BuildingNoWeaponBench RoutineBuildingReason = "no_weapon_bench"

// RoutineEquipCraftSource is the bench census the equip planner reads once no
// loose weapon is left for an unarmed colonist.
type RoutineEquipCraftSource interface {
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
}

// newCraftingSpotPlanner is the EnsureBasicDefense placement the equip planner
// falls back to; nil when the source cannot serve a building step.
func newCraftingSpotPlanner(reviewer *RoutineReviewer, native RoutineEquipSource) *RoutineBuildingPlanner {
	building, ok := native.(RoutineBuildingSource)
	if !ok {
		return nil
	}
	if _, ok := native.(RoutineEquipCraftSource); !ok {
		return nil
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: building, goal: policy.EnsureBasicDefense, definition: craftingSpotDefinition, environment: policy.PlacementAnywhere}
}

// craftWeapons arms the colonists no loose weapon can: an active weapon bill
// is waited on, a bench hosting a weapon recipe gets a demand-sized batch
// bill, and with no such bench a crafting spot is placed (its bill follows
// on the next step once it stands).
func (r *RoutineEquipPlanner) craftWeapons(call, epoch context.Context, arbiter *stepArbiter, state ControlState, goal store.GoalState, identity *c.Identity, pawns []policy.EquipCandidatePawn, weapons []policy.EquipCandidateWeapon) (RoutineEquipResult, error) {
	source, ok := r.native.(RoutineEquipCraftSource)
	if !ok || policy.UnarmedFighters(pawns, weapons) == 0 {
		return RoutineEquipResult{Reason: BuildingMethodUsed}, nil
	}
	census, _, err := source.ReadGearBenches(call, identity)
	if err != nil {
		return RoutineEquipResult{}, err
	}
	benches := make([]policy.GearBench, 0, len(census))
	for _, row := range census {
		benches = append(benches, row.Bench)
	}
	selection := selectWeaponBill(pawns, weapons, benches)
	switch selection.reason {
	case weaponBillAdd:
	case weaponBillNoBench:
		if r.spot == nil {
			return RoutineEquipResult{Reason: BuildingNoWeaponBench}, nil
		}
		result, err := r.spot.step(call, epoch, arbiter)
		if err != nil {
			return RoutineEquipResult{}, err
		}
		if result.Decision.Admitted {
			return RoutineEquipResult{Reason: BuildingMethodAdmitted}, nil
		}
		return RoutineEquipResult{Reason: result.Reason}, nil
	default:
		return RoutineEquipResult{Reason: BuildingMethodUsed}, nil
	}
	claim := "weapon-bill:" + selection.bench
	arbiter.mu.Lock()
	taken := arbiter.resources[claim]
	arbiter.resources[claim] = true
	arbiter.mu.Unlock()
	if taken {
		return RoutineEquipResult{Reason: BuildingMethodUsed}, nil
	}
	bill, err := domain.NewProductionBill(selection.bench, selection.recipe, domain.GearBatch, selection.count)
	if err != nil {
		return RoutineEquipResult{}, err
	}
	id := domain.MintPlanID("routine-weapon-bill")
	action, err := domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill)
	if err != nil {
		return RoutineEquipResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineEquipResult{}, err
	}
	p := r.reviewer.player
	if err = p.current(call, epoch); err != nil {
		return RoutineEquipResult{}, err
	}
	if p.session.State() != state {
		return RoutineEquipResult{}, fmt.Errorf("%w: craftWeapons: p.session.State() != state", ErrControl)
	}
	method := domain.MethodID(fmt.Sprintf("weapon-bill-%d", len(goal.Methods)))
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineEquipResult{}, err
	}
	return RoutineEquipResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

type weaponBillReason int

const (
	weaponBillNone weaponBillReason = iota
	weaponBillAdd
	weaponBillNoBench
)

type weaponBill struct {
	reason        weaponBillReason
	bench, recipe string
	count         int32
}

// selectWeaponBill is the pure choice behind craftWeapons: wait on any
// active weapon bill (or an unknown bill list), place a spot when no bench
// hosts a weapon recipe at all, otherwise bill the best weapon the unarmed
// fighters could use on the first bench (by ID) whose recipe makes it.
func selectWeaponBill(pawns []policy.EquipCandidatePawn, weapons []policy.EquipCandidateWeapon, benches []policy.GearBench) weaponBill {
	hosted := false
	var recipes []policy.GearRecipe
	for _, b := range benches {
		bills, known := b.Bills.Value()
		if !known {
			return weaponBill{}
		}
		for _, bill := range bills {
			if active, _ := bill.Active.Value(); active && policy.WeaponBill(bill) {
				return weaponBill{}
			}
		}
		rows, known := b.Recipes.Value()
		if !known {
			return weaponBill{}
		}
		for _, recipe := range rows {
			hosted = hosted || policy.WeaponRecipe(recipe)
		}
		recipes = append(recipes, rows...)
	}
	if !hosted {
		return weaponBill{reason: weaponBillNoBench}
	}
	demand := policy.WeaponProductionDemand(pawns, weapons, recipes)
	if len(demand) == 0 {
		return weaponBill{}
	}
	ordered := append([]policy.GearBench(nil), benches...)
	sort.Slice(ordered, func(i, j int) bool { return ordered[i].ID < ordered[j].ID })
	for _, want := range demand {
		for _, b := range ordered {
			rows, _ := b.Recipes.Value()
			for _, recipe := range rows {
				available, _ := recipe.Available.Value()
				on, _ := recipe.AvailableOn.Value()
				if !available || !on {
					continue
				}
				for _, product := range recipe.Products {
					if product == want.Resource {
						return weaponBill{reason: weaponBillAdd, bench: b.ID, recipe: recipe.Definition, count: int32(min(want.Count, 10000))}
					}
				}
			}
		}
	}
	return weaponBill{}
}
