package bridge

import (
	"context"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func combatCell(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
func combatPawn(id string) *o.EntityPrecondition {
	return &o.EntityPrecondition{EntityId: proto.String(id)}
}

// combatTestOrders is every order kind once, as the native case issues them.
func combatTestOrders() *o.CombatOrders {
	return &o.CombatOrders{Orders: []*o.CombatOrder{
		{Pawn: combatPawn("p0"), Order: &o.CombatOrder_AttackGround{AttackGround: combatCell(5, 6)}},
		{Pawn: combatPawn("p1"), Order: &o.CombatOrder_Move{Move: combatCell(7, 8)}},
		{Pawn: combatPawn("p2"), Order: &o.CombatOrder_Attack{Attack: combatPawn("h0")}},
		{Pawn: combatPawn("p3"), Order: &o.CombatOrder_FireMode{FireMode: o.CombatFireMode_COMBAT_FIRE_MODE_HOLD}},
		{Pawn: combatPawn("p3"), Order: &o.CombatOrder_HoldPosition{HoldPosition: &o.Clear{}}},
		{Pawn: combatPawn("p4"), Order: &o.CombatOrder_Stop{Stop: &o.Clear{}}},
		{Pawn: combatPawn("p5"), Order: &o.CombatOrder_Draft{Draft: &o.Clear{}}},
		{Order: &o.CombatOrder_Door{Door: &o.CombatDoor{Cell: combatCell(1, 1), Mode: o.CombatDoorMode_COMBAT_DOOR_MODE_HOLD_OPEN.Enum()}}},
	}}
}

func TestValidateCombatOrders(t *testing.T) {
	if err := ValidateCombatOrders(combatTestOrders()); err != nil {
		t.Fatal(err)
	}
	for name, edit := range map[string]func(*o.CombatOrders){
		"empty":           func(v *o.CombatOrders) { v.Orders = nil },
		"nil order":       func(v *o.CombatOrders) { v.Orders[0] = nil },
		"no order":        func(v *o.CombatOrders) { v.Orders[0].Order = nil },
		"no pawn":         func(v *o.CombatOrders) { v.Orders[1].Pawn = nil },
		"bad pawn id":     func(v *o.CombatOrders) { v.Orders[1].Pawn.EntityId = proto.String("") },
		"bad token":       func(v *o.CombatOrders) { v.Orders[1].Pawn.ExpectedSnapshotToken = proto.String("") },
		"negative cell":   func(v *o.CombatOrders) { v.Orders[1].Order = &o.CombatOrder_Move{Move: combatCell(-1, 2)} },
		"missing cell":    func(v *o.CombatOrders) { v.Orders[0].Order = &o.CombatOrder_AttackGround{} },
		"self attack":     func(v *o.CombatOrders) { v.Orders[2].Order = &o.CombatOrder_Attack{Attack: combatPawn("p2")} },
		"fire mode unset": func(v *o.CombatOrders) { v.Orders[3].Order = &o.CombatOrder_FireMode{} },
		"door with pawn":  func(v *o.CombatOrders) { v.Orders[7].Pawn = combatPawn("p0") },
		"door no mode":    func(v *o.CombatOrders) { v.Orders[7].GetDoor().Mode = nil },
		"door no cell":    func(v *o.CombatOrders) { v.Orders[7].GetDoor().Cell = nil },
		"draft nil":       func(v *o.CombatOrders) { v.Orders[6].Order = &o.CombatOrder_Draft{} },
		"draft no pawn":   func(v *o.CombatOrders) { v.Orders[6].Pawn = nil },
		"stop nil":        func(v *o.CombatOrders) { v.Orders[5].Order = &o.CombatOrder_Stop{} },
	} {
		v := combatTestOrders()
		edit(v)
		if err := ValidateCombatOrders(v); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	token := combatTestOrders()
	token.Orders[0].Pawn.ExpectedSnapshotToken = proto.String("tok")
	if err := ValidateCombatOrders(token); err != nil {
		t.Errorf("snapshot token refused: %v", err)
	}
}

func combatResult(i int, pawn string, applied bool, refusal, job string) *r.CombatOrderResult {
	v := &r.CombatOrderResult{Index: proto.Uint32(uint32(i)), Applied: proto.Bool(applied)}
	if pawn != "" {
		v.PawnId = proto.String(pawn)
	}
	if refusal != "" {
		v.Refusal = proto.String(refusal)
	}
	if job != "" {
		v.JobDef = proto.String(job)
	}
	return v
}

func combatTestResults() []*r.CombatOrderResult {
	return []*r.CombatOrderResult{
		combatResult(0, "p0", true, "", "AttackStatic"),
		combatResult(1, "p1", true, "", "Goto"),
		combatResult(2, "p2", false, CombatRefusalNotDrafted, ""),
		combatResult(3, "p3", true, "", ""),
		combatResult(4, "p3", true, "", "Wait_Combat"),
		combatResult(5, "p4", true, "", ""),
		combatResult(6, "p5", true, "", ""),
		combatResult(7, "", false, CombatRefusalNotADoor, ""),
	}
}

// combatReceipt is the applied action receipt: an applied combat_orders
// action carries every order's result, refused ones included.
func combatReceipt(results []*r.CombatOrderResult) *r.Receipt {
	evidence := &r.EffectEvidence{Effect: &r.EffectEvidence_CombatOrders{CombatOrders: &r.CombatOrdersEffect{Results: results}}}
	return &r.Receipt{AdmittedContext: pbContext(), Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: evidence}}}
}

func TestCombatOrderResultsDecode(t *testing.T) {
	got, err := CombatOrderResults(combatReceipt(combatTestResults()), combatTestOrders())
	if err != nil {
		t.Fatal(err)
	}
	want := []CombatOrderResult{
		{0, "p0", true, "", "AttackStatic"}, {1, "p1", true, "", "Goto"}, {2, "p2", false, "not_drafted", ""},
		{3, "p3", true, "", ""}, {4, "p3", true, "", "Wait_Combat"}, {5, "p4", true, "", ""},
		{6, "p5", true, "", ""}, {7, "", false, "not_a_door", ""},
	}
	for i := range want {
		if got[i] != want[i] {
			t.Errorf("result %d = %+v, want %+v", i, got[i], want[i])
		}
	}
	allRefused := []*r.CombatOrderResult{combatResult(0, "p0", false, CombatRefusalStaleSnapshot, "")}
	one := &o.CombatOrders{Orders: combatTestOrders().Orders[:1]}
	if got, err := CombatOrderResults(combatReceipt(allRefused), one); err != nil || got[0].Refusal != "stale_snapshot" {
		t.Errorf("no-change decode: %v %+v", err, got)
	}
	uncertain := &r.Receipt{AdmittedContext: pbContext()}
	uncertain.Outcome = &r.Receipt_Uncertain{Uncertain: &r.Uncertain{Detail: proto.String("x")}}
	if got, err := CombatOrderResults(uncertain, one); err != nil || got != nil {
		t.Errorf("uncertain decode: %v %+v", err, got)
	}
	for _, tc := range []struct {
		name string
		edit func([]*r.CombatOrderResult) []*r.CombatOrderResult
		want string
	}{
		{"short", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { return v[:7] }, "7 results for 8 orders"},
		{"index", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { v[1].Index = proto.Uint32(3); return v }, "index"},
		{"pawn", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { v[1].PawnId = proto.String("p9"); return v }, "names pawn"},
		{"door pawn", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { v[7].PawnId = proto.String("p0"); return v }, "names pawn"},
		{"no reason", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { v[2].Refusal = nil; return v }, "known reason"},
		{"unknown reason", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { v[2].Refusal = proto.String("nope"); return v }, "known reason"},
		{"applied refusal", func(v []*r.CombatOrderResult) []*r.CombatOrderResult {
			v[0].Refusal = proto.String("cannot_hit")
			return v
		}, "applied with a refusal"},
		{"refused job", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { v[2].JobDef = proto.String("Goto"); return v }, "refused with a job"},
	} {
		_, err := CombatOrderResults(combatReceipt(tc.edit(combatTestResults())), combatTestOrders())
		if err == nil || !strings.Contains(err.Error(), tc.want) {
			t.Errorf("%s: err %v, want %q", tc.name, err, tc.want)
		}
	}
}

func TestCombatDrugRefusalsPreserveBatch(t *testing.T) {
	// Use the native strings independently of the bridge constants.
	for _, reason := range []string{"child", "not_a_drug", "no_drug", "already_high", "drug_risk"} {
		t.Run(reason, func(t *testing.T) {
			orders := &o.CombatOrders{Orders: []*o.CombatOrder{
				{Pawn: combatPawn("p0"), Order: &o.CombatOrder_Move{Move: combatCell(7, 8)}},
				{Pawn: combatPawn("p1"), Order: &o.CombatOrder_CombatDrug{CombatDrug: "GoJuice"}},
				{Pawn: combatPawn("p2"), Order: &o.CombatOrder_Stop{Stop: &o.Clear{}}},
			}}
			if err := ValidateCombatOrders(orders); err != nil {
				t.Fatal(err)
			}
			got, err := CombatOrderResults(combatReceipt([]*r.CombatOrderResult{
				combatResult(0, "p0", true, "", "Goto"),
				combatResult(1, "p1", false, reason, ""),
				combatResult(2, "p2", true, "", ""),
			}), orders)
			if err != nil {
				t.Fatal(err)
			}
			want := []CombatOrderResult{
				{0, "p0", true, "", "Goto"},
				{1, "p1", false, reason, ""},
				{2, "p2", true, "", ""},
			}
			if len(got) != len(want) {
				t.Fatalf("got %d results, want %d", len(got), len(want))
			}
			for i := range want {
				if got[i] != want[i] {
					t.Errorf("result %d = %+v, want %+v", i, got[i], want[i])
				}
			}
		})
	}
}

func TestCombatOrdersIssue(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != ActionsApplyMethod {
			t.Fatal(arg.Tool)
		}
		protoTestRequest(t, arg, &o.ApplyRequest{Identity: pbIdentity(), Actions: []*o.Action{{Key: proto.String("fight"), Intent: &o.Action_CombatOrders{CombatOrders: combatTestOrders()}}}})
		return pbResult(&o.ApplyReply{Results: []*o.ActionResult{{Key: proto.String("fight"), Outcome: &o.ActionResult_Applied{Applied: combatReceipt(combatTestResults())}}}}), nil
	}}, time.Second)
	issue := func(command *o.CombatOrders) ([]CombatOrderResult, error) {
		return client.CombatOrders(context.Background(), pbIdentity(), "fight", command)
	}
	results, err := issue(combatTestOrders())
	if err != nil || len(results) != 8 || results[2].Refusal != CombatRefusalNotDrafted {
		t.Fatalf("issue: %v %+v", err, results)
	}
	rows := telemetrytest.Install(t)
	if _, err := issue(combatTestOrders()); err != nil {
		t.Fatal(err)
	}
	// combatlab metrics count these rows by verdict.
	var outcomes []string
	for _, row := range rows.Of("combat_order") {
		outcomes = append(outcomes, row.Payload["verdict"].(string))
	}
	if strings.Join(outcomes, ",") != "applied,applied,refused,applied,applied,applied,applied,refused" {
		t.Errorf("combat_order rows: %v", outcomes)
	}
	if _, err := issue(&o.CombatOrders{}); err == nil {
		t.Fatal("empty batch reached the wire")
	}
}

// TestValidateCombatRescueAndDoorModes covers the
// rescue order and the door forbid/allow modes.
func TestValidateCombatRescueAndDoorModes(t *testing.T) {
	rescue := func() *o.CombatOrders {
		return &o.CombatOrders{Orders: []*o.CombatOrder{
			{Pawn: combatPawn("p0"), Order: &o.CombatOrder_Rescue{Rescue: &o.CombatRescue{Downed: combatPawn("p1")}}},
			{Pawn: combatPawn("p0"), Order: &o.CombatOrder_Rescue{Rescue: &o.CombatRescue{Downed: combatPawn("p2"), Dest: combatCell(3, 4)}}},
			{Order: &o.CombatOrder_Door{Door: &o.CombatDoor{Cell: combatCell(1, 1), Mode: o.CombatDoorMode_COMBAT_DOOR_MODE_FORBID.Enum()}}},
			{Order: &o.CombatOrder_Door{Door: &o.CombatDoor{Cell: combatCell(1, 1), Mode: o.CombatDoorMode_COMBAT_DOOR_MODE_ALLOW.Enum()}}},
		}}
	}
	if err := ValidateCombatOrders(rescue()); err != nil {
		t.Fatal(err)
	}
	for name, edit := range map[string]func(*o.CombatOrders){
		"rescue nil":        func(v *o.CombatOrders) { v.Orders[0].Order = &o.CombatOrder_Rescue{} },
		"rescue no downed":  func(v *o.CombatOrders) { v.Orders[0].GetRescue().Downed = nil },
		"rescue self":       func(v *o.CombatOrders) { v.Orders[0].GetRescue().Downed = combatPawn("p0") },
		"rescue bad dest":   func(v *o.CombatOrders) { v.Orders[1].GetRescue().Dest = combatCell(-1, 4) },
		"rescue no pawn":    func(v *o.CombatOrders) { v.Orders[0].Pawn = nil },
		"door mode unknown": func(v *o.CombatOrders) { v.Orders[2].GetDoor().Mode = o.CombatDoorMode(5).Enum() },
	} {
		v := rescue()
		edit(v)
		if err := ValidateCombatOrders(v); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	results := []*r.CombatOrderResult{
		combatResult(0, "p0", false, CombatRefusalCannotRescue, ""),
		combatResult(1, "p0", false, CombatRefusalNoBed, ""),
		combatResult(2, "", true, "", ""),
		combatResult(3, "", true, "", ""),
	}
	if _, err := CombatOrderResults(combatReceipt(results), rescue()); err != nil {
		t.Fatalf("rescue refusals: %v", err)
	}
}

// TestValidateCombatRepair covers the repair order.
func TestValidateCombatRepair(t *testing.T) {
	repair := func() *o.CombatOrders {
		return &o.CombatOrders{Orders: []*o.CombatOrder{
			{Pawn: combatPawn("p0"), Order: &o.CombatOrder_Repair{Repair: &o.CombatRepair{Cell: combatCell(3, 4)}}},
		}}
	}
	if err := ValidateCombatOrders(repair()); err != nil {
		t.Fatal(err)
	}
	for name, edit := range map[string]func(*o.CombatOrders){
		"repair nil":      func(v *o.CombatOrders) { v.Orders[0].Order = &o.CombatOrder_Repair{} },
		"repair no cell":  func(v *o.CombatOrders) { v.Orders[0].GetRepair().Cell = nil },
		"repair bad cell": func(v *o.CombatOrders) { v.Orders[0].GetRepair().Cell = combatCell(-1, 4) },
		"repair no pawn":  func(v *o.CombatOrders) { v.Orders[0].Pawn = nil },
	} {
		v := repair()
		edit(v)
		if err := ValidateCombatOrders(v); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	if _, err := CombatOrderResults(combatReceipt([]*r.CombatOrderResult{combatResult(0, "p0", false, CombatRefusalCannotRepair, "")}), repair()); err != nil {
		t.Fatalf("repair refusal: %v", err)
	}
	if _, err := CombatOrderResults(combatReceipt([]*r.CombatOrderResult{combatResult(0, "p0", true, "", "Repair")}), repair()); err != nil {
		t.Fatalf("repair applied: %v", err)
	}
}

// TestValidateCombatMortar covers man_mortar (a pawn) and
// mortar_fire (no pawn) orders.
func TestValidateCombatMortar(t *testing.T) {
	mortar := func() *o.CombatOrders {
		return &o.CombatOrders{Orders: []*o.CombatOrder{
			{Order: &o.CombatOrder_MortarFire{MortarFire: &o.CombatMortarFire{Mortar: combatCell(3, 4), Target: combatCell(3, 50), Shell: proto.String("Shell_EMP")}}},
			{Pawn: combatPawn("p0"), Order: &o.CombatOrder_ManMortar{ManMortar: combatCell(3, 4)}},
		}}
	}
	if err := ValidateCombatOrders(mortar()); err != nil {
		t.Fatal(err)
	}
	clear := mortar()
	clear.Orders[0].GetMortarFire().Target, clear.Orders[0].GetMortarFire().Shell = nil, nil
	if err := ValidateCombatOrders(clear); err != nil {
		t.Fatalf("clear (#1235): %v", err)
	}
	for name, edit := range map[string]func(*o.CombatOrders){
		"nil fire":       func(v *o.CombatOrders) { v.Orders[0].Order = &o.CombatOrder_MortarFire{} },
		"clear w/ shell": func(v *o.CombatOrders) { v.Orders[0].GetMortarFire().Target = nil },
		"no mortar":      func(v *o.CombatOrders) { v.Orders[0].GetMortarFire().Mortar = nil },
		"bad target":     func(v *o.CombatOrders) { v.Orders[0].GetMortarFire().Target = combatCell(-1, 4) },
		"bad cell":       func(v *o.CombatOrders) { v.Orders[0].GetMortarFire().Mortar = combatCell(-1, 4) },
		"fire with pawn": func(v *o.CombatOrders) { v.Orders[0].Pawn = combatPawn("p0") },
		"man no pawn":    func(v *o.CombatOrders) { v.Orders[1].Pawn = nil },
		"man bad cell":   func(v *o.CombatOrders) { v.Orders[1].Order = &o.CombatOrder_ManMortar{ManMortar: combatCell(-1, 4)} },
	} {
		v := mortar()
		edit(v)
		if err := ValidateCombatOrders(v); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	results := []*r.CombatOrderResult{combatResult(0, "", false, CombatRefusalNoShell, ""), combatResult(1, "p0", false, CombatRefusalNotAMortar, "")}
	if _, err := CombatOrderResults(combatReceipt(results), mortar()); err != nil {
		t.Fatalf("mortar refusals: %v", err)
	}
}

// TestValidateCombatAnimalOrders covers release and animal_area orders.
func TestValidateCombatAnimalOrders(t *testing.T) {
	animal := func() *o.CombatOrders {
		return &o.CombatOrders{Orders: []*o.CombatOrder{
			{Pawn: combatPawn("dog"), Order: &o.CombatOrder_Release{Release: combatPawn("h0")}},
			{Pawn: combatPawn("dog"), Order: &o.CombatOrder_AnimalArea{AnimalArea: &o.CombatAnimalArea{Area: &o.CombatAnimalArea_Cell{Cell: combatCell(4, 5)}}}},
			{Pawn: combatPawn("dog"), Order: &o.CombatOrder_AnimalArea{AnimalArea: &o.CombatAnimalArea{Area: &o.CombatAnimalArea_Clear{Clear: &o.Clear{}}}}},
		}}
	}
	if err := ValidateCombatOrders(animal()); err != nil {
		t.Fatal(err)
	}
	for name, edit := range map[string]func(*o.CombatOrders){
		"release nil":     func(v *o.CombatOrders) { v.Orders[0].Order = &o.CombatOrder_Release{} },
		"release self":    func(v *o.CombatOrders) { v.Orders[0].Order = &o.CombatOrder_Release{Release: combatPawn("dog")} },
		"release no pawn": func(v *o.CombatOrders) { v.Orders[0].Pawn = nil },
		"area nil":        func(v *o.CombatOrders) { v.Orders[1].Order = &o.CombatOrder_AnimalArea{} },
		"area empty":      func(v *o.CombatOrders) { v.Orders[1].GetAnimalArea().Area = nil },
		"area bad cell": func(v *o.CombatOrders) {
			v.Orders[1].GetAnimalArea().Area = &o.CombatAnimalArea_Cell{Cell: combatCell(-1, 5)}
		},
		"clear nil": func(v *o.CombatOrders) { v.Orders[2].GetAnimalArea().Area = &o.CombatAnimalArea_Clear{} },
	} {
		v := animal()
		edit(v)
		if err := ValidateCombatOrders(v); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	receipt := combatReceipt([]*r.CombatOrderResult{
		combatResult(0, "dog", false, CombatRefusalUntrained, ""),
		combatResult(1, "dog", false, CombatRefusalNotOurs, ""),
		combatResult(2, "dog", false, CombatRefusalNotOurs, ""),
	})
	if _, err := CombatOrderResults(receipt, animal()); err != nil {
		t.Fatalf("animal refusals: %v", err)
	}
}
