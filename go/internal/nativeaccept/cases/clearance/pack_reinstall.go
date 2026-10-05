package clearance

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/planstage"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// clearance/pack-reinstall (#2118, epic #2101): a standing furnished room is
// reconciled to a different plan and its packable piece makes the round trip.
// The plan's starter shelter stands finished with one Bed in it (quality
// Excellent, 60 percent hit points) and the plan's first bedroom stands
// finished, roofed and empty. The sleeping planner reconciles the bedroom
// (BedroomReconcile): the shelter's vacant bed is packed (uninstall_building,
// vanilla minifies it and hauls it to the stockpile) and the bedroom's bed is
// installed from that packed stock (move_building) in its template cell rather
// than built on site. The case asserts the same building came out the other
// end: its load id is the one staged, spawned and unpacked in the bedroom, its
// quality and hit points unchanged.
//
// A Go snapshot cannot cover it: the assertion is the native uninstall and
// install jobs with their reservations, the ReadPackedItems read contract
// (the packed item's inner id is the building's) and vanilla minifying, which
// keeps the inner thing's quality and hit points. Which cell the piece lands
// on, and the planner decisions behind the reconcile, are snapshot tests
// (policy.Reconcile, BedroomTemplate). Hauling to the warehouse is vanilla's
// own work after the uninstall and is not asserted: the controller installs
// from wherever the packed item lies.
const (
	packQuality = 4   // Excellent
	packHitFrac = 0.6 // damaged: hit points must survive the round trip
	packWood    = 300 // floors and the bedroom's own work
)

func init() {
	cases.Register(cases.Case{
		Name: "clearance/pack-reinstall",
		Scope: "A standing starter shelter holding a Bed and an empty standing planned bedroom: the sleeping planner packs the " +
			"shelter bed and installs the same building, quality and hit points kept, in the bedroom from packed stock (#2118). " +
			"Native: uninstall and install jobs with their reservations, the ReadPackedItems read contract, vanilla minifying.",
		Start:       cases.LabStart(),
		RequiredOps: []string{planstage.Tool},
		Serve: &cases.ServeSpec{Families: []string{"sleeping", "clearance", "work"},
			NativeTimeout: 60 * time.Second, Prefix: "pack-reinstall"},
		Budget: 14 * time.Minute,
		Reason: "two service runs: an inert one reads the layout plan the rooms are staged from, then the controller reconciles; " +
			"the pack, haul and install are real pawn work over game time",
		Run: runPackReinstall,
	})
}

func runPackReinstall(ctx context.Context, s cases.Session) error {
	report, h := s.Report(), s.Harness()
	center, _ := na.AsMap(s.Prepared()["center"])
	middle := planstage.Cell{X: int32(na.AsNumber(center["x"])), Z: int32(na.AsNumber(center["z"]))}
	if middle == (planstage.Cell{}) {
		return fmt.Errorf("lab start: no centre in %#v", s.Prepared())
	}
	// Wooden beds need ComplexFurniture and the tier reads Masonry with
	// Stonecutting, as the bedroom case's colony does.
	if _, err := planstage.Stage(ctx, h, "stage-research", planstage.Spec{Research: []string{"Stonecutting", "ComplexFurniture"}}); err != nil {
		return err
	}

	// Phase one: an inert controller derives and records the layout plan.
	one, err := planstage.Begin(ctx, s, nil, []string{"work"})
	if err != nil {
		return err
	}
	var shelter, bedroom policy.PlannedRoom
	plan, err := one.Plan(ctx, "layout plan with a shelter and a bedroom", func(p policy.LayoutPlan) bool {
		_, hasShelter := planstage.FirstRoom(p, policy.PlannedShelter)
		_, hasBedroom := planstage.FirstRoom(p, policy.PlannedBedroom)
		return hasShelter && hasBedroom
	})
	if err != nil {
		one.Abort()
		return err
	}
	shelter, _ = planstage.FirstRoom(plan, policy.PlannedShelter)
	bedroom, _ = planstage.FirstRoom(plan, policy.PlannedBedroom)
	if h, err = one.Stop(ctx, "plan_service"); err != nil {
		return err
	}

	// Stage both rooms finished, the bed in the shelter, wood in a stockpile.
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
	spec.Things = []planstage.Building{{Def: "Bed", Stuff: "WoodLog", X: in.X, Z: in.Z, Rotation: bedRot, Quality: ptr(packQuality), HitFraction: ptr(packHitFrac)}}
	spec.Drops = []planstage.Drop{{Def: "WoodLog", Count: packWood, X: pile.MinX, Z: pile.MinZ}}
	spec.Stockpile = &pile
	staged, err := planstage.Stage(ctx, h, "stage-rooms", spec)
	if err != nil {
		return err
	}
	if len(staged.Things) != 1 {
		return fmt.Errorf("staged %d things, want the one bed: %#v", len(staged.Things), staged.Reply)
	}
	bed := staged.Things[0]
	bedCells := append(bedRing.Cells(), shellRing.Cells()...)
	before, err := planstage.Read(ctx, h, "audit-before", []string{bed}, bedCells)
	if err != nil {
		return err
	}
	start := before.Things[bed]
	report["staged"] = map[string]any{"shelter": shelter.Interior, "bedroom": bedroom.Interior, "bed": start, "stockpile": pile}
	if !start.Found || start.Def != "Bed" || start.Quality != packQuality || start.HitPoints >= start.MaxHP {
		return fmt.Errorf("the staged bed is not a damaged Excellent Bed: %+v", start)
	}
	if err := bedRing.Sealed(before); err != nil {
		return fmt.Errorf("the staged bedroom is not a standing room: %w", err)
	}

	// Phase two: the real families reconcile the bedroom.
	two, err := planstage.Begin(ctx, s, one.Svc, s.Spec().Families)
	if err != nil {
		return err
	}
	var packedDone, installedDone bool
	var installed domain.MoveBuilding
	err = two.Until(ctx, "the shelter bed packed and installed in the bedroom", func(store.Rounds) (string, bool, error) {
		plans, err := two.St.PlanHistoryWithMethods(ctx, 256, "*")
		if err != nil {
			return "", false, err
		}
		for _, plan := range plans {
			for _, progress := range plan.Progress {
				done := progress.View().Stage == domain.Completed
				if move, uninstall, ok := progress.Action().Relocation(); ok && move.Thing() == bed && done {
					if uninstall {
						packedDone = true
					} else {
						installedDone, installed = true, move
					}
				}
			}
		}
		return na.Signature(packedDone, installedDone), packedDone && installedDone, nil
	})
	if err != nil {
		two.Abort()
		return err
	}
	if h, err = two.Stop(ctx, "reconcile_service"); err != nil {
		return err
	}
	report["install"] = map[string]any{"cell": installed.Cell(), "rotation": installed.Rotation()}

	// The same building stands in the bedroom: spawned, not packed, quality and
	// hit points kept.
	cell := planstage.Cell{X: installed.Cell().X, Z: installed.Cell().Z}
	after, err := planstage.Read(ctx, h, "audit-after", []string{bed}, append([]planstage.Cell{cell}, bedCells...))
	if err != nil {
		return err
	}
	end := after.Things[bed]
	report["after"] = map[string]any{"bed": end, "cell": after.Cells[cell]}
	if !end.Found || end.Packed || !end.Spawned || end.Def != "Bed" {
		return fmt.Errorf("the bed did not come out of the pack installed: %+v", end)
	}
	if end.Quality != start.Quality || end.HitPoints != start.HitPoints {
		return fmt.Errorf("the round trip changed the bed: quality %d -> %d, hit points %d -> %d", start.Quality, end.Quality, start.HitPoints, end.HitPoints)
	}
	if !inside(bedroom.Interior, end.X, end.Z) || inside(shelter.Interior, end.X, end.Z) {
		return fmt.Errorf("the bed stands at %d,%d, not in the planned bedroom %v (shelter %v)", end.X, end.Z, bedroom.Interior, shelter.Interior)
	}
	if role := after.Cells[planstage.Cell{X: end.X, Z: end.Z}].Role; role != "Bedroom" {
		return fmt.Errorf("the bed's room reads %q, not Bedroom", role)
	}
	return nil
}

func ptr[T any](v T) *T { return &v }

func inside(r policy.Rectangle, x, z int32) bool {
	return x >= r.X && x < r.X+r.Width && z >= r.Z && z < r.Z+r.Height
}

// merge adds b's walls, doors and roofs to a; a wall or door cell both rings
// share is staged once.
func merge(a *planstage.Spec, b planstage.Spec) {
	have := map[planstage.Cell]bool{}
	for _, w := range a.Walls {
		have[planstage.Cell{X: w.X, Z: w.Z}] = true
	}
	for _, d := range a.Doors {
		have[planstage.Cell{X: d.X, Z: d.Z}] = true
	}
	for _, w := range b.Walls {
		if !have[planstage.Cell{X: w.X, Z: w.Z}] {
			a.Walls = append(a.Walls, w)
		}
	}
	for _, d := range b.Doors {
		if !have[planstage.Cell{X: d.X, Z: d.Z}] {
			a.Doors = append(a.Doors, d)
		}
	}
	a.Roof = append(a.Roof, b.Roof...)
	a.Floor = append(a.Floor, b.Floor...)
}
