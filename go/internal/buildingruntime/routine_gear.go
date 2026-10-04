package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"sort"
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

// RoutineGearSource reads the same generic colony census (with planning
// facts requested) that carries the already-validated gear census
// (bridge.ReadColonyFacts runs validateColonyGear on it internally); no
// dedicated gear read is needed to plan a method, only to refresh CAS tokens
// immediately before dispatch (see bridge.ReadGearReplacement, used by
// GearReplaceBoundary). ReadGearBenches and ReadSupplyStock feed the
// workshop-bill half (GearProduce): a fresh bench/recipe census and the
// ingredient stock funding it, respectively, gathered only when no
// replace-candidate is already pending (SelectGearMethod always prefers
// wearing an existing item over crafting a new one). Native checks the bill against
// live state when the ProductionBillIntent applies.
type RoutineGearSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error)
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error)
}
type RoutineGearPlanner struct {
	reviewer *Rounder
	native   RoutineGearSource
}
type RoutineGearResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutineGearPlanner(reviewer *Rounder, native RoutineGearSource) (*RoutineGearPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineGearPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineGearPlanner{reviewer, native}, nil
}

// gearObservationFacts decodes the frame's gear census the way rounds
// does (observation.GearFacts). The planner reads a fresh census of its own
// immediately before proposing a method, the same way RoutineEquipPlanner
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
func (r *RoutineGearPlanner) stampCreepjoiners(call context.Context, identity *c.Identity, state ControlState, census policy.GearObservation, defs observation.GearDefinitions) (policy.GearObservation, error) {
	source, ok := r.native.(RoutineEquipSource)
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

func (r *RoutineGearPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineGearResult, error) {
	review, err := r.reviewer.player.journal.LoadRounds(call)
	if err != nil {
		return RoutineGearResult{}, err
	}
	limit, refused, err := equipmentSlots(call, r.reviewer.player, review)
	if err != nil || !refused.IsZero() {
		return RoutineGearResult{Verdict: refused}, err
	}
	var admitted RoutineGearResult
	for i := 0; i < limit; i++ {
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

// equipmentSlots is how many MaintainEquipment methods the gear and armory
// planners may start in one step. There is no development slot or pawn limit:
// each gear plan is already confined to one pawn and one item.
func equipmentSlots(call context.Context, p *Player, review store.Rounds) (int, Verdict, error) {
	return 16, Verdict{}, nil
}

// equipmentRanked: the ranking gave MaintainEquipment a slot (or it already
// holds one). A goal the colony stage has not raised yet waits with a
// reason and holds none, and a wear order or bill admitted for it is refused
// on every tick. Settings writes hold no slot and go ahead regardless. A
// review with no rows at all predates the ranking.
func equipmentRanked(review store.Rounds) bool {
	if len(review.Development.Rows) == 0 {
		return true
	}
	for _, row := range review.Development.Rows {
		if row.Goal == policy.MaintainEquipment {
			return row.Selected || row.Committed
		}
	}
	return false
}

func (r *RoutineGearPlanner) stepOne(call, epoch context.Context, arbiter *stepArbiter) (RoutineGearResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineGearResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineGearResult{}, fmt.Errorf("%w: stepOne: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoutineGearResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineGearResult{Verdict: BuildingReasonNoReview}, nil
	}
	call, recorded := recordPlannerStep(call, policy.MaintainEquipment, state.Snapshot, review.Tick)
	defer recorded()
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainEquipment)
	if err != nil {
		return RoutineGearResult{}, err
	}
	if !workable {
		return RoutineGearResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	busy := map[domain.PawnID]bool{}
	claimed := map[string]bool{}
	pruning := false
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineGearResult{}, err
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
					return RoutineGearResult{Verdict: BuildingReasonExistingWork}, nil
				}
			}
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.reviewer.colonyFacts(call, r.native, identity, true)
	if err != nil {
		return RoutineGearResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineGearResult{}, fmt.Errorf("%w: stepOne: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineGearResult{}, fmt.Errorf("%w: stepOne: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	gear := observed.GetPlanning().GetObserved().GetGear()
	if gear == nil || observed.ColonistCount == nil || uint32(len(gear.GetPawns())) != observed.GetColonistCount() {
		return RoutineGearResult{Verdict: waitFor(WaitMethodUsed, "gear_observation")}, nil
	}
	things, err := frameThings(call, r.native, identity)
	if err != nil {
		return RoutineGearResult{}, err
	}
	defs, err := gearDefinitions(call, r.native, identity)
	if err != nil {
		return RoutineGearResult{}, err
	}
	observation, known := gearObservationFacts(observed, bridge.Tables{Things: things}, defs)
	if !known {
		return RoutineGearResult{Verdict: fieldUnavailable("gear_census")}, nil
	}
	if observation, err = r.stampCreepjoiners(call, identity, state, observation, defs); err != nil {
		return RoutineGearResult{}, err
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
	// Configure vanilla dressing before choosing individual replacements. The
	// shared goal and Hands executor own this settings operation like wear work.
	// Every pawn that needs a policy is admitted in the same step: one write per
	// development slot per round spent a whole 60k-tick window assigning eight
	// colonists before any wear order or bill (#660). A write whose CAS token an
	// earlier write in the batch staled is cancelled and re-admitted next round,
	// so the batch converges in about one round per distinct role policy.
	var policies RoutineGearResult
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
			return RoutineGearResult{}, err
		}
		result, err := r.commitPolicyPlan(call, epoch, state, started, &goal, "outfit-prune", fmt.Sprint(prune.IDs()), func(id domain.ActionID) (domain.Action, error) {
			return domain.NewPolicyPruneAction(id, prune)
		})
		if err != nil || result.Plan != "" {
			return result, err
		}
	}
	if !equipmentRanked(review) {
		return RoutineGearResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	// SelectGearMethod always prefers wearing an already-observed replacement
	// candidate over crafting a new one and never reaches bench/recipe
	// selection while any candidate is pending, so the bench census (extra
	// native round trips) is only worth gathering once none exist. A wear
	// order still needs the candidate's own definition in a funded stock
	// (gearIngredients), so its stock is read either way (#233).
	hasCandidates := false
	candidateNames := map[string]bool{}
	for _, pawn := range observation.Pawns {
		if candidates, known := pawn.Candidates.Value(); known && len(candidates) > 0 {
			hasCandidates = true
			for _, c := range candidates {
				candidateNames[string(c.Definition)] = true
			}
		}
	}
	benchesFact := domain.Unknown[[]policy.GearBench]()
	tokens := map[string]string{}
	var stock []policy.Stock
	if hasCandidates {
		names := make([]string, 0, len(candidateNames))
		for name := range candidateNames {
			names = append(names, name)
		}
		sort.Strings(names)
		if stock, _, err = r.native.ReadSupplyStock(call, identity, names); err != nil {
			return RoutineGearResult{}, err
		}
	} else {
		census, _, err := r.native.ReadGearBenches(call, identity)
		if err != nil {
			return RoutineGearResult{}, err
		}
		benches := make([]policy.GearBench, 0, len(census))
		for _, row := range census {
			benches = append(benches, row.Bench)
			tokens[row.Bench.ID] = row.Token
		}
		names := recipeIngredientNames(census, "")
		if len(names) > 0 {
			stock, _, err = r.native.ReadSupplyStock(call, identity, names)
			if err != nil {
				return RoutineGearResult{}, err
			}
		}
		benchesFact = domain.Known(benches)
	}
	// Construction and other pawns' bill jobs hold their material (#1354).
	request := policy.GearPlanningRequest{Observation: domain.Known(observation), Seen: seen, Benches: benchesFact, Stock: stock, Holds: r.reviewer.census.materialHolds(state.Snapshot)}
	snap.NoteGearMethod(call, request)
	choice, err := policy.SelectGearMethod(request)
	if err != nil {
		return RoutineGearResult{}, err
	}
	id := domain.MintPlanID()
	var action domain.Action
	switch choice.Kind {
	case policy.GearReplace:
		definition, ok := gearCandidateDefinition(observation, choice.Pawn, choice.Target)
		if !ok {
			return RoutineGearResult{}, fmt.Errorf("%w: stepOne: !ok", ErrControl)
		}
		if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(choice.Pawn)}) {
			return RoutineGearResult{Verdict: claimHeld("colonist")}, nil
		}
		replace, err := domain.NewGearReplace(domain.PawnID(choice.Pawn), choice.Target, definition)
		if err != nil {
			return RoutineGearResult{}, err
		}
		if action, err = domain.NewGearReplaceAction(domain.ActionID(fmt.Sprintf("%s-0", id)), replace); err != nil {
			return RoutineGearResult{}, err
		}
	case policy.GearProduce:
		_, ok := tokens[choice.Bench]
		if !ok {
			return RoutineGearResult{}, fmt.Errorf("%w: stepOne: !ok", ErrControl)
		}
		// A finite batch covers the colony gap, using only funded ingredients.
		ingredients := make([]string, len(choice.Filter))
		for i, resource := range choice.Filter {
			ingredients[i] = string(resource)
		}
		bill, err := domain.NewProductionBill(choice.Bench, choice.Recipe, domain.GearBatch, choice.Count, ingredients...)
		if err != nil {
			return RoutineGearResult{}, err
		}
		if action, err = domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill); err != nil {
			return RoutineGearResult{}, err
		}
	default:
		if len(busy) > 0 {
			return RoutineGearResult{Verdict: BuildingReasonExistingWork}, nil
		}
		return RoutineGearResult{Verdict: waitFor(WaitMethodUsed, "gear_bill")}, nil
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineGearResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineGearResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineGearResult{}, fmt.Errorf("%w: stepOne: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, choice.ID, plan); err != nil {
		return RoutineGearResult{}, err
	}
	return RoutineGearResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
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
func (r *RoutineGearPlanner) commitPolicyPlan(call, epoch context.Context, state ControlState, started time.Time, goal *store.StandardState, prefix, value string, build func(domain.ActionID) (domain.Action, error)) (RoutineGearResult, error) {
	p := r.reviewer.player
	method := policyMethod(*goal, prefix, value)
	if seenMethod(*goal, method) {
		return RoutineGearResult{}, nil
	}
	id := domain.MintPlanID()
	action, err := build(domain.ActionID(string(id) + "-0"))
	if err != nil {
		return RoutineGearResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineGearResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineGearResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineGearResult{}, fmt.Errorf("%w: commitPolicyPlan: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if *goal, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoutineGearResult{}, err
	}
	return RoutineGearResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
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
