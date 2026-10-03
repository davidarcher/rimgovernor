package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// defenseFuelTier names the burn-out's seal and fuel methods and results
// (#1120, #1122).
const defenseFuelTier policy.DefenseTierName = "fuel"

// burnRegion is the burn-out's census rectangle around the hive.
func burnRegion(hive domain.Cell) bridge.CellRect {
	lo, hi := policy.BurnRegion(hive)
	return bridge.CellRect{Min: lo, Max: hi}
}

// fuel admits what the burn-out still lacks (#1120, #1122): its stone
// seal and corridor doors and its wood stools, on the census around the
// hive; nothing once all of it stands, or while the seal reads unroofed
// (the fight then drops the burn).
func (r *RoutineDefenseLayoutPlanner) fuel(call, epoch context.Context, goal store.GoalState, state ControlState, burn policy.CombatBurn) (RoutineDefenseLayoutResult, error) {
	read, census, err := r.fightCensus(call, state, burnRegion(burn.Hive))
	if err != nil || !policy.BurnRoofed(burn.Hive, census) {
		return RoutineDefenseLayoutResult{Verdict: BuildingReasonNoDeficit, Tier: defenseFuelTier}, err
	}
	items, err := r.reviewer.itemFacts(call, state.Snapshot)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	stock, _ := read.Projection.Resources.Value()
	stone, err := items.StoneBlock(stock)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	buildings, err := policy.BurnBuilds(burn, census, string(stone), items)
	if err != nil || len(buildings) == 0 {
		return RoutineDefenseLayoutResult{Verdict: BuildingReasonNoDeficit, Tier: defenseFuelTier}, err
	}
	return r.admitFightBuilds(call, epoch, goal, state, read, buildings, defenseFuelTier)
}

// burnFuelSource is the census read the combat step makes for a burn-out
// waiting on its seal and fuel.
type burnFuelSource interface {
	ReadDefenseSite(context.Context, *c.Identity, bridge.CellRect) (bridge.DefenseSite, bridge.Result, error)
}

// combatBurnSite is an active burn-out's census, read at the combat
// frame's tick: what it lacks, its roof and its standing fuel. Unknown for
// any other fight or a census older than the frame, so the fight never
// lights or tops up on a stale read.
func combatBurnSite(ctx context.Context, native burnFuelSource, identity *c.Identity, m policy.CombatMemory, tick int64, items policy.ItemFacts) (domain.Fact[policy.BurnSite], error) {
	if !m.Burn.Active() {
		return domain.Unknown[policy.BurnSite](), nil
	}
	site, _, err := native.ReadDefenseSite(ctx, identity, burnRegion(m.Burn.Hive))
	if err != nil {
		return domain.Unknown[policy.BurnSite](), err
	}
	if site.Context.GetTick() < tick {
		return domain.Unknown[policy.BurnSite](), nil
	}
	out, err := policy.BurnSurvey(*m.Burn, defenseFightCensus(site), items)
	if err != nil {
		return domain.Unknown[policy.BurnSite](), err
	}
	return domain.Known(out), nil
}
