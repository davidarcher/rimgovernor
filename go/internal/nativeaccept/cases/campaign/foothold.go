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
	cases.Register(foothold())
}

// campaignGoals are the goals every campaign sample reads beside
// EnsureFoodSupply: the foothold gates and the wood stock the recovery
// campaign breaches.
var campaignGoals = []policy.GoalID{
	policy.EnsureInitialShelter, policy.EnsureFoodStorage, policy.EnsureCooking,
	policy.EnsureTemperatureSafety, policy.MaintainSleeping, policy.MaintainWood,
	policy.EnsureBasicDefense,
}

// foothold is campaign/foothold (#633): the eight tribal colonists, played
// through the player control path for three game days with one dashboard
// viewer streaming video and no harness hand after setup, must end
// sheltered (indoor sleeping capacity for every colonist), fed (no
// malnutrition past 0.3, a known food runway) and alive, with a goal
// progress record advanced on native evidence and none stalled past its
// review deadline (#629).
func foothold() cases.Case {
	window := footholdWindow()
	return campaignCase("campaign/foothold", "Unassisted campaign: shelter and food maintained over three game days on the player control path with a dashboard viewer.",
		"three game days at Ultrafast on the player path with every family and a viewer is 20-25 minutes of play plus the reload", 45*time.Minute,
		func(ctx context.Context, s cases.Session) error {
			c, err := newCampaign(s)
			if err != nil {
				return err
			}
			initial, err := initialColonists(ctx, s.Harness(), c.report)
			if err != nil {
				return err
			}
			c.setupDone()
			p, err := c.play(ctx, "foothold", playOptions{Watch: sustainedfood.WatchConfig{
				Watch: 35 * time.Minute, Window: window, PollTicks: 5000, Goal: policy.EnsureFoodSupply, Extra: campaignGoals,
				FailFast: sustainedfood.FailFast{Disabled: true},
			}})
			if err != nil {
				return err
			}
			c.milestone("foothold_window_end", p.lastTick())
			failures := []error{p.assertPlaying(window), p.assertProgress(), auditColony(ctx, s.Harness(), s, c.report, "end_state", initial), c.assertUnassisted()}
			return errors.Join(failures...)
		})
}

// assertUnassisted is the campaign's own gate: no intervention after setup.
func (c *campaign) assertUnassisted() error {
	if n := c.interventionCount(); n > 0 {
		var labels []string
		for _, row := range c.interventions {
			if after, _ := row["after_setup"].(bool); after {
				labels = append(labels, na.AsString(row["label"]))
			}
		}
		return fmt.Errorf("%d intervention(s) after setup: %v", n, labels)
	}
	return nil
}
