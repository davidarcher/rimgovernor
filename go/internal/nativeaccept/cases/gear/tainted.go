package gear

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// gear/tainted (#468): an excellent tainted parka lies beside a normal
// clean one next to a colonist stripped of their outer layer, every
// colonist on the scenario's Anything policy (which allows tainted
// apparel) with vanilla's optimizer held off. The apparel-policy operation
// must put every colonist on a RimGovernor role policy, the Worker policy
// among them, and nobody may end up wearing the tainted item.
const taintedPolicyPrefix = "RimGovernor "

func init() {
	cases.Register(cases.Case{
		Name:   "gear/tainted",
		Scope:  "Apparel policies (#468): with a tainted parka beside a clean one, no colonist wears the tainted item and the RimGovernor worker policy exists and is assigned.",
		Start:  start("tainted", nil),
		Keep:   []string{string(na.NeedFood)},
		Serve:  serve(dressingFamilies, "gear-tainted"),
		Budget: watch + 3*time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			return observe(ctx, s, dayTicks, nil, func(ctx context.Context, h *na.Harness, report na.Report, timeline []map[string]any, final census) error {
				return auditTainted(ctx, s, report, timeline, final)
			})
		},
	})
}

func auditTainted(ctx context.Context, s cases.Session, report na.Report, timeline []map[string]any, final census) error {
	methods, summary, err := equipmentMethods(ctx, s.Config().Output)
	report["equipment_goal"] = summary
	if err != nil {
		return err
	}
	policies := 0
	for _, m := range methods {
		if m.Kind == domain.ApparelPolicyAction && m.completed() {
			policies++
		}
	}
	report["completed_policy_orders"] = policies
	if policies == 0 {
		return fmt.Errorf("no completed %s under MaintainEquipment: %v", domain.ApparelPolicyAction, summary)
	}
	tainted := na.AsString(s.Prepared()["tainted"])
	worker := taintedPolicyPrefix + string(policy.GearWorker)
	var wearers, unassigned []string
	workers := 0
	for _, c := range final.Colonists {
		for _, w := range c.Worn {
			if w.ThingID == tainted || w.Tainted {
				wearers = append(wearers, fmt.Sprintf("%s wears %s %s", c.Name, w.Definition, w.ThingID))
			}
		}
		if !strings.HasPrefix(c.Policy, taintedPolicyPrefix) {
			unassigned = append(unassigned, fmt.Sprintf("%s on %q", c.Name, c.Policy))
		}
		if c.Policy == worker {
			workers++
		}
	}
	report["worker_policy_colonists"] = workers
	if len(wearers) > 0 {
		return fmt.Errorf("tainted apparel worn: %v", wearers)
	}
	if len(unassigned) > 0 {
		return fmt.Errorf("colonists without a RimGovernor role policy: %v", unassigned)
	}
	if workers == 0 {
		return fmt.Errorf("no colonist is assigned the %q policy", worker)
	}
	if _, ok := firstRecoveredTick(timeline); !ok {
		return fmt.Errorf("MaintainEquipment never recovered within the window: %v", summary)
	}
	return nil
}
