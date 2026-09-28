package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// defenseFuelTier names the burn-out fuel's methods and results (#1120).
const defenseFuelTier policy.DefenseTierName = "fuel"

// burnFuelRegion is the fuel census rectangle around the hive.
func burnFuelRegion(hive domain.Cell) bridge.CellRect {
	lo, hi := policy.BurnFuelRegion(hive)
	return bridge.CellRect{Min: lo, Max: hi}
}

// fuel admits the burn-out's wood stools around the hive (#1120) on the
// free cells the census shows, enough to bring the standing fuel to the
// policy's count; nothing once it stands.
func (r *RoutineDefenseLayoutPlanner) fuel(call, epoch context.Context, goal store.GoalState, state ControlState, burn policy.CombatBurn) (RoutineDefenseLayoutResult, error) {
	read, census, err := r.fightCensus(call, state, burnFuelRegion(burn.Hive))
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	buildings, err := policy.BurnFuel(burn.Hive, census)
	if err != nil || len(buildings) == 0 {
		return RoutineDefenseLayoutResult{Reason: BuildingMethodNoDeficit, Tier: defenseFuelTier}, err
	}
	return r.admitFightBuilds(call, epoch, goal, state, read, buildings, defenseFuelTier)
}

// burnFuelSource is the census read the combat step makes for a burn-out
// waiting on its fuel.
type burnFuelSource interface {
	ReadDefenseSite(context.Context, *c.Identity, bridge.CellRect) (bridge.DefenseSite, bridge.Result, error)
}

// combatBurnFuel is the fuel standing around a fueling burn-out's hive,
// censused at the combat frame's tick; unknown for any other fight or a
// census older than the frame, so the fight never lights on a stale read.
func combatBurnFuel(ctx context.Context, native burnFuelSource, identity *c.Identity, m policy.CombatMemory, tick int64) (domain.Fact[int], error) {
	if !m.Burn.Fueling() {
		return domain.Unknown[int](), nil
	}
	site, _, err := native.ReadDefenseSite(ctx, identity, burnFuelRegion(m.Burn.Hive))
	if err != nil {
		return domain.Unknown[int](), err
	}
	if site.Context.GetTick() < tick {
		return domain.Unknown[int](), nil
	}
	return domain.Known(policy.BurnFuelStanding(m.Burn.Hive, defenseFightCensus(site))), nil
}
