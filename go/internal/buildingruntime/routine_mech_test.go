package buildingruntime

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func mechBiotechCatalog() *bridge.BiotechCatalog {
	return &bridge.BiotechCatalog{
		MechKinds: map[string]*o.MechKindRow{
			"Mech_Constructoid": {WorkMech: proto.Bool(true)},
			"Mech_Militor":      {WorkMech: proto.Bool(false)},
		},
		MechWorkModes: map[string]*o.MechWorkModeRow{"Work": {}, "Escort": {}, "Recharge": {Recharge: proto.Bool(true)}},
	}
}

func mechPawnRows() []*o.PawnState {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	boss := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("boss"), Position: cell(10, 10)}, Colonist: proto.Bool(true), Dead: proto.Bool(false),
		Biotech: &o.PawnBiotech{Mechanitor: &o.PawnMechanitor{ControlGroups: proto.Int32(2)}}}
	mech := func(id, kind string, group int32, mode string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), Position: cell(11, 10)}, KindDefName: proto.String(kind), Mechanoid: proto.Bool(true), Dead: proto.Bool(false),
			Biotech: &o.PawnBiotech{Mech: &o.PawnMech{Overseer: &c.Ref{Id: proto.String("boss")}, ControlGroup: proto.Int32(group), WorkMode: proto.String(mode)}}}
	}
	return []*o.PawnState{boss, mech("worker", "Mech_Constructoid", 0, "Work"), mech("guard", "Mech_Militor", 0, "Work")}
}

// A worker and a guard sharing group 0 under a two-group mechanitor: the
// routine plan moves the guard to the other group and sets its mode.
func TestRoutineMechSettingsSplitRolesThroughTheRead(t *testing.T) {
	var read observation.RoutineReading
	read.Projection.Mechs = domain.Known(observation.MechFleet(slices.Values(mechPawnRows())))
	read.Frame.Catalog = &bridge.DefinitionCatalog{Biotech: mechBiotechCatalog()}
	got, err := routineMechSettings(read)
	if err != nil {
		t.Fatal(err)
	}
	if len(got) != 2 {
		t.Fatalf("settings = %v, want a move and a mode", got)
	}
	if g, ok := got[0].MechControlGroup(); !ok || got[0].Pawn() != "guard" || g != 1 {
		t.Fatalf("first setting = %v, want guard to group 1", got[0])
	}
	if m, ok := got[1].MechWorkMode(); !ok || got[1].Pawn() != "guard" || m != "Escort" {
		t.Fatalf("second setting = %v, want guard Escort", got[1])
	}
}

func TestRoutineMechSettingsNoMechanitorPlansNothing(t *testing.T) {
	var read observation.RoutineReading
	read.Projection.Mechs = domain.Known(policy.MechFleet{})
	got, err := routineMechSettings(read)
	if err != nil || len(got) != 0 {
		t.Fatalf("got %v, %v", got, err)
	}
}

// The combat frame's mechs: a guard is drafted and ordered at the hostile in
// its overseer's range.
func TestCombatMechGuardsOrderGuardAtHostile(t *testing.T) {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	rows := mechPawnRows()
	table := bridge.NewPawns(rows...)
	combat := bridge.Combat{Detail: table, Catalog: &bridge.DefinitionCatalog{Biotech: mechBiotechCatalog()}}
	raider := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("raider"), Position: cell(20, 10)}}
	plan, err := combatMechGuards(combat, []string{"raider"}, map[string]*o.PawnState{"raider": raider})
	if err != nil {
		t.Fatal(err)
	}
	if !slices.Equal(plan.Drafts, []domain.PawnID{"guard"}) || len(plan.Orders) != 1 || plan.Orders[0].Pawn != "guard" || plan.Orders[0].Target != "raider" || plan.Orders[0].Kind != policy.OrderAttack {
		t.Fatalf("plan = %+v", plan)
	}
}
