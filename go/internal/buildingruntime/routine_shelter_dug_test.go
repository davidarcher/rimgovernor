package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// dugShelterNative is the shelter site with natural rock on some planned
// storeroom cells and the excavation site read plan dig asks of it.
type dugShelterNative struct {
	*sleepingNative
	rock map[domain.Cell]bool
}

func (n *dugShelterNative) ReadExcavationSite(ctx context.Context, _ *c.Identity, cells []domain.Cell, _ domain.Cell) (bridge.ExcavationSite, bridge.Result, error) {
	site := bridge.ExcavationSite{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Support: policy.ExcavationSupportSupported, WorkerAvailable: true, AccessReachable: true, Workers: []string{"miner"}}
	for _, cell := range cells {
		row := bridge.ExcavationSiteCell{Cell: cell}
		if n.rock[cell] {
			row.Definition, row.Eligible = "Granite", true
		} else {
			row.Walkable = true
		}
		site.Cells = append(site.Cells, row)
	}
	return site, bridge.Result{}, ctx.Err()
}

// The initial shelter on a planned storeroom the plan marks Dug is mined
// by plan dig before anything else is placed (#1250): no excavation site
// is searched, and the dig mines exactly the storeroom's rock.
func TestRoutineShelterDigsADugPlannedStoreroom(t *testing.T) {
	t.Parallel()
	r, db, base := shelterSiteFixture(t)
	rock := map[domain.Cell]bool{}
	for i := range base.cells.Cells {
		cell := &base.cells.Cells[i]
		at := cell.Cell
		// The storeroom's east half (interior x 1..7, z 1..7) is rock.
		if at.X >= 5 && at.X <= 7 && at.Z >= 1 && at.Z <= 7 {
			rock[at] = true
			cell.Walkable, cell.Occupied, cell.NaturalRock = domain.Known(false), domain.Known(true), domain.Known(true)
		}
	}
	n := &dugShelterNative{sleepingNative: base, rock: rock}
	r.native = n
	room := policy.LayoutRoom{Role: policy.ModuleBarracks, Interior: policy.Rectangle{X: 1, Z: 1, Width: 7, Height: 7}, Door: domain.Cell{X: 4, Z: 0}, DoorRot: domain.South, Dug: true}
	recordLayout(t, r, db, policy.LayoutPlan{Rooms: []policy.LayoutRoom{room}})
	result, err := r.Step(context.Background())
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(context.Background(), methodPlan(t, result.Decision, digMethod("room", room)))
	if err != nil {
		t.Fatal(err)
	}
	dug := map[domain.Cell]bool{}
	for _, action := range plan.Spec.Actions() {
		excavation, ok := action.Excavation()
		if !ok {
			t.Fatal("plan dig admitted a non-excavation", action)
		}
		dug[excavation.Cell()] = true
	}
	if len(dug) != len(rock) {
		t.Fatal(len(dug), len(rock))
	}
	for cell := range rock {
		if !dug[cell] {
			t.Fatal("storeroom rock not dug", cell)
		}
	}
	for _, m := range result.Decision.Goal.Methods {
		if IsExcavationMethod(m.Method) {
			t.Fatal("excavation site search ran", m.Method)
		}
	}
}
