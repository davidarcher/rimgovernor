package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoundsGearSource reads the same generic colony census (with planning
// facts requested) that carries the already-validated gear census
// (bridge.ReadColonyFacts runs validateColonyGear on it internally); no
// dedicated gear read is needed to plan a method, only to refresh CAS tokens
// immediately before dispatch (see bridge.ReadGearReplacement, used by
// GearReplaceBoundary). ReadGearBenches feeds the workshop-bill half
// (GearProduce): a fresh bench/recipe census, gathered only when no
// replace-candidate is already pending (SelectGearMethod always prefers
// wearing an existing item over crafting a new one). The bill is placed
// whatever the stock; its ingredients are demand (#2373).
type RoundsGearSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error)
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
}
type RoundsGearPlanner struct {
	reviewer *Rounder
	native   RoundsGearSource
}
type RoundsGearResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsGearPlanner(reviewer *Rounder, native RoundsGearSource) (*RoundsGearPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsGearPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsGearPlanner{reviewer, native}, nil
}

// gearObservationFacts decodes the frame's gear census the way rounds
// does (observation.GearFacts). The planner reads a fresh census of its own
// immediately before proposing a method, the same way RoundsEquipPlanner
// rereads combat pawns and loose weapons rather than reusing the review's
// cached facts. False when the frame cannot ground the census.
func gearObservationFacts(observed *o.ColonyFactsSnapshot, tables bridge.Tables, defs observation.GearDefinitions) (policy.GearObservation, bool) {
	return observation.GearFacts(observed, tables, defs).Value()
}

// gearDefinitions are the catalog and finished research the gear census is
// decoded against, read from source.
func gearDefinitions(call context.Context, source any, identity *c.Identity) (observation.GearDefinitions, error) {
	return observation.ReadGearDefinitions(call, source, identity)
}

// stampCreepjoiners flags the census colonists that are creepjoiners with an
// unrevealed downside (#1962), from a fresh combat-pawn read the way the
// review does, so the apparel policy this planner writes matches the review's.
// A source that serves no combat-pawn read leaves the census unflagged.
func (r *RoundsGearPlanner) stampCreepjoiners(call context.Context, identity *c.Identity, state ControlState, census policy.GearObservation, defs observation.GearDefinitions) (policy.GearObservation, error) {
	source, ok := r.native.(RoundsEquipSource)
	if !ok {
		return census, nil
	}
	ids := make([]string, 0, len(census.Pawns))
	for _, p := range census.Pawns {
		ids = append(ids, string(p.Pawn))
	}
	reply, _, err := source.ReadCombatPawns(call, identity, ids)
	if err != nil {
		return census, err
	}
	pawns := reply.GetObserved()
	if pawns == nil {
		return census, fmt.Errorf("%w: stampCreepjoiners: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(pawns.Context, state.Snapshot); err != nil {
		return census, fmt.Errorf("%w: stampCreepjoiners: %v", ErrControl, err)
	}
	return observation.StampGearCreepjoiners(census, pawns, defs.Catalog.CreepJoinerDownsides()), nil
}

func gearCandidateDefinition(observation policy.GearObservation, pawn policy.PawnID, target string) (string, bool) {
	for _, p := range observation.Pawns {
		if p.Pawn != pawn {
			continue
		}
		candidates, known := p.Candidates.Value()
		if !known {
			return "", false
		}
		for _, c := range candidates {
			if c.Target == target {
				return string(c.Definition), true
			}
		}
	}
	return "", false
}

// maxGearMethods bounds planning cost; each method names one pawn and item.
const maxGearMethods = 16

func (r *RoundsGearPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsGearResult, error) {
	var admitted RoundsGearResult
	for i := 0; i < maxGearMethods; i++ {
		result, err := r.stepOne(call, epoch, arbiter)
		if err != nil {
			return result, err
		}
		if result.Verdict != BuildingReasonAdmitted {
			if admitted.Plan != "" {
				return admitted, nil
			}
			return result, nil
		}
		admitted = result
	}
	return admitted, nil
}

func (r *RoundsGearPlanner) stepOne(call, epoch context.Context, arbiter *stepArbiter) (RoundsGearResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsGearResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsGearResult{}, fmt.Errorf("%w: stepOne: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsGearResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsGearResult{Verdict: BuildingReasonNoReview}, nil
	}
	call, recorded := recordPlannerStep(call, policy.MaintainEquipment, state.Snapshot, review.Tick)
	defer recorded()
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainEquipment)
	if err != nil {
		return RoundsGearResult{}, err
	}
	if !workable {
		return RoundsGearResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	busy := map[domain.PawnID]bool{}
	claimed := map[string]bool{}
	pruning := false
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsGearResult{}, err
		}
		if store.PlanOpen(plan) {
			for _, action := range plan.Spec.Actions() {
				if wear, ok := action.GearReplace(); ok {
					busy[wear.Pawn()] = true
					claimed[wear.Thing()] = true
				} else if settings, ok := action.ApparelPolicy(); ok {
					busy[settings.Pawn()] = true
				} else if _, ok := action.PolicyPrune(); ok {
					pruning = true
				} else {
					return RoundsGearResult{Verdict: BuildingReasonExistingWork}, nil
				}
			}
		}
	}
	// A bill whose need is gone (the owner stayed Met) is removed first (#2411).
	if plan, err := r.reviewer.removeStaleBill(call, epoch, arbiter, state, goal, policy.MaintainEquipment); err != nil || plan != "" {
		return RoundsGearResult{Verdict: BuildingReasonAdmitted, Plan: plan}, err
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.reviewer.colonyFacts(call, r.native, identity, true)
	if err != nil {
		return RoundsGearResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoundsGearResult{}, fmt.Errorf("%w: stepOne: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoundsGearResult{}, fmt.Errorf("%w: stepOne: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	gear := observed.GetPlanning().GetObserved().GetGear()
	if gear == nil || observed.ColonistCount == nil || uint32(len(gear.GetPawns())) != observed.GetColonistCount() {
		return RoundsGearResult{Verdict: waitFor(WaitMethodUsed, "gear_observation")}, nil
	}
	things, err := frameThings(call, r.native, identity)
	if err != nil {
		return RoundsGearResult{}, err
	}
	defs, err := gearDefinitions(call, r.native, identity)
	if err != nil {
		return RoundsGearResult{}, err
	}
	observation, known := gearObservationFacts(observed, bridge.Tables{Things: things}, defs)
	if !known {
		return RoundsGearResult{Verdict: fieldUnavailable("gear_census")}, nil
	}
	if observation, err = r.stampCreepjoiners(call, identity, state, observation, defs); err != nil {
		return RoundsGearResult{}, err
	}
	for i := range observation.Pawns {
		pawn := &observation.Pawns[i]
		pawn.Blocked = pawn.Blocked || busy[domain.PawnID(pawn.Pawn)]
		candidates, _ := pawn.Candidates.Value()
		available := []policy.GearCandidate{}
		for _, c := range candidates {
			if !claimed[c.Target] {
				available = append(available, c)
			}
		}
		pawn.Candidates = domain.Known(available)
	}
	// An apparel or armor bill outside every loadout's replacements goes
	// whatever the owner's finding (#2433); the armory judges weapons.
	wanted, known, err := policy.GearBillsWanted(domain.Known(observation))
	if err != nil {
		return RoundsGearResult{}, err
	}
	if known {
		judge := func(b policy.StaleBill) (bool, bool) {
			return !policy.WeaponBill(b.Products), policy.BillWanted(b.Products, wanted)
		}
		if plan, err := r.reviewer.removeUnwantedBill(call, epoch, arbiter, state, review, goal, policy.MaintainEquipment, judge); err != nil || plan != "" {
			return RoundsGearResult{Verdict: BuildingReasonAdmitted, Plan: plan}, err
		}
	}
	// Configure vanilla dressing before choosing individual replacements.
	var policies RoundsGearResult
	for _, pawn := range observation.Pawns {
		if pawn.Blocked {
			continue
		}
		value, needed := policy.DesiredApparelPolicy(pawn)
		if !needed {
			continue
		}
		if seenMethod(goal, policyMethod(goal, "apparel-policy", value.Encoded())) || !arbiter.tryClaim([]domain.PawnID{value.Pawn()}) {
			continue
		}
		result, err := r.commitPolicyPlan(call, epoch, state, started, &goal, "apparel-policy", value.Encoded(), func(id domain.ActionID) (domain.Action, error) {
			return domain.NewApparelPolicyAction(id, value)
		})
		if err != nil {
			return result, err
		}
		policies = result
	}
	if policies.Plan != "" {
		return policies, nil
	}
	// Once every pawn is on its own outfit, every other outfit (vanilla and
	// player-made included) is pruned (#1302, #1298). The other policy
	// databases wait for their own per-pawn planners.
	if drop := observation.OutfitsToPrune(); len(drop) > 0 && !pruning {
		prune, err := domain.NewPolicyPrune(domain.OutfitPolicies, drop)
		if err != nil {
			return RoundsGearResult{}, err
		}
		result, err := r.commitPolicyPlan(call, epoch, state, started, &goal, "outfit-prune", fmt.Sprint(prune.IDs()), func(id domain.ActionID) (domain.Action, error) {
			return domain.NewPolicyPruneAction(id, prune)
		})
		if err != nil || result.Plan != "" {
			return result, err
		}
	}
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	// SelectGearMethod always prefers wearing an already-observed replacement
	// candidate over crafting a new one and never reaches bench/recipe
	// selection while any candidate is pending, so the bench census (extra
	// native round trips) is only worth gathering once none exist.
	hasCandidates := false
	for _, pawn := range observation.Pawns {
		if candidates, known := pawn.Candidates.Value(); known && len(candidates) > 0 {
			hasCandidates = true
		}
	}
	benchesFact := domain.Unknown[[]policy.GearBench]()
	tokens := map[string]string{}
	if !hasCandidates {
		census, _, err := r.native.ReadGearBenches(call, identity)
		if err != nil {
			return RoundsGearResult{}, err
		}
		benches := make([]policy.GearBench, 0, len(census))
		for _, row := range census {
			benches = append(benches, row.Bench)
			tokens[row.Bench.ID] = row.Token
		}
		benchesFact = domain.Known(benches)
	}
	items, err := r.reviewer.itemFacts(call, state.Snapshot)
	if err != nil {
		return RoundsGearResult{}, err
	}
	request := policy.GearPlanningRequest{Observation: domain.Known(observation), Seen: seen, Benches: benchesFact, StuffCategories: items.StuffCategories}
	snap.NoteGearMethod(call, request)
	choice, err := policy.SelectGearMethod(request)
	if err != nil {
		return RoundsGearResult{}, err
	}
	id := domain.MintPlanID()
	var action domain.Action
	switch choice.Kind {
	case policy.GearReplace:
		definition, ok := gearCandidateDefinition(observation, choice.Pawn, choice.Target)
		if !ok {
			return RoundsGearResult{}, fmt.Errorf("%w: stepOne: !ok", ErrControl)
		}
		if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(choice.Pawn)}) {
			return RoundsGearResult{Verdict: claimHeld("colonist")}, nil
		}
		replace, err := domain.NewGearReplace(domain.PawnID(choice.Pawn), choice.Target, definition)
		if err != nil {
			return RoundsGearResult{}, err
		}
		if action, err = domain.NewGearReplaceAction(domain.ActionID(fmt.Sprintf("%s-0", id)), replace); err != nil {
			return RoundsGearResult{}, err
		}
	case policy.GearProduce:
		_, ok := tokens[choice.Bench]
		if !ok {
			return RoundsGearResult{}, fmt.Errorf("%w: stepOne: !ok", ErrControl)
		}
		// A finite batch covers the colony gap, using only funded ingredients.
		ingredients := make([]string, len(choice.Filter))
		for i, resource := range choice.Filter {
			ingredients[i] = string(resource)
		}
		bill, err := domain.NewProductionBill(choice.Bench, choice.Recipe, domain.GearBatch, choice.Count, ingredients...)
		if err != nil {
			return RoundsGearResult{}, err
		}
		if action, err = domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill); err != nil {
			return RoundsGearResult{}, err
		}
	default:
		if len(busy) > 0 {
			return RoundsGearResult{Verdict: BuildingReasonExistingWork}, nil
		}
		return RoundsGearResult{Verdict: waitFor(WaitMethodUsed, "gear_bill")}, nil
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsGearResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsGearResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsGearResult{}, fmt.Errorf("%w: stepOne: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, choice.ID, plan); err != nil {
		return RoundsGearResult{}, err
	}
	return RoundsGearResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// policyMethod is the method id of a policy write or prune: the Episode
// and the action's canonical value, so a repeat of the same write is seen.
func policyMethod(goal store.StandardState, prefix, value string) domain.MethodID {
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Standard.ID, goal.Standard.Episode, value)))
	return domain.MethodID(fmt.Sprintf("%s-%x", prefix, digest[:16]))
}

func seenMethod(goal store.StandardState, method domain.MethodID) bool {
	for _, m := range goal.Methods {
		if m.Method == method {
			return true
		}
	}
	return false
}

// commitPolicyPlan admits a one-action policy plan (an outfit write or the
// outfit prune) under the goal unless the same method was already tried;
// the zero result is a repeat. goal is advanced to the committed revision.
func (r *RoundsGearPlanner) commitPolicyPlan(call, epoch context.Context, state ControlState, started time.Time, goal *store.StandardState, prefix, value string, build func(domain.ActionID) (domain.Action, error)) (RoundsGearResult, error) {
	p := r.reviewer.player
	method := policyMethod(*goal, prefix, value)
	if seenMethod(*goal, method) {
		return RoundsGearResult{}, nil
	}
	id := domain.MintPlanID()
	action, err := build(domain.ActionID(string(id) + "-0"))
	if err != nil {
		return RoundsGearResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsGearResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsGearResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsGearResult{}, fmt.Errorf("%w: commitPolicyPlan: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if *goal, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsGearResult{}, err
	}
	return RoundsGearResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// frameThingsSource serves the newest frame's things table (#1342).
type frameThingsSource interface {
	FrameThings(context.Context, *c.Identity) (bridge.Things, error)
}

// frameThings is native's newest things table, empty when native serves
// none: every gear reference then stays unresolved.
func frameThings(ctx context.Context, native any, identity *c.Identity) (bridge.Things, error) {
	if source, ok := native.(frameThingsSource); ok {
		return source.FrameThings(ctx, identity)
	}
	return bridge.Things{}, nil
}

// gearDef is a gear reference's definition from its things table row, ""
// when the table does not hold it yet.
func gearDef(things bridge.Things, ref bridge.Reference) string {
	row, _ := things.Row(ref)
	return row.GetThing().GetDefName()
}
