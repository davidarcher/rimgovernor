package buildingruntime

import (
	"context"
	"fmt"

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
// authoritative snapshot, the colony-facts census the rounder
// judges food from, and the home colonist roster (downed, needs). Nothing
// here dispatches a native write.
type ColonyStatusNative interface {
	Identity(context.Context) (*l.IdentityReply, bridge.Result, error)
	ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error)
	ReadHomeColonists(context.Context, *c.Identity) (*o.ListPawnsReply, bridge.Result, error)
}

// ColonyStatus is a live, read-only census of the facts a sustained run is
// judged by: the food stock and runway the reviewer sees and
// every home colonist's downed state and needs, read under one identity. It
// claims no plan slot and no player gate: it issues only native reads, so a
// planner step holding the gate never delays a sample past its timeout.
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
	// PetLabels names the held census's animals for the food plan's
	// pet shortfalls; an animal without a label is absent.
	PetLabels map[policy.PawnID]string
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
	// Threat is the census's wealth split and raid points; unknown
	// under a native build that does not report the section.
	Threat bridge.ColonyThreat
	// Shrines is the ancient shrine census; unknown under a native
	// build that does not serve it.
	Shrines domain.Fact[[]policy.AncientShrine]
	// ShrineReadiness is each shrine's breach judgement, one per
	// Shrines row; empty while the census or its inputs are unknown.
	ShrineReadiness []ShrineReadinessReport
	// PlayerTechLevel is the player faction's native TechLevel name and
	// TechTier the construction tier the last rounds derived from
	// it and finished research; the tier is unknown until a review
	// with the research census has filed.
	PlayerTechLevel domain.Fact[string]
	TechTier        domain.Fact[policy.TechTier]
	// Stockpiles counts the owned stockpile zones by role kind as the last
	// rounds read them, and ForbiddenSupplies is whether the loot
	// census held a safe stack forbidden at the last rounds; both are
	// unknown until a review with the fact has filed.
	Stockpiles        domain.Fact[[]policy.StockpileRoleCount]
	ForbiddenSupplies domain.Fact[bool]
	// MoodLedger is where the colony loses mood, as the last rounds built it;
	// unknown until a review has filed.
	MoodLedger domain.Fact[policy.MoodLedger]
	// Pawns is the living home colonist roster, sorted as native listed it.
	Pawns []ColonyStatusPawn
}

// ColonyStatusPawn is one living home colonist's state.
type ColonyStatusPawn struct {
	ID         domain.PawnID
	Label      string
	Downed     domain.Fact[bool]
	Mood, Food domain.Fact[float64]
	// Share, Spent and Remaining are the colonist's personal wealth share
	// from the held colony projection; unknown when it is missing or
	// stale or holds no share for the colonist.
	Share, Spent, Remaining domain.Fact[float64]
}

// NewColonyStatus builds a ColonyStatus boundary. player and native must be
// non-nil.
func NewColonyStatus(player *Player, native ColonyStatusNative, food ...*facts.Store) (*ColonyStatus, error) {
	if player == nil || native == nil {
		return nil, fmt.Errorf("%w: NewColonyStatus: player == nil || native == nil", ErrControl)
	}
	if len(food) > 1 {
		return nil, fmt.Errorf("%w: NewColonyStatus: len(food) > 1", ErrControl)
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
	call, done, err := s.player.observe(ctx)
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
	colony, _, err := s.native.ReadColonyFacts(call, identity, false)
	if err != nil {
		return ColonyStatusReport{}, err
	}
	observed := colony.GetObserved()
	if observed == nil {
		return ColonyStatusReport{}, fmt.Errorf("%w: Read: observed == nil", ErrControl)
	}
	roster, _, err := s.native.ReadHomeColonists(call, identity)
	if err != nil {
		return ColonyStatusReport{}, err
	}
	pawns := roster.GetObserved()
	if pawns == nil {
		return ColonyStatusReport{}, fmt.Errorf("%w: Read: pawns == nil", ErrControl)
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
		Shrines:              domain.Unknown[[]policy.AncientShrine](),
		PlayerTechLevel:      optionalFact(observed.PlayerTechLevel),
		TechTier:             domain.Unknown[policy.TechTier](),
		Stockpiles:           domain.Unknown[[]policy.StockpileRoleCount](),
		ForbiddenSupplies:    domain.Unknown[bool](),
		MoodLedger:           domain.Unknown[policy.MoodLedger](),
		Pawns:                []ColonyStatusPawn{},
	}
	if source, ok := s.native.(observation.ShrineSource); ok {
		if report.Shrines, err = observation.ObserveShrines(call, source, decoded); err != nil {
			return ColonyStatusReport{}, err
		}
	}
	var shares map[policy.PawnID]policy.PersonalShare
	if held, ok := facts.Get[observation.ColonyProjection](s.food, facts.Colony); ok {
		id := held.Value.Identity
		if id.SameContext(decoded) && id.Tick <= decoded.Tick {
			a, ak := id.NativeGeneration.Value()
			b, bk := decoded.NativeGeneration.Value()
			if ak && bk && a == b {
				shares = held.Value.Facts.PersonalShares
				report.TechTier = held.Value.TechTier
				report.FoodPlan = held.Value.Facts.FoodPlan
				report.ForbiddenSupplies = forbiddenSupplies(held.Value.Facts.EventLoot)
				report.Stockpiles = held.Value.Facts.StockpileZones
				report.MoodLedger = held.Value.Facts.MoodLedger
				if animals, known := held.Value.Facts.AnimalUpkeep.Animals.Value(); known {
					report.PetLabels = map[policy.PawnID]string{}
					for _, animal := range animals {
						if animal.Label != "" {
							report.PetLabels[animal.ID] = animal.Label
						}
					}
				}
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
		if share, held := shares[policy.PawnID(pawn.ID)]; held {
			pawn.Share, pawn.Spent, pawn.Remaining = share.Share, share.Spent, share.Remaining
		}
		report.Pawns = append(report.Pawns, pawn)
	}
	if shrines, known := report.Shrines.Value(); known && len(shrines) > 0 {
		if native, ok := s.native.(shrineReadinessNative); ok {
			colonists := make([]string, 0, len(report.Pawns))
			for _, pawn := range report.Pawns {
				colonists = append(colonists, string(pawn.ID))
			}
			bounds := policy.Bounds{Width: int32(observed.MapSize.GetWidth()), Height: int32(observed.MapSize.GetHeight())}
			center := domain.Cell{X: observed.Center.GetX(), Z: observed.Center.GetZ()}
			if report.ShrineReadiness, err = shrineReadiness(call, native, identity, shrines, colonists, report.Threat.RaidPoints, center, bounds); err != nil {
				return ColonyStatusReport{}, err
			}
		}
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

// forbiddenSupplies is whether the loot census lists a safe stack still
// forbidden, the stack ManageSupplySafety releases.
func forbiddenSupplies(census domain.Fact[[]policy.LootItem]) domain.Fact[bool] {
	rows, known := census.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, row := range rows {
		if row.SafetyKnown && row.SafeToHaul && row.Forbidden {
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}
