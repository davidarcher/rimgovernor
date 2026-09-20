package gear

import (
	"context"
	"fmt"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// gear/roster (#469): the baseline grown to twelve colonists, each
// stripped of shirt and headgear, with a stockpile beside them holding a
// shirt and a tuque per pawn plus spares. The batch planner must recover
// MaintainEquipment within one in-game day of the first review, raise at
// most three bills, and never dress a pawn twice for the same slot.
const (
	rosterSize     = 12
	rosterMaxBills = 3
	rosterShirt    = "Apparel_BasicShirt"
	rosterHeadgear = "Apparel_Tuque"
)

func init() {
	cases.Register(cases.Case{
		Name:   "gear/roster",
		Scope:  fmt.Sprintf("Batch dressing (#469): twelve stripped colonists with spares in storage recover MaintainEquipment within one in-game day, with at most %d bills raised and no pawn dressed twice for the same slot.", rosterMaxBills),
		Start:  start("roster", nil),
		Keep:   []string{string(na.NeedFood)},
		Serve:  serve(dressingFamilies, "gear-roster"),
		Budget: watch + 3*time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			return observe(ctx, s, dayTicks, nil, func(ctx context.Context, h *na.Harness, report na.Report, timeline []map[string]any, final census) error {
				return auditRoster(ctx, s, h, report, timeline, final)
			})
		},
	})
}

func auditRoster(ctx context.Context, s cases.Session, h *na.Harness, report na.Report, timeline []map[string]any, final census) error {
	methods, summary, err := equipmentMethods(ctx, s.Config().Output)
	report["equipment_goal"] = summary
	if err != nil {
		return err
	}
	if len(final.Colonists) != rosterSize {
		return fmt.Errorf("final census holds %d colonists, not %d", len(final.Colonists), rosterSize)
	}
	if len(timeline) == 0 {
		return fmt.Errorf("the window took no samples")
	}
	first := asTick(timeline[0]["review_tick"])
	recoveredAt, ok := firstRecoveredTick(timeline)
	report["first_tick"] = first
	report["recovered_tick"] = recoveredAt
	if !ok {
		return fmt.Errorf("MaintainEquipment never recovered within the window: %v", summary)
	}
	if recoveredAt-first > dayTicks {
		return fmt.Errorf("MaintainEquipment recovered %d ticks after the first review, past one day (%d)", recoveredAt-first, dayTicks)
	}
	bills := 0
	wears := map[string][]string{} // pawn -> definitions worn through completed orders
	defs := map[string]bool{}
	for _, m := range methods {
		switch m.Kind {
		case domain.ProductionBillAction:
			bills++
		case domain.GearReplaceAction:
			if m.completed() {
				wears[m.Pawn] = append(wears[m.Pawn], m.Definition)
				defs[m.Definition] = true
			}
		}
	}
	report["bills_raised"] = bills
	if bills > rosterMaxBills {
		return fmt.Errorf("%d bills raised under MaintainEquipment, more than %d", bills, rosterMaxBills)
	}
	// The slot of each ordered definition (layers|groups) comes from the
	// probe, so two orders for one pawn that share a slot are a double
	// dressing whatever their definitions.
	names := make([]string, 0, len(defs))
	for d := range defs {
		names = append(names, d)
	}
	sort.Strings(names)
	slots, err := probe(ctx, h, "slot-census", names)
	if err != nil {
		return err
	}
	var doubled []string
	for pawn, worn := range wears {
		seen := map[string]string{}
		for _, d := range worn {
			slot, ok := slots.DefSlots[d]
			if !ok {
				return fmt.Errorf("probe reports no slot for %s", d)
			}
			if prior, dup := seen[slot]; dup {
				doubled = append(doubled, fmt.Sprintf("%s: %s then %s in slot %s", pawn, prior, d, slot))
			}
			seen[slot] = d
		}
	}
	sort.Strings(doubled)
	report["doubled_slots"] = doubled
	if len(doubled) > 0 {
		return fmt.Errorf("pawns dressed twice for one slot: %v", doubled)
	}
	var shirtless []string
	for _, c := range final.Colonists {
		if !c.wears(rosterShirt) {
			shirtless = append(shirtless, c.Name)
		}
	}
	if len(shirtless) > 0 {
		return fmt.Errorf("colonists still without a %s: %v", rosterShirt, shirtless)
	}
	return nil
}
