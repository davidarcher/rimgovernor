package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ColonyStatusNative is the read-only native surface ColonyStatus needs:
// the Identity call every clock-scheduled planner uses to learn the current
// authoritative snapshot, the colony-facts census the routine reviewer
// judges food from, and the home colonist roster (downed, needs). Nothing
// here dispatches a native write.
type ColonyStatusNative interface {
	Identity(context.Context) (*l.IdentityReply, bridge.Result, error)
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
	ReadHomeColonists(context.Context, *c.Identity) (*o.ListPawnsReply, bridge.Result, error)
}

// ColonyStatus is a live, read-only census of the facts a sustained run is
// judged by (issue #261): the food stock and runway the reviewer sees and
// every home colonist's downed state and needs, read under one identity. It
// claims no plan slot and serializes through Player.enter like
// WorldEvaluation, so it never interleaves with an in-flight native write.
type ColonyStatus struct {
	player *Player
	native ColonyStatusNative
	food   *facts.Store
}

// ColonyStatusReport is one ColonyStatus read. Unknown facts are unknown,
// never zero: a native census that omits the field says so.
type ColonyStatusReport struct {
	FoodPlan     domain.Fact[policy.FoodPlan]
	FoodPlanTick domain.Fact[domain.Tick]
	// Tick is the colony census's tick; RosterTick the roster read's, equal
	// under a stopped clock and a few ticks later under a running window.
	Tick, RosterTick     domain.Tick
	Colonists, Workers   domain.Fact[int64]
	FoodNutrition        domain.Fact[float64]
	NutritionPerDay      domain.Fact[float64]
	FoodRunwayDays       domain.Fact[float64]
	PendingFoodNutrition domain.Fact[float64]
	// FoodCorpses counts the edible corpses the census listed.
	FoodCorpses int
	// Threat is the census's wealth split and raid points (#395); unknown
	// under a native build that does not report the section.
	Threat bridge.ColonyThreat
	// Pawns is the living home colonist roster, sorted as native listed it.
	Pawns []ColonyStatusPawn
}

// ColonyStatusPawn is one living home colonist's state.
type ColonyStatusPawn struct {
	ID         domain.PawnID
	Label      string
	Downed     domain.Fact[bool]
	Mood, Food domain.Fact[float64]
}

// NewColonyStatus builds a ColonyStatus boundary. player and native must be
// non-nil.
func NewColonyStatus(player *Player, native ColonyStatusNative, food ...*facts.Store) (*ColonyStatus, error) {
	if player == nil || native == nil {
		return nil, ErrControl
	}
	if len(food) > 1 {
		return nil, ErrControl
	}
	s := &ColonyStatus{player: player, native: native}
	if len(food) == 1 {
		s.food = food[0]
	}
	return s, nil
}

// Read reports the current colony census. It does not require player
// control to be enabled: nothing here writes, so no control epoch protects
// it. The game may be running under a live window, so the two reads land
// on nearby ticks rather than one; the report carries both.
func (s *ColonyStatus) Read(ctx context.Context) (ColonyStatusReport, error) {
	call, _, done, err := s.player.enter(ctx, false)
	if err != nil {
		return ColonyStatusReport{}, err
	}
	defer done()
	identityReply, _, err := s.native.Identity(call)
	if err != nil {
		return ColonyStatusReport{}, err
	}
	decoded, err := observation.DecodeIdentity(identityReply)
	if err != nil {
		return ColonyStatusReport{}, err
	}
	identity := &c.Identity{ColonyId: proto.String(string(decoded.Colony)), LoadToken: proto.String(string(decoded.Load)), MapId: proto.Int32(int32(decoded.Map))}
	colony, _, err := s.native.ReadColonyFacts(call, identity, false, nil)
	if err != nil {
		return ColonyStatusReport{}, err
	}
	observed := colony.GetObserved()
	if observed == nil {
		return ColonyStatusReport{}, ErrControl
	}
	roster, _, err := s.native.ReadHomeColonists(call, identity)
	if err != nil {
		return ColonyStatusReport{}, err
	}
	pawns := roster.GetObserved()
	if pawns == nil {
		return ColonyStatusReport{}, ErrControl
	}
	report := ColonyStatusReport{
		Tick:                 domain.Tick(observed.Context.GetTick()),
		RosterTick:           domain.Tick(pawns.Context.GetTick()),
		Colonists:            countFact(observed.ColonistCount),
		Workers:              countFact(observed.WorkerCount),
		FoodNutrition:        optionalFact(observed.FoodNutrition),
		NutritionPerDay:      optionalFact(observed.NutritionPerDay),
		FoodRunwayDays:       optionalFact(observed.FoodRunwayDays),
		PendingFoodNutrition: optionalFact(observed.PendingFoodNutrition),
		FoodCorpses:          len(observed.FoodCorpses),
		Threat:               bridge.ProjectColonyThreat(observed),
		Pawns:                []ColonyStatusPawn{},
	}
	if held, ok := facts.Get[observation.ColonyProjection](s.food, facts.Colony); ok {
		id := held.Value.Identity
		if id.SameContext(decoded) && id.Tick <= decoded.Tick && bridge.FactColony.Fresh(int64(id.Tick), int64(decoded.Tick)) {
			a, ak := id.NativeGeneration.Value()
			b, bk := decoded.NativeGeneration.Value()
			if ak && bk && a == b {
				report.FoodPlan = held.Value.Facts.FoodPlan
				if _, known := report.FoodPlan.Value(); known {
					report.FoodPlanTick = domain.Known(id.Tick)
				}
			}
		}
	}
	for _, row := range pawns.Pawns {
		if row == nil || row.Pawn == nil || row.Pawn.GetId() == "" {
			continue
		}
		if row.GetDead() {
			continue
		}
		pawn := ColonyStatusPawn{ID: domain.PawnID(row.Pawn.GetId()), Label: row.Pawn.GetLabel(), Downed: optionalFact(row.Downed)}
		if needs := row.GetNeeds(); needs != nil {
			pawn.Mood = optionalFact(needs.Mood)
			pawn.Food = optionalFact(needs.Food)
		}
		report.Pawns = append(report.Pawns, pawn)
	}
	return report, nil
}

func countFact(p *uint32) domain.Fact[int64] {
	if p == nil {
		return domain.Unknown[int64]()
	}
	return domain.Known(int64(*p))
}

func optionalFact[T any](p *T) domain.Fact[T] {
	if p == nil {
		return domain.Unknown[T]()
	}
	return domain.Known(*p)
}
