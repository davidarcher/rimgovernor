package campaign

import (
	"context"
	"errors"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	cases.Register(recovery())
}

// Policy wood thresholds the breach is judged against: MaintainWood binds
// once stock falls under woodMin and its deficit is (WoodTarget-stock)/
// WoodTarget, so a stock back at woodMin reads as woodRecoveredDeficit.
const (
	woodMin              = 120
	woodTarget           = 350
	woodRecoveredDeficit = float64(woodTarget-woodMin) / float64(woodTarget)
)

// recovery is campaign/recovery (#633): after half a day of settled play
// the harness takes every wood log on the map (the wood-floor breach) and
// resumes; the colony must acknowledge the breach (MaintainWood binds) and
// restock to the policy floor on its own. A staged raid follows
// (test/defense_setup, an edge walk-in at the storyteller's floor points,
// #347's sapper-capable group maker); after a day of play the colony must
// have no live hostile standing, every initial colonist alive, shelter and
// food intact, and goal progress still advancing (#629). Each fixture op is
// an injection, not assistance: the harness never touches the colony's own
// work.
func recovery() cases.Case {
	window := footholdWindow()
	settle := window / 6
	breachWindow := window * 2 / 3
	raidWindow := window / 3
	return campaignCase("campaign/recovery", "Unassisted campaign: an induced wood-floor breach and then a raid, each recovered on the player control path with a dashboard viewer.",
		"half a day of settling, up to two days restocking wood and a day around the raid at Ultrafast with three reloads", 60*time.Minute,
		func(ctx context.Context, s cases.Session) error {
			c, err := newCampaign(s)
			if err != nil {
				return err
			}
			h := s.Harness()
			initial, err := initialColonists(ctx, h, c.report)
			if err != nil {
				return err
			}
			c.setupDone()

			// Settle: the foothold goals are running before anything breaks.
			settled, err := c.play(ctx, "settle", playOptions{Watch: sustainedfood.WatchConfig{
				Watch: 10 * time.Minute, Window: settle, PollTicks: 2500, Goal: policy.EnsureFoodSupply, Extra: campaignGoals,
				FailFast: sustainedfood.FailFast{Disabled: true},
			}})
			if err != nil {
				return err
			}
			if err := settled.assertPlaying(settle); err != nil {
				return err
			}
			c.milestone("settled", settled.lastTick())

			// The wood-floor breach: every log on the map is gone.
			taken, err := c.fixture(ctx, "take-wood", "test/hut_shell_fixture", map[string]any{"action": "take"})
			if err != nil {
				return err
			}
			c.inject("wood_breach", map[string]any{"taken": taken["taken"], "stacks": taken["stacks"], "tick": settled.lastTick()})

			var breachTick, restockTick uint64
			breached := false
			restocked, err := c.play(ctx, "restock", playOptions{Watch: sustainedfood.WatchConfig{
				Watch: 25 * time.Minute, Window: breachWindow, PollTicks: 2500, Goal: policy.MaintainWood, Extra: campaignGoals,
				FailFast: sustainedfood.FailFast{Disabled: true},
				Until: func(sample map[string]any) bool {
					tick, _ := sample["tick"].(uint64)
					bound, _ := sample["goal_bound"].(bool)
					deficit, known := woodDeficit(sample)
					if !breached {
						if bound {
							breached, breachTick = true, tick
						}
						return false
					}
					if !bound || (known && deficit <= woodRecoveredDeficit) {
						restockTick = tick
						return true
					}
					return false
				},
			}})
			if err != nil {
				return err
			}
			if restocked.Err != nil {
				return restocked.Err
			}
			if !breached {
				return fmt.Errorf("restock: MaintainWood never bound after the breach (%d samples)", len(restocked.Timeline))
			}
			c.milestone("wood_breach_acknowledged", breachTick)
			if restockTick == 0 {
				return fmt.Errorf("restock: wood not back to the policy floor within %d ticks of the breach", breachWindow)
			}
			c.milestone("wood_restocked", restockTick)
			facts, err := colonyFacts(ctx, h, s, "wood-after-restock")
			if err != nil {
				return err
			}
			wood, known := facts.Facts.Wood.Value()
			c.report["wood_after_restock"] = map[string]any{"wood": wood, "known": known}
			if !known || wood < woodMin {
				return fmt.Errorf("restock: native wood stock %d (known %v) under the policy floor %d", wood, known, woodMin)
			}

			// The raid: staged on the paused game, then played through.
			raid, err := c.fixture(ctx, "raid", "test/defense_setup", map[string]any{"op": "raid", "strategy": "ImmediateAttack", "arrival": "EdgeWalkIn"})
			if err != nil {
				return err
			}
			c.inject("raid", map[string]any{"points": raid["points"], "added": raid["added"], "group_maker_seed": raid["groupMakerSeed"], "applied": raid["applied"], "tick": restockTick})
			c.milestone("raid_staged", restockTick)
			fought, err := c.play(ctx, "raid", playOptions{Watch: sustainedfood.WatchConfig{
				Watch: 15 * time.Minute, Window: raidWindow, PollTicks: 2500, Goal: policy.EnsureFoodSupply, Extra: campaignGoals,
				FailFast: sustainedfood.FailFast{Disabled: true},
			}})
			if err != nil {
				return err
			}
			c.milestone("raid_window_end", fought.lastTick())
			inspect, err := c.fixture(ctx, "inspect-after-raid", "test/defense_setup", map[string]any{"op": "inspect"})
			if err != nil {
				return err
			}
			c.report["after_raid"] = map[string]any{"hostiles": inspect["hostiles"], "colonists": inspect["colonists"]}
			var failures []error
			if standing := liveHostiles(inspect); len(standing) > 0 {
				failures = append(failures, fmt.Errorf("raid: %d hostile(s) still standing after %d ticks: %v", len(standing), raidWindow, standing))
			}
			failures = append(failures, fought.assertPlaying(raidWindow), fought.assertProgress(),
				auditColony(ctx, h, s, c.report, "end_state", initial), c.assertUnassisted())
			return errors.Join(failures...)
		})
}

// woodDeficit reads the MaintainWood ranking row's deficit off a sample.
func woodDeficit(sample map[string]any) (float64, bool) {
	development, ok := sample["development"].(map[string]any)
	if !ok {
		return 0, false
	}
	deficit, ok := development["deficit"].(float64)
	return deficit, ok
}

// liveHostiles lists the hostiles an inspect reply shows neither dead nor
// downed.
func liveHostiles(inspect map[string]any) []string {
	var live []string
	for _, row := range na.AsSlice(inspect["hostiles"]) {
		m, _ := na.AsMap(row)
		dead, _ := na.AsBool(m["dead"])
		downed, _ := na.AsBool(m["downed"])
		if !dead && !downed {
			live = append(live, na.AsString(m["id"]))
		}
	}
	return live
}
