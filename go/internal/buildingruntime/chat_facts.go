package buildingruntime

import (
	"context"
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/interpreter"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ChatFactsNative narrows *bridge.Client to the three reads chat's fact
// gatherer needs: the colony census, the named colonist roster and the
// custody census every observed humanlike appears in.
type ChatFactsNative interface {
	ReadColonyFacts(ctx context.Context, identity *c.Identity, planning bool, definitions []string) (*o.ColonyFactsReply, bridge.Result, error)
	ReadHomeColonists(ctx context.Context, identity *c.Identity) (*o.ListPawnsReply, bridge.Result, error)
	ReadRoutinePopulation(ctx context.Context, identity *c.Identity) (bridge.PrisonerCensus, bridge.Result, error)
}

// ChatFactsJournal narrows *store.Store to the policy state chat reads: the
// goals the routine reviewer and the player currently track, and every
// policy input a guidance nudge may change.
type ChatFactsJournal interface {
	LoadRoutineReview(context.Context) (store.RoutineReview, error)
	LoadGoal(context.Context, domain.GoalID) (store.GoalState, error)
	PlayerGoals(context.Context, store.World) (map[domain.GoalKind]domain.GoalID, error)
	CurrentPopulationPolicy(context.Context, store.World) (domain.PopulationPolicy, error)
	CurrentExpeditionPolicy(context.Context, store.World) (domain.ExpeditionPolicy, error)
	ResourcePolicies(context.Context, store.World) ([]domain.ResourceDirective, error)
	PopulationDecisions(context.Context, store.World) ([]domain.PopulationDirective, error)
}

// GatherChatFacts assembles the bounded interpreter.Facts for one chat
// message plus the domain.GenerationSnapshot the interpreter call must be
// issued against. It is a read-only fact gather: nothing here chains through
// boundary CAS machinery, because every nudge the guidance may carry
// re-establishes its own admission when the policy input is submitted. The
// returned generation carries the native generation the colony read observed
// under the world's root plan, so a nudge applied against it is judged
// against the same authority the routine reviewer uses.
func GatherChatFacts(ctx context.Context, native ChatFactsNative, journal ChatFactsJournal, world store.World) (interpreter.Facts, domain.GenerationSnapshot, error) {
	var none interpreter.Facts
	if native == nil || journal == nil {
		return none, domain.GenerationSnapshot{}, errors.New("chat facts native reader and journal required")
	}
	if err := world.Validate(); err != nil {
		return none, domain.GenerationSnapshot{}, err
	}
	identity := &c.Identity{ColonyId: proto.String(string(world.Colony)), LoadToken: proto.String(string(world.Load)), MapId: proto.Int32(int32(world.Map))}
	colony, _, err := native.ReadColonyFacts(ctx, identity, false, nil)
	if err != nil {
		return none, domain.GenerationSnapshot{}, err
	}
	observed := colony.GetObserved()
	if observed == nil || observed.Context == nil || observed.Context.NativeGeneration == nil {
		return none, domain.GenerationSnapshot{}, errors.New("colony facts read returned no usable context")
	}
	current := domain.GenerationSnapshot{Colony: world.Colony, Load: world.Load, Map: world.Map, Plan: store.RootPlanID(world), Revision: 1, Native: domain.NativeGeneration(observed.Context.GetNativeGeneration())}
	if err = current.Validate(); err != nil {
		return none, domain.GenerationSnapshot{}, err
	}
	facts := interpreter.Facts{Generation: current, Pawns: []interpreter.Pawn{}, Goals: []interpreter.Goal{}, PolicyResources: []string{}, ResourcePolicies: []interpreter.ResourcePolicy{}, PopulationDecisions: []interpreter.PopulationDecision{}}
	facts.Colony = interpreter.Colony{Tick: domain.Tick(observed.Context.GetTick()), Biome: observed.GetBiome(), ColonistCount: observed.GetColonistCount(), WorkerCount: observed.GetWorkerCount(), BedCapacity: observed.GetBedCapacity(), Resources: []interpreter.Resource{}}
	if observed.FoodRunwayDays != nil {
		days := observed.GetFoodRunwayDays()
		facts.Colony.FoodRunwayDays = &days
	}
	if observed.OutdoorTemperatureC != nil {
		temperature := observed.GetOutdoorTemperatureC()
		facts.Colony.OutdoorTemperatureC = &temperature
	}
	stocked := map[string]bool{}
	for _, row := range observed.GetResources() {
		if row == nil || row.DefName == nil || stocked[row.GetDefName()] {
			continue
		}
		stocked[row.GetDefName()] = true
		facts.Colony.Resources = append(facts.Colony.Resources, interpreter.Resource{DefName: row.GetDefName(), Units: row.GetUnits()})
	}
	policyResources := map[string]bool{}
	for _, row := range observed.GetPolicyResources() {
		if row == nil || row.GetDefName() == "" || policyResources[row.GetDefName()] {
			continue
		}
		policyResources[row.GetDefName()] = true
		facts.PolicyResources = append(facts.PolicyResources, row.GetDefName())
	}

	roster, _, err := native.ReadHomeColonists(ctx, identity)
	if err != nil {
		return none, domain.GenerationSnapshot{}, err
	}
	rosterObserved := roster.GetObserved()
	if rosterObserved == nil {
		return none, domain.GenerationSnapshot{}, errors.New("home colonist read returned no usable roster")
	}
	pawns := map[domain.PawnID]int{}
	for _, row := range rosterObserved.Pawns {
		if row == nil || row.Pawn == nil || row.Pawn.GetId() == "" {
			continue
		}
		id := domain.PawnID(row.Pawn.GetId())
		if _, seen := pawns[id]; seen {
			continue
		}
		pawns[id] = len(facts.Pawns)
		facts.Pawns = append(facts.Pawns, interpreter.Pawn{ID: id, Label: row.Pawn.GetLabel(), Colonist: true, Downed: row.GetDowned()})
	}
	census, _, err := native.ReadRoutinePopulation(ctx, identity)
	if err != nil {
		return none, domain.GenerationSnapshot{}, err
	}
	if custody, known := census.Custody.Value(); known {
		for _, row := range custody {
			if dead, _ := row.Dead.Value(); dead || row.Pawn == "" {
				continue
			}
			pawn := interpreter.Pawn{ID: row.Pawn}
			pawn.Downed, _ = row.Downed.Value()
			pawn.Prisoner, _ = row.Prisoner.Value()
			pawn.Guest, _ = row.Guest.Value()
			pawn.Hostile, _ = row.Hostile.Value()
			if index, seen := pawns[row.Pawn]; seen {
				existing := &facts.Pawns[index]
				existing.Downed, existing.Prisoner, existing.Guest, existing.Hostile = existing.Downed || pawn.Downed, pawn.Prisoner, pawn.Guest, pawn.Hostile
				continue
			}
			pawns[row.Pawn] = len(facts.Pawns)
			facts.Pawns = append(facts.Pawns, pawn)
		}
	}

	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return none, domain.GenerationSnapshot{}, err
	}
	goals := map[domain.GoalID]bool{}
	addGoal := func(id domain.GoalID, kind string) error {
		if goals[id] {
			return nil
		}
		state, err := journal.LoadGoal(ctx, id)
		if errors.Is(err, store.ErrNotFound) {
			return nil
		}
		if err != nil {
			return err
		}
		g := state.Goal
		if g.Snapshot.Colony != world.Colony || g.Snapshot.Load != world.Load || g.Snapshot.Map != world.Map || state.Retired || g.Status == domain.GoalCancelled || g.Status == domain.GoalInvalidated {
			return nil
		}
		goals[id] = true
		facts.Goals = append(facts.Goals, interpreter.Goal{ID: id, Kind: kind, Source: g.Source, Status: g.Status, Need: g.Need, Priority: g.Priority})
		return nil
	}
	if review.Snapshot.Colony == world.Colony && review.Snapshot.Load == world.Load && review.Snapshot.Map == world.Map {
		for _, binding := range review.Goals {
			if err = addGoal(binding.Goal, string(binding.Need)); err != nil {
				return none, domain.GenerationSnapshot{}, err
			}
		}
	}
	playerGoals, err := journal.PlayerGoals(ctx, world)
	if err != nil {
		return none, domain.GenerationSnapshot{}, err
	}
	kinds := make([]domain.GoalKind, 0, len(playerGoals))
	for kind := range playerGoals {
		kinds = append(kinds, kind)
	}
	sort.Slice(kinds, func(i, j int) bool { return kinds[i] < kinds[j] })
	for _, kind := range kinds {
		if err = addGoal(playerGoals[kind], string(kind)); err != nil {
			return none, domain.GenerationSnapshot{}, err
		}
	}

	population, err := journal.CurrentPopulationPolicy(ctx, world)
	if err != nil && !errors.Is(err, store.ErrNotFound) {
		return none, domain.GenerationSnapshot{}, err
	}
	if err == nil && population.Set() {
		facts.PopulationPolicy = &interpreter.PopulationPolicy{Maximum: population.Maximum(), FoodDays: population.FoodDays()}
	}
	expedition, err := journal.CurrentExpeditionPolicy(ctx, world)
	if err != nil {
		return none, domain.GenerationSnapshot{}, err
	}
	facts.ExpeditionPolicy = interpreter.ExpeditionPolicy{
		MinimumHomeColonists: expedition.MinimumHomeColonists(), MinimumHomeFoodDays: expedition.MinimumHomeFoodDays(),
		TravelFoodMarginDays: expedition.TravelFoodMarginDays(), MaximumTravelDays: expedition.MaximumTravelDays(),
		MaximumCaravans: expedition.MaximumCaravans(), MinimumGoodwill: expedition.MinimumGoodwill(),
		MinimumDestinationTemperature: expedition.MinimumDestinationTemperature(), MaximumDestinationTemperature: expedition.MaximumDestinationTemperature(),
		KeepHomeDoctor: expedition.KeepHomeDoctor(), RequireReturnStorage: expedition.RequireReturnStorage(),
	}
	directives, err := journal.ResourcePolicies(ctx, world)
	if err != nil {
		return none, domain.GenerationSnapshot{}, err
	}
	for _, directive := range directives {
		facts.ResourcePolicies = append(facts.ResourcePolicies, interpreter.ResourcePolicy{Resource: directive.Resource(), Reserve: directive.Reserve(), Spending: directive.Spending()})
	}
	decisions, err := journal.PopulationDecisions(ctx, world)
	if err != nil {
		return none, domain.GenerationSnapshot{}, err
	}
	for _, decision := range decisions {
		facts.PopulationDecisions = append(facts.PopulationDecisions, interpreter.PopulationDecision{Pawn: decision.Pawn(), Decision: decision.Decision()})
	}
	return facts, current, nil
}
