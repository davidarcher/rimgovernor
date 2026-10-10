package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// sculptureRoomsOwed is the review's MaintainArt room input: a
// bedroom below target, weakest in beauty, with a free cell.
func sculptureRoomsOwed(facts observation.ColonyProjection, stage policy.ColonyStage) domain.Fact[bool] {
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	obs, sk := facts.Facts.Sleeping.Value()
	traits := sleepingTraits(facts)
	if !rk || !ck || !sk || !census.Colony || traits == nil {
		return domain.Unknown[bool]()
	}
	tier, _ := facts.TechTier.Value()
	return policy.SculptureRoomsOwed(facts.Facts.Sleeping, upgradeTargets(facts, policy.RoomQualityTargets(obs, traits, tier, facts.Impressiveness), stage), policy.FurnitureRooms(rooms, census, facts.Cells))
}

// sculptBedroom is the beauty lever after pots and floors: a
// finished packed sculpture (MaintainArt's pinned bill) installed (a
// RelocateIntent on the packed item's inner building) on free floor in the
// room, once per Episode. due is false when the lever has nothing to do.
func (r *RoundsSleepingUpkeepPlanner) sculptBedroom(call, epoch context.Context, stock *packedStock, state ControlState, goal store.WorkOwner, reading observation.RoundsReading) (RoundsBuildingResult, bool, error) {
	facts := reading.Projection
	obs, sk := facts.Facts.Sleeping.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	traits := sleepingTraits(facts)
	if !sk || !rk || !ck || !census.Colony || traits == nil {
		return RoundsBuildingResult{}, false, nil
	}
	tier, _ := facts.TechTier.Value()
	items, ok, err := stock.Items(call, policy.PackedSculptureDefinition)
	if err != nil || !ok {
		return RoundsBuildingResult{}, false, err
	}
	packed := packedSculptures(items, facts.Facts.Items)
	inner := map[string]bridge.PackedItem{}
	for _, item := range items {
		inner[item.ID] = item
	}
	step, due := policy.NextSculpture(obs, upgradeTargets(facts, policy.RoomQualityTargets(obs, traits, tier, facts.Impressiveness), r.reviewer.stage), policy.FurnitureRooms(rooms, census, facts.Cells), packed, facts.Facts.Items, bedroomGate(facts, r.reviewer.stage))
	if !due {
		return RoundsBuildingResult{}, false, nil
	}
	p := r.reviewer.player
	digest := sha256.Sum256([]byte(step.Packed))
	method := domain.MethodID(fmt.Sprintf("bedroom-sculpture-install-%x", digest[:8]))
	if _, err := p.journal.LoadOwnerMethod(call, goal, method); err == nil {
		return RoundsBuildingResult{Verdict: waitFor(policy.CauseMethodUsed, "sculpture_install")}, true, nil
	}
	item := inner[step.Packed]
	id := domain.MintPlanID()
	move, err := domain.NewMoveBuilding(item.Inner, item.InnerDef, step.Anchor, step.Rot)
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	action, err := domain.NewMoveBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", id)), move)
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	if err := p.current(call, epoch); err != nil {
		return RoundsBuildingResult{}, false, err
	}
	if p.session.State() != state {
		return RoundsBuildingResult{}, false, fmt.Errorf("%w: sculptBedroom: p.session.State() != state", ErrControl)
	}
	if err := p.journal.CommitOwnerMethod(call, goal, method, "", plan); err != nil {
		return RoundsBuildingResult{}, false, err
	}
	return RoundsBuildingResult{Verdict: BuildingReasonAdmitted}, true, nil
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

// saleArt is the packed art the trade selector may sell: every
// packed sculpture but those the install lever would put in owed rooms.
// The trade's colony read carries no rooms, construction census or work
// pawns, so the room inputs come from the routine read the install lever
// uses. Nil (no art sale) when the source cannot read packed items
// or a room input is unknown.
func (r *RoundsTradePlanner) saleArt(call context.Context, identity *c.Identity, snapshot domain.GenerationSnapshot) (map[string]bool, error) {
	native, ok := r.native.(packedSource)
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
func saleSculptures(call context.Context, native packedSource, identity *c.Identity, facts observation.ColonyProjection) (map[string]bool, error) {
	obs, sk := facts.Facts.Sleeping.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	traits := sleepingTraits(facts)
	if !sk || !rk || !ck || !census.Colony || traits == nil {
		return nil, nil
	}
	items, _, err := native.ReadPackedItems(call, identity, policy.PackedSculptureDefinition)
	if err != nil {
		return nil, err
	}
	tier, _ := facts.TechTier.Value()
	return policy.SaleSculptures(obs, policy.RoomQualityTargets(obs, traits, tier, facts.Impressiveness), policy.FurnitureRooms(rooms, census, facts.Cells), packedSculptures(items, facts.Facts.Items), facts.Facts.Items), nil
}

// reviewSaleArt is the review's shed_art input: the unreserved
// packed art count, read only while the wealth headroom is known and
// negative (unknown otherwise, so the need adds nothing).
func reviewSaleArt(call context.Context, source any, identity *c.Identity, facts observation.ColonyProjection) (domain.Fact[int64], error) {
	native, ok := source.(packedSource)
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
