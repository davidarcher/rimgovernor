package gear

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// gear/soldier (#470, #471): two colonists at Shooting 12 and 8 with no
// weapon, no middle layer and no headgear; Smithing, Machining and
// FlakArmor finished; a fuelled smithy and a powered machining table with
// steel, cloth, components and plasteel funded; a bolt-action rifle and a
// pump shotgun loose. The armor ladder must dress both in a flak vest and
// a simple or flak helmet, and the weapon planner must hand the
// bolt-action to the better shot and the shotgun to the other. Lands with
// #472 ahead of the armor ladder (#470): it passes once that slice does.
const (
	soldierVest       = "Apparel_FlakVest"
	soldierHelmet     = "Apparel_SimpleHelmet"
	soldierFlakHelmet = "Apparel_FlakHelmet"
	soldierRifle      = "Gun_BoltActionRifle"
	soldierShotgun    = "Gun_PumpShotgun"
)

func init() {
	cases.Register(cases.Case{
		Name:   "gear/soldier",
		Scope:  fmt.Sprintf("Armor ladder and weapon fit (#470, #471): two colonists with Shooting 12 and 8, Smithing and FlakArmor researched and steel funded, both end in a %s plus a %s or %s, the %s on the better shot and the %s on the other.", soldierVest, soldierHelmet, soldierFlakHelmet, soldierRifle, soldierShotgun),
		Start:  start("soldier", nil),
		Keep:   []string{string(na.NeedFood)},
		Serve:  serve(soldierFamilies, "gear-soldier"),
		Budget: watch + 3*time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			return observe(ctx, s, 2*dayTicks, nil, func(ctx context.Context, h *na.Harness, report na.Report, timeline []map[string]any, final census) error {
				return auditSoldier(ctx, s, report, timeline, final)
			})
		},
	})
}

func auditSoldier(ctx context.Context, s cases.Session, report na.Report, timeline []map[string]any, final census) error {
	_, summary, err := equipmentMethods(ctx, s.Config().Output)
	report["equipment_goal"] = summary
	if err != nil {
		return err
	}
	// The fixture names the two soldiers with the Shooting level it set;
	// the census confirms the level and carries what each wears and holds.
	type soldier struct {
		Pawn     string
		Shooting int
		Census   *colonist
	}
	var soldiers []soldier
	for _, raw := range na.AsSlice(s.Prepared()["soldiers"]) {
		row, _ := na.AsMap(raw)
		sd := soldier{Pawn: na.AsString(row["pawn"]), Shooting: int(na.AsNumber(row["shooting"]))}
		for i := range final.Colonists {
			if final.Colonists[i].Pawn == sd.Pawn {
				sd.Census = &final.Colonists[i]
			}
		}
		if sd.Census == nil {
			return fmt.Errorf("soldier %s missing from the final census", sd.Pawn)
		}
		if sd.Census.Shooting != sd.Shooting {
			return fmt.Errorf("soldier %s is at Shooting %d, the fixture set %d", sd.Census.Name, sd.Census.Shooting, sd.Shooting)
		}
		soldiers = append(soldiers, sd)
	}
	if len(soldiers) != 2 {
		return fmt.Errorf("fixture staged %d soldiers, not two", len(soldiers))
	}
	if soldiers[0].Shooting < soldiers[1].Shooting {
		soldiers[0], soldiers[1] = soldiers[1], soldiers[0]
	}
	var failures []string
	for _, sd := range soldiers {
		if !sd.Census.wears(soldierVest) {
			failures = append(failures, fmt.Sprintf("%s wears no %s", sd.Census.Name, soldierVest))
		}
		if !sd.Census.wears(soldierHelmet, soldierFlakHelmet) {
			failures = append(failures, fmt.Sprintf("%s wears no %s or %s", sd.Census.Name, soldierHelmet, soldierFlakHelmet))
		}
	}
	if soldiers[0].Census.PrimaryDefinition != soldierRifle {
		failures = append(failures, fmt.Sprintf("%s (Shooting %d) holds %q, not the %s", soldiers[0].Census.Name, soldiers[0].Shooting, soldiers[0].Census.PrimaryDefinition, soldierRifle))
	}
	if soldiers[1].Census.PrimaryDefinition != soldierShotgun {
		failures = append(failures, fmt.Sprintf("%s (Shooting %d) holds %q, not the %s", soldiers[1].Census.Name, soldiers[1].Shooting, soldiers[1].Census.PrimaryDefinition, soldierShotgun))
	}
	report["soldier_failures"] = failures
	if len(failures) > 0 {
		return fmt.Errorf("soldier loadouts: %v", failures)
	}
	if _, ok := firstRecoveredTick(timeline); !ok {
		return fmt.Errorf("MaintainEquipment never recovered within the window: %v", summary)
	}
	return nil
}
