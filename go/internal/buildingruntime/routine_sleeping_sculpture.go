package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// sculptureSource is the native half the sculpture install (#830) needs
// beyond RoutineBuildingSource; a source without it skips the lever.
type sculptureSource interface {
	ReadPackedItems(context.Context, *c.Identity, string) ([]bridge.PackedItem, bridge.Result, error)
}

var _ sculptureSource = (*bridge.Client)(nil)

// sculptureRoomsOwed is the review's MaintainArt room input (#1190): a
// bedroom below target, weakest in beauty, with a free cell.
func sculptureRoomsOwed(facts observation.ColonyProjection, stage policy.ColonyStage) domain.Fact[bool] {
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	obs, sk := facts.Facts.Sleeping.Value()
	traits := sleepingTraits(facts)
	if !rk || !ck || !sk || !census.Colony || traits == nil {
		return domain.Unknown[bool]()
	}
	tier, _ := facts.BuildTier.Value()
	return policy.SculptureRoomsOwed(facts.Facts.Sleeping, upgradeTargets(facts, policy.RoomQualityTargets(obs, traits, tier, facts.Impressiveness), stage), policy.TidyFurnitureRooms(rooms, census, facts.Cells))
}

// sculptBedroom is the beauty lever after pots and floors (#830): a
// finished packed sculpture (MaintainArt's pinned bill, #1190) installed (a
// RelocateIntent on the packed item's inner building) on free floor in the
// room, once per goal epoch. due is false when the lever has nothing to do.
func (r *RoutineSleepingUpkeepPlanner) sculptBedroom(call, epoch context.Context, state ControlState, goal store.GoalState, reading observation.RoutineReading) (RoutineBuildingResult, bool, error) {
	native, ok := r.native.(sculptureSource)
	facts := reading.Projection
	obs, sk := facts.Facts.Sleeping.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	traits := sleepingTraits(facts)
	if !ok || !sk || !rk || !ck || !census.Colony || traits == nil {
		clockSchedulerLog("%s: sculpture install: inputs unknown source=%v sleeping=%v rooms=%v census=%v traits=%v", goal.Goal.ID, ok, sk, rk, ck, traits != nil)
		return RoutineBuildingResult{}, false, nil
	}
	tier, _ := facts.BuildTier.Value()
	identity := boundary.Identity(state.Snapshot)
	items, _, err := native.ReadPackedItems(call, identity, policy.PackedSculptureDefinition)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	packed := packedSculptures(items, facts.Facts.Items)
	inner := map[string]bridge.PackedItem{}
	for _, item := range items {
		inner[item.ID] = item
	}
	step, due := policy.NextSculpture(obs, upgradeTargets(facts, policy.RoomQualityTargets(obs, traits, tier, facts.Impressiveness), r.reviewer.stage), policy.TidyFurnitureRooms(rooms, census, facts.Cells), packed, facts.Facts.Items, bedroomGate(facts, r.reviewer.stage))
	if !due {
		if len(packed) > 0 {
			clockSchedulerLog("%s: sculpture install: no room fits %d packed %v (owed %v)", goal.Goal.ID, len(packed), packed, sculptureRoomsOwed(facts, r.reviewer.stage))
		}
		return RoutineBuildingResult{}, false, nil
	}
	p := r.reviewer.player
	digest := sha256.Sum256([]byte(step.Packed))
	method := domain.MethodID(fmt.Sprintf("bedroom-sculpture-install-%x", digest[:8]))
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Verdict: BuildingReasonUsed}, true, nil
	}
	item := inner[step.Packed]
	id := domain.MintPlanID()
	move, err := domain.NewMoveBuilding(item.Inner, item.InnerDef, step.Anchor, step.Rot)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	action, err := domain.NewMoveBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", id)), move)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	if err := p.current(call, epoch); err != nil {
		return RoutineBuildingResult{}, false, err
	}
	if p.session.State() != state {
		return RoutineBuildingResult{}, false, fmt.Errorf("%w: sculptBedroom: p.session.State() != state", ErrControl)
	}
	if _, err := p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineBuildingResult{}, false, err
	}
	clockSchedulerLog("%s: bedroom %s: sculpture install (weakest beauty)", goal.Goal.ID, step.Room)
	return RoutineBuildingResult{Verdict: BuildingReasonAdmitted}, true, nil
}

// packedSculptures is the policy view of packed sculpture stock.
func packedSculptures(items []bridge.PackedItem, facts policy.ItemFacts) []policy.PackedSculpture {
	out := make([]policy.PackedSculpture, 0, len(items))
	for _, item := range items {
		if _, ok := facts.SculptureSize(item.InnerDef); !ok {
			continue
		}
		quality := -1
		if item.QualityKnown {
			quality = int(item.Quality)
		}
		out = append(out, policy.PackedSculpture{ID: item.ID, Def: item.InnerDef, Quality: quality, MarketValue: item.MarketValue})
	}
	return out
}

// saleArt is the packed art the trade selector may sell (#1194): every
// packed sculpture but those the install lever would put in owed rooms.
// The trade's colony read carries no rooms, construction census or work
// pawns, so the room inputs come from the routine read the install lever
// uses (#1195). Nil (no art sale) when the source cannot read packed items
// or a room input is unknown.
func (r *RoutineTradePlanner) saleArt(call context.Context, identity *c.Identity, snapshot domain.GenerationSnapshot) (map[string]bool, error) {
	native, ok := r.native.(sculptureSource)
	if !ok {
		return nil, nil
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return nil, err
	}
	claims, err := r.reviewer.player.journal.ConstructionClaims(call, snapshot, expected.Tick)
	if err != nil {
		return nil, err
	}
	read, err := r.reviewer.observeRooms(call, r.reviewer.native, expected, claims)
	if err != nil {
		return nil, err
	}
	return saleSculptures(call, native, identity, read.Projection)
}

// saleSculptures is SaleSculptures over a routine projection and the packed
// items read; nil when a room input is unknown.
func saleSculptures(call context.Context, native sculptureSource, identity *c.Identity, facts observation.ColonyProjection) (map[string]bool, error) {
	obs, sk := facts.Facts.Sleeping.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	traits := sleepingTraits(facts)
	if !sk || !rk || !ck || !census.Colony || traits == nil {
		clockSchedulerLog("sale art: room inputs unknown sleeping=%v rooms=%v construction=%v colony=%v traits=%v", sk, rk, ck, census.Colony, traits != nil)
		return nil, nil
	}
	items, _, err := native.ReadPackedItems(call, identity, policy.PackedSculptureDefinition)
	if err != nil {
		return nil, err
	}
	clockSchedulerLog("sale art: packed=%+v", packedSculptures(items, facts.Facts.Items))
	tier, _ := facts.BuildTier.Value()
	return policy.SaleSculptures(obs, policy.RoomQualityTargets(obs, traits, tier, facts.Impressiveness), policy.TidyFurnitureRooms(rooms, census, facts.Cells), packedSculptures(items, facts.Facts.Items), facts.Facts.Items), nil
}

// reviewSaleArt is the review's shed_art input (#1247): the unreserved
// packed art count, read only while the wealth headroom is known and
// negative (unknown otherwise, so the need adds nothing).
func reviewSaleArt(call context.Context, source any, identity *c.Identity, facts observation.ColonyProjection) (domain.Fact[int64], error) {
	native, ok := source.(sculptureSource)
	h, hk := facts.Facts.WealthBudget().Value()
	if !ok || !hk || h >= 0 {
		return domain.Unknown[int64](), nil
	}
	if _, rk := facts.Rooms.Value(); !rk {
		return domain.Unknown[int64](), nil
	}
	sale, err := saleSculptures(call, native, identity, facts)
	if err != nil || sale == nil {
		return domain.Unknown[int64](), err
	}
	return domain.Known(int64(len(sale))), nil
}
