package clearance

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/planstage"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// clearance/room-obstruction (#2278, epic #2241): a planned room's ground holds
// things the colony did not build. The plan's starter shelter stands finished
// with one Bed and the plan's first bedroom stands finished and roofed, with an
// unowned tree on one interior cell and an unowned ancient casket, still holding
// its contents, on the cell the bedroom's bed belongs on. The sleeping planner
// reconciles the bedroom: the tree is cut (the obstruction policy's cut wave),
// the casket is left standing and the room's wait names it (a foreign_held row
// naming the casket and why: "casket" or "ancient_danger").
//
// A Go snapshot cannot cover it: the assertion is the native thing list reading
// a foreign tree and a casket as the flags the policy keys on (impassable plant,
// casket contents), the native cut job and the vanilla casket staying put. Which
// operation each foreign thing owes is a snapshot test (policy.ReconcileRoomHolds).
const obstructionWood = 300

func init() {
	cases.Register(cases.Case{
		Name: "clearance/room-obstruction",
		Scope: "A standing planned bedroom whose ground holds an unowned tree and a filled ancient casket on its bed slot: the sleeping " +
			"planner cuts the tree, leaves the casket standing and reports the hold in a foreign_held row (#2278). Native: the thing list's " +
			"impassable and casket facts, the cut job.",
		Start:       cases.LabStart(),
		RequiredOps: []string{planstage.Tool},
		Serve: &cases.ServeSpec{Families: []string{"sleeping", "clearance", "work"},
			NativeTimeout: 60 * time.Second, Prefix: "room-obstruction"},
		Budget: 14 * time.Minute,
		Reason: "two service runs: an inert one reads the layout plan the rooms are staged from, then the controller reconciles; the cut is real pawn work over game time",
		Run:    runRoomObstruction,
	})
}

func runRoomObstruction(ctx context.Context, s cases.Session) error {
	report, h := s.Report(), s.Harness()
	center, _ := na.AsMap(s.Prepared()["center"])
	middle := planstage.Cell{X: int32(na.AsNumber(center["x"])), Z: int32(na.AsNumber(center["z"]))}
	if middle == (planstage.Cell{}) {
		return fmt.Errorf("lab start: no centre in %#v", s.Prepared())
	}
	if _, err := planstage.Stage(ctx, h, "stage-research", planstage.Spec{Research: []string{"Stonecutting", "ComplexFurniture"}}); err != nil {
		return err
	}

	// Phase one: an inert controller derives and records the layout plan.
	one, err := planstage.Begin(ctx, s, nil, []string{"work"})
	if err != nil {
		return err
	}
	plan, err := one.Plan(ctx, "layout plan with a shelter and a bedroom", func(p policy.LayoutPlan) bool {
		_, hasShelter := planstage.FirstRoom(p, policy.PlannedShelter)
		_, hasBedroom := planstage.FirstRoom(p, policy.PlannedBedroom)
		return hasShelter && hasBedroom
	})
	if err != nil {
		one.Abort()
		return err
	}
	shelter, _ := planstage.FirstRoom(plan, policy.PlannedShelter)
	bedroom, _ := planstage.FirstRoom(plan, policy.PlannedBedroom)
	if h, err = one.Stop(ctx, "plan_service"); err != nil {
		return err
	}

	// The bed slot the bedroom template holds: a Bed is one by two.
	// The core furniture rows, the catalog shapes the template lays the room out from.
	shapes, err := bridge.FixtureCatalog("room-obstruction", bridge.WithCoreFurniture(nil)...).PieceShapes()
	if err != nil {
		return err
	}
	slots, ok := policy.BedroomTemplate(bedroom, shapes, "Bed")
	if !ok || len(slots) != 1 {
		return fmt.Errorf("the bedroom template does not fit planned room %+v", bedroom.Interior)
	}
	slot := slots[0]
	treeCell, ok := farthestFree(bedroom, slot)
	if !ok {
		return fmt.Errorf("no interior cell off the bed slot %+v for the tree in %+v", slot, bedroom.Interior)
	}

	shellRing, bedRing := planstage.RingOf(plan, shelter), planstage.RingOf(plan, bedroom)
	pile, ok := planstage.FreeRect(plan, middle, 6, 4)
	if !ok {
		return fmt.Errorf("no free ground for the stockpile in the layout plan %s", plan.Summary())
	}
	spec := shellRing.Spec("WoodLog", "")
	merge(&spec, bedRing.Spec("WoodLog", ""))
	bedRot, in := 0, shelter.Interior
	if in.Height < 2 {
		bedRot = 1
	}
	anchor := slot.Anchor()
	spec.Things = []planstage.Building{
		{Def: "Bed", Stuff: "WoodLog", X: in.X, Z: in.Z, Rotation: bedRot},
		{Def: "Plant_TreeOak", X: treeCell.X, Z: treeCell.Z, Foreign: true},
		{Def: "AncientCryptosleepCasket", X: anchor.X, Z: anchor.Z, Rotation: planstage.RotationNumber(slot.Rot), Foreign: true, Filled: true},
	}
	spec.Drops = []planstage.Drop{{Def: "WoodLog", Count: obstructionWood, X: pile.MinX, Z: pile.MinZ}}
	spec.Stockpile = &pile
	staged, err := planstage.Stage(ctx, h, "stage-rooms", spec)
	if err != nil {
		return err
	}
	if len(staged.Things) != 3 {
		return fmt.Errorf("staged %d things, want the bed, the tree and the casket: %#v", len(staged.Things), staged.Reply)
	}
	tree, casket := staged.Things[1], staged.Things[2]
	cells := append(bedRing.Cells(), shellRing.Cells()...)
	before, err := planstage.Read(ctx, h, "audit-before", []string{tree, casket}, cells)
	if err != nil {
		return err
	}
	report["staged"] = map[string]any{"bedroom": bedroom.Interior, "bed_slot": slot, "tree_cell": treeCell, "tree": before.Things[tree], "casket": before.Things[casket]}
	if t := before.Things[tree]; !t.Found || !t.Spawned || t.X != treeCell.X || t.Z != treeCell.Z {
		return fmt.Errorf("the staged tree is not standing on %v: %+v", treeCell, t)
	}
	if c := before.Things[casket]; !c.Found || !c.Spawned {
		return fmt.Errorf("the staged casket is not standing: %+v", c)
	}
	if err := bedRing.Sealed(before); err != nil {
		return fmt.Errorf("the staged bedroom is not a standing room: %w", err)
	}

	// Phase two: the real families reconcile the bedroom.
	two, err := planstage.Begin(ctx, s, one.Svc, s.Spec().Families)
	if err != nil {
		return err
	}
	var cut bool
	var held []string
	err = two.Until(ctx, "the tree cut and the casket hold reported", func(store.Rounds) (string, bool, error) {
		plans, err := two.St.PlanHistoryWithMethods(ctx, 256, "*-cut-*")
		if err != nil {
			return "", false, err
		}
		cut = false
		for _, plan := range plans {
			done := len(plan.Progress) > 0
			for _, progress := range plan.Progress {
				done = done && progress.View().Stage == domain.Completed
			}
			cut = cut || done
		}
		messages, err := na.FlightMessages(two.Svc.FlightPath, "foreign_held")
		if err != nil {
			return "", false, err
		}
		held = held[:0]
		for _, msg := range messages {
			if strings.Contains(msg, "AncientCryptosleepCasket@") {
				held = append(held, msg)
			}
		}
		return na.Signature(cut, len(held)), cut && len(held) > 0, nil
	})
	refusals, refusalErr := na.RefusalsNameBlockers(two.Svc.FlightPath)
	if err != nil {
		two.Abort()
		return err
	}
	report["foreign_held"], report["placement_refused"] = held, refusals
	if refusalErr != nil {
		two.Abort()
		return refusalErr
	}
	if !strings.HasSuffix(held[0], ":casket") && !strings.HasSuffix(held[0], ":ancient_danger") {
		two.Abort()
		return fmt.Errorf("the casket hold names no reason: %q", held[0])
	}
	if h, err = two.Stop(ctx, "reconcile_service"); err != nil {
		return err
	}

	// The tree is gone, the casket stands untouched on the bed slot.
	after, err := planstage.Read(ctx, h, "audit-after", []string{tree, casket}, []planstage.Cell{{X: anchor.X, Z: anchor.Z}})
	if err != nil {
		return err
	}
	report["after"] = map[string]any{"tree": after.Things[tree], "casket": after.Things[casket]}
	if t := after.Things[tree]; t.Found {
		return fmt.Errorf("the tree still stands after its cut plan completed: %+v", t)
	}
	if c := after.Things[casket]; !c.Found || !c.Spawned || c.X != before.Things[casket].X || c.Z != before.Things[casket].Z {
		return fmt.Errorf("the held casket was disturbed: %+v", c)
	}
	return nil
}

// farthestFree is the bedroom's interior cell farthest from its door that the
// bed slot does not cover.
func farthestFree(room policy.PlannedRoom, slot policy.WantedPiece) (planstage.Cell, bool) {
	var best planstage.Cell
	far, found := int32(-1), false
	for x := room.Interior.X; x < room.Interior.X+room.Interior.Width; x++ {
		for z := room.Interior.Z; z < room.Interior.Z+room.Interior.Height; z++ {
			if x >= slot.Minimum.X && x <= slot.Maximum.X && z >= slot.Minimum.Z && z <= slot.Maximum.Z {
				continue
			}
			d := max(x-room.Door.X, room.Door.X-x) + max(z-room.Door.Z, room.Door.Z-z)
			if d > far {
				best, far, found = planstage.Cell{X: x, Z: z}, d, true
			}
		}
	}
	return best, found
}
