package buildingruntime

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// lab-siege (#1147, #1154): three riflemen and a crewed mortar against
// four rifle raiders under a real LordJob_Siege camped ~32 cells out.
// The fight opens on the far camp: the first stop holds (the camp not yet
// set), then the siege sorties with the camp still building, every
// rifleman attacking and the mortar firing counter-battery, before any
// mortar frame; the raiders flee in the sortie, so the recording never
// reaches the harass mode.
func TestCombatReplayLabSiege(t *testing.T) {
	t.Parallel()
	sortie := func(s combatReplayStop) bool {
		return s.Memory.Tactic == policy.TacticSiege && s.Memory.SiegeMode == policy.SiegeSortie
	}
	checkCombat(t, "testdata/combat/lab-siege.json.gz",
		formsTactic(firstStop, policy.TacticSiege),
		combatAssertion{name: "holds before the camp is set", at: firstStop, check: func(s combatReplayStop) error {
			if s.Memory.SiegeMode != policy.SiegeHold || len(s.Orders) != 0 {
				return fmt.Errorf("mode %q orders %+v", s.Memory.SiegeMode, s.Orders)
			}
			return nil
		}},
		combatAssertion{name: "the sortie attacks before any mortar frame", at: sortie, check: func(s combatReplayStop) error {
			if s.Memory.SiegeMortar {
				return fmt.Errorf("sortie past a mortar frame")
			}
			for _, o := range s.Orders {
				if o.Kind == policy.OrderAttack {
					return nil
				}
			}
			return fmt.Errorf("no attack in %+v", s.Orders)
		}},
		ordersOwnedDrafts(),
		attacksOnPresentHostiles(),
	)
}
