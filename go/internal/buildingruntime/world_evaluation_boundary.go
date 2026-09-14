package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// WorldEvaluationNative is the read-only native surface WorldEvaluation
// needs: the same Identity call every clock-scheduled planner uses to learn
// the current authoritative snapshot, the world-progression census
// EvaluateWorld reads caravans/quests from, and the colony-facts census
// EvaluateWorld reads home resource stock from. Nothing here ever
// dispatches a native write -- this family only ever reports on already
// observed facts, never accepts a quest, escalates diplomacy or orders an
// expedition, the same restriction policy.EvaluateWorld's own doc comment
// states.
type WorldEvaluationNative interface {
	Identity(context.Context) (*l.IdentityReply, bridge.Result, error)
	ReadWorldProgression(context.Context, *c.Identity, bool) (bridge.WorldProgressionRead, bridge.Result, error)
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
}

// WorldEvaluation composes a same-tick read of native's world-progression
// and colony-facts censuses into policy.EvaluateWorld's read-only advisory
// report. It never claims a plan slot or competes with a goal the way
// RoutineBuildingPlanner implementations do; it is a plain read gated only
// by Player.enter's existing native-call serialization, the same
// concurrency discipline CaravanJourneyTracker uses for its own
// world-progression poll.
type WorldEvaluation struct {
	player *Player
	native WorldEvaluationNative
	policy policy.WorldEvaluationPolicy
}

// NewWorldEvaluation builds a WorldEvaluation boundary. player and native
// must be non-nil.
func NewWorldEvaluation(player *Player, native WorldEvaluationNative, p policy.WorldEvaluationPolicy) (*WorldEvaluation, error) {
	if player == nil || native == nil {
		return nil, ErrControl
	}
	return &WorldEvaluation{player: player, native: native, policy: p}, nil
}

// Read reports the current read-only world-evaluation advisory. It does not
// require player control to be enabled -- unlike CaravanJourneyTracker's
// background reconciliation, nothing here writes local tracking state, so
// there is no store consistency this read needs an active control epoch to
// protect. It still serializes through Player.enter so this read never
// interleaves with an in-flight native write.
func (w *WorldEvaluation) Read(ctx context.Context) (policy.WorldEvaluationReport, error) {
	call, _, done, err := w.player.enter(ctx, false)
	if err != nil {
		return policy.WorldEvaluationReport{}, err
	}
	defer done()
	identityReply, _, err := w.native.Identity(call)
	if err != nil {
		return policy.WorldEvaluationReport{}, err
	}
	decoded, err := observation.DecodeIdentity(identityReply)
	if err != nil {
		return policy.WorldEvaluationReport{}, err
	}
	identity := &c.Identity{ColonyId: proto.String(string(decoded.Colony)), LoadToken: proto.String(string(decoded.Load)), MapId: proto.Int32(int32(decoded.Map))}
	world, _, err := w.native.ReadWorldProgression(call, identity, false)
	if err != nil {
		return policy.WorldEvaluationReport{}, err
	}
	colony, _, err := w.native.ReadColonyFacts(call, identity, false, nil)
	if err != nil {
		return policy.WorldEvaluationReport{}, err
	}
	// ReadColonyFacts already validated this snapshot (ValidateColonyFacts)
	// before returning a nil error; GetObserved() is non-nil by construction
	// once the outcome switch there picked the Observed branch without error.
	observed := colony.GetObserved()
	if observed == nil {
		return policy.WorldEvaluationReport{}, ErrControl
	}
	if observed.Context.GetTick() != world.Context.GetTick() {
		return policy.WorldEvaluationReport{}, ErrControl
	}
	resources := make(map[string]int64, len(observed.Resources))
	for _, row := range observed.Resources {
		resources[row.GetDefName()] = row.GetUnits()
	}
	facts := policy.WorldEvaluationFacts{Readable: true, HomeResources: resources}
	for _, journey := range world.Caravans {
		routes := make([]policy.WorldEvaluationRouteFact, len(journey.HomeRoutes))
		for i, route := range journey.HomeRoutes {
			routes[i] = policy.WorldEvaluationRouteFact{DestinationMapID: route.DestinationMapID, Reachable: route.Reachable, EstimatedTicks: route.EstimatedTicks, EstimatedTicksKnown: route.EstimatedTicksKnown}
		}
		pawns := make([]policy.WorldEvaluationPawnFact, len(journey.Pawns))
		for i, pawn := range journey.Pawns {
			pawns[i] = policy.WorldEvaluationPawnFact{Dead: pawn.Dead, DeadKnown: pawn.DeadKnown, Downed: pawn.Downed, DownedKnown: pawn.DownedKnown}
		}
		facts.Caravans = append(facts.Caravans, policy.WorldEvaluationCaravanFact{
			ID: journey.ID, FoodDays: journey.FoodDays, FoodDaysKnown: journey.FoodDaysKnown,
			HomeRoutes: routes, Pawns: pawns, Inventory: journey.Inventory,
		})
	}
	for _, quest := range world.Quests {
		requests := make([]policy.WorldEvaluationTradeRequestFact, len(quest.TradeRequests))
		for i, row := range quest.TradeRequests {
			requests[i] = policy.WorldEvaluationTradeRequestFact{Resource: row.Resource, Count: row.Count, DestinationTile: row.DestinationTile}
		}
		facts.Quests = append(facts.Quests, policy.WorldEvaluationQuestFact{ID: quest.ID, State: quest.State, NativeEligible: quest.CanAccept, TradeRequests: requests})
	}
	return policy.EvaluateWorld(w.policy, facts), nil
}
