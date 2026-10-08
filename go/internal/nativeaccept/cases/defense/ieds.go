package defense

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// defense/ied-lane is the perimeter campaign with the IED tier's gates
// open (#1209, #1211): before the layout is built the fixture finishes the
// IEDs research and drops high-explosive shells, so the layout planner
// extends the trap lane outward from the killbox entry with IEDs whose
// blast keeps off doors, the safe lane, colonist routes and storage. The
// case holds the built tier to the native IEDs standing on its cells, then
// the edge raid walks the approach and at least one IED is gone (sprung)
// once it resolves. A layout that places no IED is a legitimate policy
// outcome (colonist routes can cover the whole approach) but proves
// nothing here, so the case fails naming it.
func init() {
	v := variant{strategy: "ImmediateAttack", arrival: "EdgeWalkIn", threat: "raid", instantWalls: true,
		gates: func(fixture fixtureFunc, siteX, siteZ int) (map[string]any, error) {
			return fixture("ieds", map[string]any{"op": "ieds", "x": siteX, "z": siteZ})
		},
		layoutBuilt: iedsBuilt,
		raided:      iedsSprung,
	}
	cases.Register(cases.Case{
		Name: "defense/ied-lane",
		Scope: "IEDs on the approach (#1209): with IEDs researched and HE shells stocked, the perimeter layout's ieds tier places IEDs outward from the " +
			"killbox entry and builds them natively (every tier IED stands on its cell); a real RaidEnemy edge assault walks the approach and at least " +
			"one IED is sprung by the time the raid resolves; the rest of the perimeter campaign (hold-the-line, release, repair) holds as in defense/perimeter. Native: vanilla raid pathing springing the traps.",
		Start: cases.Save{Name: sustained.BaselineSave}, Serve: &cases.ServeSpec{Families: perimeterFamilies, Prefix: "defense-ieds"},
		Budget: 3 * time.Hour,
		Crew:   cases.Crew{Size: 3}, Reason: "the IED tier is part of the perimeter's build, and only a real raid on the approach springs it",
		Run: func(ctx context.Context, s cases.Session) error { return run(ctx, s, v) },
	})
}

// iedsBuilt holds the layout's ieds tier to the native IEDs: at least one
// placed, the tier built, and every tier IED standing on its cell.
func iedsBuilt(layout store.DefenseLayoutRecord, inspect map[string]any) error {
	tier, _, ok := layout.Tier(policy.TierIEDs)
	if !ok || len(tier.Buildings) == 0 {
		return fmt.Errorf("the layout placed no IED on the approach (a door, the safe lane, a colonist route or storage covers every approach cell's blast); nothing to spring: %+v", tier)
	}
	if !tier.Built {
		return fmt.Errorf("ieds tier not built: %+v", tier)
	}
	standing := map[[2]int]bool{}
	for _, raw := range na.AsSlice(inspect["ieds"]) {
		row, _ := na.AsMap(raw)
		standing[[2]int{int(na.AsNumber(row["x"])), int(na.AsNumber(row["z"]))}] = true
	}
	for _, b := range tier.Buildings {
		if !standing[[2]int{int(b.Cell.X), int(b.Cell.Z)}] {
			return fmt.Errorf("tier IED %s at %d,%d not standing natively: %v", b.Definition, b.Cell.X, b.Cell.Z, inspect["ieds"])
		}
	}
	return nil
}

// iedsSprung wants at least one IED from the layout audit gone once the
// raid resolved: an IED explodes when sprung and is destroyed.
func iedsSprung(afterLayout, afterRaid map[string]any) error {
	now := map[string]bool{}
	for _, raw := range na.AsSlice(afterRaid["ieds"]) {
		row, _ := na.AsMap(raw)
		now[na.AsString(row["id"])] = true
	}
	for _, raw := range na.AsSlice(afterLayout["ieds"]) {
		row, _ := na.AsMap(raw)
		if !now[na.AsString(row["id"])] {
			return nil
		}
	}
	return fmt.Errorf("no IED sprung during the edge raid: %v before, %v after", afterLayout["ieds"], afterRaid["ieds"])
}
