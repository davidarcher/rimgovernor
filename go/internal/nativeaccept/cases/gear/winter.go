package gear

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// gear/winter (#467): the tribal baseline moved to one day before its
// first winter twelfth, ComplexClothing finished, cloth and a hand
// tailoring bench supplied and nobody owning winter wear. The season
// lookahead must dress every colonist in a parka or jacket plus a tuque
// before the winter twelfth opens, and the thermal deficit must never
// flag: MaintainEquipment recovers before the fixture's winter tick, no
// later sample falls back into deficit, and the final census shows every
// colonist inside their comfort band with no hypothermia or heatstroke.
const (
	winterHoursBefore = 24
	winterOuter       = "Apparel_Parka"
	winterJacket      = "Apparel_Jacket"
	winterHeadgear    = "Apparel_Tuque"
)

func init() {
	cases.Register(cases.Case{
		Name:   "gear/winter",
		Scope:  fmt.Sprintf("Season lookahead (#467): from the last day of autumn with cloth and a tailoring bench, every colonist owns a %s or %s plus a %s before the first winter twelfth and the thermal deficit never flags.", winterOuter, winterJacket, winterHeadgear),
		Start:  start("winter", map[string]any{"hoursBeforeWinter": winterHoursBefore}),
		Keep:   []string{string(na.NeedFood)},
		Serve:  serve(tailoringFamilies, "gear-winter"),
		Budget: watch + 3*time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			return observe(ctx, s, winterHoursBefore*2500, nil, func(ctx context.Context, h *na.Harness, report na.Report, timeline []map[string]any, final census) error {
				return auditWinter(ctx, s, report, timeline, final)
			})
		},
	})
}

func auditWinter(ctx context.Context, s cases.Session, report na.Report, timeline []map[string]any, final census) error {
	methods, summary, err := equipmentMethods(ctx, s.Config().Output)
	report["equipment_goal"] = summary
	if err != nil {
		return err
	}
	winterTick := asTick(s.Prepared()["winterTick"])
	recoveredAt, ok := firstRecoveredTick(timeline)
	report["recovered_tick"] = recoveredAt
	report["winter_tick"] = winterTick
	if !ok {
		return fmt.Errorf("MaintainEquipment never recovered within the window: %v", summary)
	}
	if recoveredAt > winterTick {
		return fmt.Errorf("MaintainEquipment recovered at tick %d, after the winter twelfth opened at %d", recoveredAt, winterTick)
	}
	relapsed := 0
	for _, sample := range timeline {
		if asTick(sample["review_tick"]) > recoveredAt && !recovered(sample) {
			relapsed++
		}
	}
	report["relapsed_samples"] = relapsed
	if relapsed > 0 {
		return fmt.Errorf("%d samples after recovery show MaintainEquipment back in deficit", relapsed)
	}
	wears := 0
	for _, m := range methods {
		if m.Kind == domain.GearReplaceAction && m.completed() {
			wears++
		}
	}
	report["completed_wear_orders"] = wears
	var undressed, exposed []string
	for _, c := range final.Colonists {
		if !c.wears(winterOuter, winterJacket) || !c.wears(winterHeadgear) {
			undressed = append(undressed, c.Name)
		}
		if c.Hypothermia > 0 || c.Heatstroke > 0 || c.Ambient < c.ComfortableMin || c.Ambient > c.ComfortableMax {
			exposed = append(exposed, fmt.Sprintf("%s ambient %.1f band [%.1f, %.1f] hypothermia %.2f heatstroke %.2f", c.Name, c.Ambient, c.ComfortableMin, c.ComfortableMax, c.Hypothermia, c.Heatstroke))
		}
	}
	if len(undressed) > 0 {
		return fmt.Errorf("colonists without a %s/%s and %s at the winter twelfth: %v", winterOuter, winterJacket, winterHeadgear, undressed)
	}
	if len(exposed) > 0 {
		return fmt.Errorf("colonists outside their comfort band or with a thermal hediff: %v", exposed)
	}
	return nil
}
