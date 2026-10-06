package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type RoundsBillPlanner struct {
	reviewer *Rounder
	need     policy.ConcernID
	purpose  policy.BillPurpose
	native   BillPlannerNative
}
type RoundsBillResult struct {
	Verdict
	Plan domain.PlanID
	// NativeWorkTicks asks for game time while a claimed art bill is still
	// being sculpted: a placed bill is no work to the clock (#1195).
	NativeWorkTicks uint32
}

// BillPlannerNative is the native reader behind a bill planner; the
// butcher purpose asserts it to RoundsResourceSource for a corpse storage zone.
type BillPlannerNative interface{}

// NewRoundsBillPlanner composes one bill purpose: cooking serves
// EnsureCooking, preservation MaintainFoodStorage, butchery EnsureFoodSupply, the
// cook-ahead bill MaintainRefrigeration under a solar flare (#408), and the
// pinned sculpture bills MaintainArt (#1190), the part bills
// MaintainSurgery (#1168), the baby food bill MaintainBabyFeeding (#1681), and the mech gestation bills MaintainMechs (#1686).
func NewRoundsBillPlanner(reviewer *Rounder, native BillPlannerNative, purpose policy.BillPurpose) (*RoundsBillPlanner, error) {
	if reviewer == nil || native == nil || (purpose != policy.CookFood && purpose != policy.PreserveFood && purpose != policy.ButcherFood && purpose != policy.CookAheadFood && purpose != policy.ArtBill && purpose != policy.SurgeryPartBill && purpose != policy.BabyFoodBill && purpose != policy.MechGestationBill) {
		return nil, fmt.Errorf("%w: NewRoundsBillPlanner: reviewer == nil || native == nil || (purpose != policy.CookFood && purpose != policy.PreserveFood && purpos", ErrControl)
	}
	need := policy.EnsureFoodSupply
	switch purpose {
	case policy.CookFood:
		need = policy.EnsureCooking
	case policy.PreserveFood:
		need = policy.MaintainFoodStorage
	case policy.CookAheadFood:
		need = policy.MaintainRefrigeration
	case policy.ArtBill:
		need = policy.MaintainArt
	case policy.SurgeryPartBill:
		need = policy.MaintainSurgery
	case policy.BabyFoodBill:
		need = policy.MaintainBabyFeeding
	case policy.MechGestationBill:
		need = policy.MaintainMechs
	}
	return &RoundsBillPlanner{reviewer: reviewer, native: native, purpose: purpose, need: need}, nil
}
func (r *RoundsBillPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsBillResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsBillResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoundsBillResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsBillResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsBillResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.WorkableOwner(call, review, r.need)
	if err != nil {
		return RoundsBillResult{}, err
	}
	if !workable {
		return RoundsBillResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	if goal.OwnerPriority() >= 3 {
		selected := false
		for _, row := range review.Development.Rows {
			selected = selected || row.Concern == r.need && row.Selected
		}
		if !selected {
			return RoundsBillResult{Verdict: awaitingSlot(string(r.need))}, nil
		}
	}
	for _, method := range goal.OwnerMethods() {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsBillResult{}, err
		}
		if r.need == policy.EnsureFoodSupply {
			// Fields, foraging and hunts share the goal and stay open for
			// days; only an open bill is this planner's own work (#260).
			for _, progress := range plan.Progress {
				if progress.Action().Kind() == domain.ProductionBillAction && domain.StandardWorkOpen([]domain.Progress{progress}) {
					if b, ok := progress.Action().ProductionBill(); ok && r.purpose == policy.ButcherFood && b.Mode() == domain.ButcherForever {
						continue
					}
					return RoundsBillResult{Verdict: BuildingReasonExistingWork}, nil
				}
			}
			continue
		}
		if r.purpose != policy.CookFood && store.PlanOpen(plan) {
			return RoundsBillResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return RoundsBillResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(call, playerWorld(state.Snapshot))
	if err != nil {
		return RoundsBillResult{}, err
	}
	definitions := roundsProjectDefinitions(plans, state.Snapshot, playerPlans)
	if r.purpose == policy.CookFood {
		definitions = append(definitions, "NutrientPasteDispenser", "Hopper")
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsBillResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsBillResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoundsBillResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, definitions...)
	if err != nil {
		return RoundsBillResult{}, err
	}
	projection := read.Projection
	recordStepRead("bill", r.need, state.Snapshot, projection)
	if r.purpose == policy.ArtBill {
		selected, missing, art, err := r.artSelection(call, state, projection, review.Latches.MedicalReserve)
		// A placed sculpture bill is no work to the clock, but the
		// sculpture takes days of game time (#1195).
		var ticks uint32
		if art.sculpting {
			ticks = artNativeWorkTicks
		}
		if err != nil || !missing.IsZero() {
			return RoundsBillResult{Verdict: missing, NativeWorkTicks: ticks}, err
		}
		result, err := r.admit(call, epoch, arbiter, state, goal, read, selected, art.finished[selected.Worker])
		result.NativeWorkTicks = max(result.NativeWorkTicks, ticks)
		return result, err
	}
	if r.purpose == policy.MechGestationBill {
		selected, verdict, err := r.mechSelection(call, state, projection)
		if err != nil {
			return RoundsBillResult{}, err
		}
		if !verdict.IsZero() {
			return RoundsBillResult{Verdict: verdict}, nil
		}
		// Each gestation is a new method of the goal: the bill is the same
		// bench, recipe and count as the one before it.
		return r.admit(call, epoch, arbiter, state, goal, read, selected, len(goal.OwnerMethods()))
	}
	if r.purpose == policy.SurgeryPartBill {
		parts, benches, err := surgeryPartDemand(call, r.native, boundary.Identity(state.Snapshot), projection.Facts.MedicalPawns, projection.SurgeryContext())
		if err != nil {
			return RoundsBillResult{}, err
		}
		selected, gap := policy.SelectSurgeryPartBill(benches, parts)
		if gap != "" {
			return RoundsBillResult{Verdict: billGapVerdict(gap, "surgery_part_bill")}, nil
		}
		return r.admit(call, epoch, arbiter, state, goal, read, selected, 0)
	}
	if r.purpose == policy.BabyFoodBill {
		babies, known := projection.Facts.BabyFeeding.Value()
		if !known {
			return RoundsBillResult{Verdict: fieldUnavailable("baby_feeding")}, nil
		}
		selected, known := policy.SelectProductionBill(r.purpose, projection.ProductionBenches, projection.Facts.Colonists, domain.Fact[float64]{}, domain.Fact[float64]{}, 1, policy.ProductionBillContext{BabyFeeding: &babies})
		if !known {
			return RoundsBillResult{Verdict: BuildingReasonNoDeficit}, nil
		}
		return r.admit(call, epoch, arbiter, state, goal, read, selected, 0)
	}
	if r.purpose == policy.CookFood && !foodPlanSupport(projection.Facts.FoodPlan, policy.CandidateCook, "cooking-capacity") {
		return RoundsBillResult{Verdict: awaitingFoodPlan("cooking-capacity")}, nil
	}
	if r.purpose == policy.ButcherFood {
		// Owed on the food runway alone (#260): native offers no hunt row
		// until a usable bench carries this bill, so waiting for an armed
		// colonist would serialise spot, bill and hunt behind the equip family.
		days, dk := projection.Facts.FoodDays.Value()
		if (!dk || days >= r.reviewer.seasonal(projection.Facts).FoodTargetDays) && !policy.HumanFoodPending(projection.Facts.FoodPlan) {
			if !dk {
				return RoundsBillResult{Verdict: fieldUnavailable("food_days")}, nil
			}
			return RoundsBillResult{Verdict: BuildingReasonNoDeficit}, nil
		}
		// A butcher bench that shares a cooking room feeds the colony but keeps
		// the kitchen dirty (issue #6 slice 2). While every bench is co-located
		// the separated-spot build owns the goal: the bill waits until that
		// method has been tried (admitted, completed or failed) and then
		// prefers whichever bench stands apart. A forever bill would otherwise
		// hold the goal open until a corpse arrives.
		if rows, known := projection.ProductionBenches.Value(); known && policy.AllButchersColocated(rows) {
			tried, triedErr := p.butcherSpotSeparated(call, review)
			if triedErr != nil {
				return RoundsBillResult{}, triedErr
			}
			if !tried {
				return RoundsBillResult{Verdict: BuildingReasonSeparation}, nil
			}
		}
	}
	benches, atRisk := projection.ProductionBenches, projection.FoodAtRiskNutrition
	if r.purpose == policy.CookAheadFood {
		// Only under a solar flare with a known remaining duration: the
		// coolers are dark for the outage, so the warm at-risk stock the
		// refrigeration review latched on is cooked instead. A bench whose
		// meal recipe this load already claimed (the ordinary cooking bill)
		// is dropped from the census: one claim per bench and recipe.
		if !policy.PowerOutageHold(projection.Facts.DisasterConditions) {
			return RoundsBillResult{Verdict: BuildingReasonNoDeficit}, nil
		}
		refrigeration, err := policy.ReviewRefrigeration(projection.Facts.FoodStorageUpkeep, review.Latches.Refrigeration, r.reviewer.policy.FoodStorage)
		if err != nil {
			return RoundsBillResult{}, err
		}
		atRisk = refrigeration.WarmNutrition
		if benches, err = r.unclaimedBenches(call, state.Snapshot, benches); err != nil {
			return RoundsBillResult{}, err
		}
	}
	var billContext []policy.ProductionBillContext
	reserveRunning := false
	if r.purpose == policy.CookFood {
		seasonal := r.reviewer.seasonal(projection.Facts)
		meals := projection.MealRequest(seasonal.FoodMinDays, seasonal.FoodTargetDays)
		billContext = append(billContext, policy.ProductionBillContext{Meals: &meals})
	}
	if r.purpose == policy.PreserveFood {
		if !foodPlanSupport(projection.Facts.FoodPlan, policy.CandidateReserve, "stock-protection") {
			return RoundsBillResult{Verdict: awaitingFoodPlan("stock-protection")}, nil
		}
		value, known := projection.Facts.FoodReserve.Value()
		if !known {
			return RoundsBillResult{Verdict: fieldUnavailable("food_reserve")}, nil
		}
		billContext = append(billContext, policy.ProductionBillContext{Reserve: &value})
		// A standing short reserve bill is native cook work: lend game time
		// instead of parking the clock on no_work while it fills.
		reserveRunning = policy.ReserveBillRunning(benches, value)
	}
	if r.purpose == policy.CookFood {
		if supply, ok := projection.CombinedFoodSupply.Value(); ok {
			if humans, ok := projection.FoodSupply.Value(); ok {
				billContext[0].Ingredients = policy.HumanCookingIngredients(supply, humans.Consumers, projection.Facts.FoodPlan, policy.HumanMeatMeals)
			}
		}
	}
	selected, known := policy.SelectProductionBill(r.purpose, benches, projection.Facts.Colonists, projection.Facts.FoodDays, atRisk, r.reviewer.seasonal(projection.Facts).FoodTargetDays, billContext...)
	if r.purpose == policy.PreserveFood {
		if supply, ok := projection.CombinedFoodSupply.Value(); ok {
			if sale, ok := policy.SelectHumanSurvivalBill(benches, supply, projection.Facts.FoodPlan); ok {
				selected, known = sale, true
			}
		}
	}
	if r.purpose == policy.ButcherFood {
		if human, ok := policy.SelectHumanButcher(benches, projection.Facts.IdeologyRead()); ok {
			if !foodPlanSupport(projection.Facts.FoodPlan, policy.CandidateCorpse, "human-butchery") {
				return RoundsBillResult{Verdict: awaitingFoodPlan("human-butchery")}, nil
			}
			selected, known = human, true
		}
	}
	if !known {
		return r.lendReserveWork(RoundsBillResult{Verdict: r.noBill(benches, projection.Facts.Colonists)}, reserveRunning), nil
	}
	result, err := r.admit(call, epoch, arbiter, state, goal, read, selected, 0)
	return r.lendReserveWork(result, reserveRunning), err
}

// billGapVerdict is the verdict of a bill selector that chose nothing: the
// gap names why, the subject the bill the planner wanted.
func billGapVerdict(gap policy.BillGap, subject string) Verdict {
	switch gap {
	case policy.BillGapNothingWanted:
		return BuildingReasonNoDeficit
	case policy.BillGapInProduction:
		return waitFor(WaitExistingWork, subject)
	case policy.BillGapNoRecipe:
		return awaitingPlan(subject, string(gap))
	case policy.BillGapBenchFull:
		return awaitingPlan(subject, string(gap))
	case policy.BillGapWaste:
		return awaitingPlan("mech_waste", "")
	case policy.BillGapNoCharger:
		return awaitingPlan("mech_charger", "")
	}
	panic(fmt.Sprintf("bill gap %q is not one of the closed set", gap))
}

// noBill says why the food-bill selection chose nothing: a census it needs is
// unread, no usable bench of the purpose's kind stands, or the bills already
// standing cover what is owed.
func (r *RoundsBillPlanner) noBill(benches domain.Fact[[]policy.ProductionBench], colonists domain.Fact[int64]) Verdict {
	rows, known := benches.Value()
	if !known {
		return fieldUnavailable("production_benches")
	}
	if _, known := colonists.Value(); !known {
		return fieldUnavailable("colonists")
	}
	butcher := r.purpose == policy.ButcherFood
	for _, bench := range rows {
		if usable, known := bench.Usable.Value(); known && usable && bench.Butcher == butcher {
			return BuildingReasonNoDeficit
		}
	}
	if butcher {
		return awaitingPlan("butcher_bench", "")
	}
	return awaitingPlan("cooking_bench", "")
}

// lendReserveWork asks for game time while a reserve bill runs and no new
// bill was admitted this step.
func (r *RoundsBillPlanner) lendReserveWork(result RoundsBillResult, running bool) RoundsBillResult {
	if running && result.Verdict != BuildingReasonAdmitted {
		result.NativeWorkTicks = max(result.NativeWorkTicks, animalFeedBillWorkTicks)
	}
	return result
}

// admit commits the selected bill as the goal's method, once per goal
// epoch and claim.
func (r *RoundsBillPlanner) admit(call, epoch context.Context, arbiter *stepArbiter, state ControlState, goal store.WorkOwner, read observation.RoundsReading, selected policy.BillSelection, round int) (RoundsBillResult, error) {
	p := r.reviewer.player
	value, err := domain.NewProductionBill(selected.Bench, selected.Recipe, selected.Mode, selected.Target, selected.Ingredients...)
	if selected.Mode == domain.HumanButcherForever {
		value, err = domain.NewHumanButcherBill(selected.Bench, selected.Recipe, selected.Worker)
	} else if err == nil && selected.Worker != "" {
		value, err = value.PinWorker(selected.Worker)
	}
	if err != nil {
		return RoundsBillResult{}, err
	}
	claimed, err := p.journal.BillClaimed(call, state.Snapshot, selected.Bench, value.ClaimRecipe())
	if err != nil {
		return RoundsBillResult{}, err
	}
	// Finite batches expire (store.checkBillMethod): a claim from an earlier
	// batch does not bar the next one.
	if claimed && selected.Replace == "" && selected.Mode != domain.GearBatch {
		return RoundsBillResult{Verdict: waitFor(WaitMethodUsed, "bill_claim")}, nil
	}
	// The bill planners of one step run concurrently and read the same
	// bench token; the second bill on a bench would hold forever on the
	// first's write (#408). One bill per bench per step.
	if arbiter != nil && !arbiter.tryClaim(nil, "bench:"+selected.Bench) {
		return RoundsBillResult{Verdict: claimHeld("bench")}, nil
	}
	hash := sha256.New()
	fmt.Fprintf(hash, "%s/%s/%s/%d", selected.Bench, selected.Recipe, selected.Mode, selected.Target)
	if selected.Worker != "" {
		fmt.Fprintf(hash, "/%s", selected.Worker)
	}
	// A finished sculpture batch stays on the bench, inactive; the next
	// piece of the same shape (a sale sculpture after the room's, #1195)
	// is a new method, keyed by the finished batches before it.
	if round > 0 {
		fmt.Fprintf(hash, "/round%d", round)
	}
	if selected.Replace != "" {
		fmt.Fprint(hash, "/", selected.Replace, "/", selected.Token)
	}
	method := domain.MethodID(fmt.Sprintf("bill-%x", hash.Sum(nil)[:16]))
	if _, err = p.journal.LoadOwnerMethod(call, goal, method); err == nil {
		return RoundsBillResult{Verdict: waitFor(WaitMethodUsed, "bill_method")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsBillResult{}, err
	}
	id := domain.MintPlanID()
	if selected.Replace != "" {
		value, err = value.ReplaceOwnedBill(selected.Replace)
		if err != nil {
			return RoundsBillResult{}, err
		}
	}
	action, err := domain.NewProductionBillAction(domain.ActionID(string(id)+"-0"), value)
	if err != nil {
		return RoundsBillResult{}, err
	}
	actions := []domain.Action{action}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsBillResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsBillResult{}, err
	}
	if p.session.State() != state {
		return RoundsBillResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoundsBillResult{}, observation.ErrStale
	}
	if err = p.journal.CommitOwnerMethod(call, goal, method, "", plan); err != nil {
		return RoundsBillResult{}, err
	}
	return RoundsBillResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// unclaimedBenches copies the bench census with every recipe this load has
// already claimed, or admitted and not yet written, on its bench marked
// unavailable, so the selection lands
// on a bench and recipe the bill claim table still admits.
func (r *RoundsBillPlanner) unclaimedBenches(ctx context.Context, snapshot domain.GenerationSnapshot, benches domain.Fact[[]policy.ProductionBench]) (domain.Fact[[]policy.ProductionBench], error) {
	rows, known := benches.Value()
	if !known {
		return benches, nil
	}
	out := make([]policy.ProductionBench, 0, len(rows))
	for _, bench := range rows {
		bench.Recipes = append([]policy.ProductionRecipe(nil), bench.Recipes...)
		for i, recipe := range bench.Recipes {
			claimed, err := r.reviewer.player.journal.BillClaimed(ctx, snapshot, bench.ID, recipe.Name)
			if err != nil {
				return benches, err
			}
			if !claimed {
				// An admitted bill not yet written: the sibling planner of
				// this step chose the bench from the same before-token.
				if claimed, err = r.reviewer.player.journal.BillPending(ctx, bench.ID, recipe.Name); err != nil {
					return benches, err
				}
			}
			if claimed {
				bench.Recipes[i].Available = domain.Known(false)
			}
		}
		out = append(out, bench)
	}
	return domain.Known(out), nil
}

// butcherSpotSeparated reports whether MaintainButcherSpot has tried its
// separated-spot method this epoch; a review that binds no such goal has
// nothing left to try.
func (p *Player) butcherSpotSeparated(ctx context.Context, review store.Rounds) (bool, error) {
	id, bound := review.ProjectFor(policy.MaintainButcherSpot)
	if !bound {
		return true, nil
	}
	project, err := p.journal.LoadProject(ctx, id)
	if err != nil {
		return false, err
	}
	_, err = p.journal.LoadOwnerMethod(ctx, project, "butcher-spot-separated")
	if errors.Is(err, store.ErrNotFound) {
		return false, nil
	}
	return err == nil, err
}
