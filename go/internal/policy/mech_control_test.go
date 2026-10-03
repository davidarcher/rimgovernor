package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func mechCatalog() MechCatalog {
	return MechCatalog{
		Kinds: map[string]MechKind{
			"Mech_Lifter":  {Name: "Mech_Lifter", WorkMech: true, WorkTypes: []WorkType{"Hauling"}, BandwidthCost: 1},
			"Mech_Militor": {Name: "Mech_Militor", BandwidthCost: 1},
		},
		Modes: map[string]bool{"Work": true, "Escort": true, "Recharge": true}, Recharge: "Recharge",
	}
}

func mechanitor(groups int) MechanitorInput {
	return MechanitorInput{ID: "M", PawnMechanitor: PawnMechanitor{
		ControlGroups: domain.Known(groups), UsedBandwidth: domain.Known(2), TotalBandwidth: domain.Known(3)}}
}

func mech(id, kind string, group int, mode string) MechInput {
	m := MechInput{ID: PawnID(id), Kind: kind, PawnMech: PawnMech{Overseer: "M", ControlGroup: domain.Known(group)}}
	if mode != "" {
		m.WorkMode = domain.Known(mode)
	}
	return m
}

type settingRow struct {
	pawn  domain.PawnID
	group int
	mode  string
}

func rows(t *testing.T, in []domain.PawnSettings) []settingRow {
	var out []settingRow
	for _, s := range in {
		if g, ok := s.MechControlGroup(); ok {
			out = append(out, settingRow{pawn: s.Pawn(), group: g})
		} else if m, ok := s.MechWorkMode(); ok {
			out = append(out, settingRow{pawn: s.Pawn(), mode: m})
		} else {
			t.Fatal("unexpected arm", s.Kind())
		}
	}
	return out
}

func equalRows(a, b []settingRow) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func TestPlanMechControlSeparatesWorkersAndGuardsMovesFirst(t *testing.T) {
	// Both in group 0, mode Work: the guard moves to group 1 first, then
	// only the guard group's mode changes.
	got, err := PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(2)}, []MechInput{
		mech("W", "Mech_Lifter", 0, "Work"), mech("G", "Mech_Militor", 0, "Work")}, false, false)
	if err != nil {
		t.Fatal(err)
	}
	want := []settingRow{{pawn: "G", group: 1}, {pawn: "G", mode: "Escort"}}
	if r := rows(t, got); !equalRows(r, want) {
		t.Fatalf("plan %v want %v", r, want)
	}
}

func TestPlanMechControlSettledGroupsWriteNothing(t *testing.T) {
	got, err := PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(2)}, []MechInput{
		mech("W", "Mech_Lifter", 0, "Work"), mech("G", "Mech_Militor", 1, "Escort")}, true, false)
	if err != nil || len(got) != 0 {
		t.Fatal(got, err)
	}
}

func TestPlanMechControlKeepsTheBusierGroupForARole(t *testing.T) {
	// Workers already sit in group 1, so they stay; the guard takes group 0.
	got, err := PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(2)}, []MechInput{
		mech("W1", "Mech_Lifter", 1, "Work"), mech("W2", "Mech_Lifter", 1, "Work"), mech("G", "Mech_Militor", 1, "Work")}, false, false)
	if err != nil {
		t.Fatal(err)
	}
	want := []settingRow{{pawn: "G", group: 0}, {pawn: "G", mode: "Escort"}}
	if r := rows(t, got); !equalRows(r, want) {
		t.Fatalf("plan %v want %v", r, want)
	}
}

func TestPlanMechControlSingleGroupThreatBeatsWork(t *testing.T) {
	in := []MechInput{mech("W", "Mech_Lifter", 0, "Work"), mech("G", "Mech_Militor", 0, "Work")}
	calm, err := PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(1)}, in, false, false)
	if err != nil || len(calm) != 0 {
		t.Fatal("a calm shared group keeps Work", calm, err)
	}
	raid, err := PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(1)}, in, true, false)
	if err != nil {
		t.Fatal(err)
	}
	// The mode is the group's: the first mech in it carries the write.
	if r := rows(t, raid); !equalRows(r, []settingRow{{pawn: "W", mode: "Escort"}}) {
		t.Fatal("a raid sends the shared group to Escort", r)
	}
}

func TestPlanMechControlGuardsOnlyEscortWithoutThreat(t *testing.T) {
	got, err := PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(1)}, []MechInput{mech("G", "Mech_Militor", 0, "Work")}, false, false)
	if err != nil {
		t.Fatal(err)
	}
	if r := rows(t, got); !equalRows(r, []settingRow{{pawn: "G", mode: "Escort"}}) {
		t.Fatal(r)
	}
}

func TestPlanMechControlSkipsUnreadAndUncontrolledFailsLoudOnUnknownDefs(t *testing.T) {
	unread := mechanitor(2)
	unread.ControlGroups = domain.Unknown[int]()
	stray := mech("S", "Mech_Militor", 0, "Work")
	stray.Overseer = ""
	if got, err := PlanMechControl(mechCatalog(), []MechanitorInput{unread}, []MechInput{mech("G", "Mech_Militor", 0, "Work")}, false, false); err != nil || len(got) != 0 {
		t.Fatal(got, err)
	}
	if got, err := PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(2)}, []MechInput{stray}, false, false); err != nil || len(got) != 0 {
		t.Fatal(got, err)
	}
	if _, err := PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(2)}, []MechInput{mech("X", "Mech_Nope", 0, "")}, false, false); err == nil {
		t.Fatal("an unknown mech kind must fail")
	}
	noEscort := mechCatalog()
	delete(noEscort.Modes, "Escort")
	if _, err := PlanMechControl(noEscort, nil, nil, false, false); err == nil {
		t.Fatal("a catalog without the Escort mode must fail")
	}
}

func TestMechRoleNextColonistNeedFirst(t *testing.T) {
	m := mechanitor(2)
	gap := []WorkCoverage{{Work: "Hauling", Demand: 2, Owners: 1}}
	role, ok, err := MechRoleNext(mechCatalog(), m, nil, gap)
	if err != nil || !ok || role != MechWorker {
		t.Fatal("a hauling gap with no mech on it buys a worker", role, ok, err)
	}
	role, _, _ = MechRoleNext(mechCatalog(), m, []MechInput{mech("W", "Mech_Lifter", 0, "")}, gap)
	if role != MechGuard {
		t.Fatal("a gap a mech already covers buys a guard", role)
	}
	role, _, _ = MechRoleNext(mechCatalog(), m, nil, []WorkCoverage{{Work: "Hauling", Demand: 1, Owners: 1}})
	if role != MechGuard {
		t.Fatal("no gap buys a guard", role)
	}
	role, _, _ = MechRoleNext(mechCatalog(), m, nil, []WorkCoverage{{Work: "Doctor", Demand: 1}})
	if role != MechGuard {
		t.Fatal("a gap no work mech kind can fill buys a guard", role)
	}
	full := m
	full.UsedBandwidth = domain.Known(3)
	if _, ok, _ := MechRoleNext(mechCatalog(), full, nil, gap); ok {
		t.Fatal("no free bandwidth buys nothing")
	}
}

func TestPlanMechGuardsOrdersNearestHostileInOverseerRange(t *testing.T) {
	m := mechanitor(2)
	m.Cell = domain.Known(domain.Cell{X: 50, Z: 50})
	guard := func(id string, x int32) MechInput {
		g := mech(id, "Mech_Militor", 1, "Escort")
		g.Cell = domain.Known(domain.Cell{X: x, Z: 50})
		return g
	}
	drafted := guard("G2", 52)
	drafted.Drafted = true
	engaged := guard("G3", 52)
	engaged.Target = "H1"
	down := guard("G4", 52)
	down.Downed = true
	worker := mech("W", "Mech_Lifter", 0, "Work")
	worker.Cell = domain.Known(domain.Cell{X: 51, Z: 50})
	hostiles := []MechHostile{{ID: "H1", Cell: domain.Cell{X: 60, Z: 50}}, {ID: "H2", Cell: domain.Cell{X: 40, Z: 50}}, {ID: "Far", Cell: domain.Cell{X: 120, Z: 50}}}
	got, err := PlanMechGuards(mechCatalog(), []MechanitorInput{m}, []MechInput{guard("G1", 48), drafted, engaged, down, worker}, hostiles)
	if err != nil {
		t.Fatal(err)
	}
	// G1 (x=48) is nearest H2 (x=40); G2 (x=52) is nearest H1 (x=60), already
	// drafted; G3 already attacks its nearest; the downed guard and the worker
	// get nothing; Far is beyond the overseer's range.
	wantOrders := []CombatOrder{
		{Pawn: "G1", Kind: OrderAttack, Target: "H2", Reason: ReasonFormation},
		{Pawn: "G2", Kind: OrderAttack, Target: "H1", Reason: ReasonFormation},
	}
	if len(got.Orders) != 2 || got.Orders[0] != wantOrders[0] || got.Orders[1] != wantOrders[1] {
		t.Fatalf("orders %v", got.Orders)
	}
	if len(got.Drafts) != 1 || got.Drafts[0] != "G1" {
		t.Fatalf("drafts %v", got.Drafts)
	}
	if got, err := PlanMechGuards(mechCatalog(), []MechanitorInput{m}, []MechInput{guard("G1", 48)}, []MechHostile{{ID: "Far", Cell: domain.Cell{X: 120, Z: 50}}}); err != nil || len(got.Orders) != 0 {
		t.Fatal("a hostile past the command range is not ordered at", got, err)
	}
}
