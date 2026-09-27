package bridge

import (
	"bytes"
	"context"
	"log/slog"
	"regexp"
	"strings"
	"testing"
	"time"

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
		{Order: &o.CombatOrder_Door{Door: &o.CombatDoor{Cell: combatCell(1, 1), Mode: o.CombatDoorMode_COMBAT_DOOR_MODE_HOLD_OPEN.Enum()}}},
	}}
}

func TestValidateCombatOrders(t *testing.T) {
	if err := ValidateCombatOrders(combatTestOrders()); err != nil {
		t.Fatal(err)
	}
	for name, edit := range map[string]func(*o.CombatOrders){
		"empty":           func(v *o.CombatOrders) { v.Orders = nil },
		"too many":        func(v *o.CombatOrders) { v.Orders = make([]*o.CombatOrder, MaxCombatOrders+1) },
		"nil order":       func(v *o.CombatOrders) { v.Orders[0] = nil },
		"no order":        func(v *o.CombatOrders) { v.Orders[0].Order = nil },
		"no pawn":         func(v *o.CombatOrders) { v.Orders[1].Pawn = nil },
		"bad pawn id":     func(v *o.CombatOrders) { v.Orders[1].Pawn.EntityId = proto.String("") },
		"bad token":       func(v *o.CombatOrders) { v.Orders[1].Pawn.ExpectedSnapshotToken = proto.String("") },
		"negative cell":   func(v *o.CombatOrders) { v.Orders[1].Order = &o.CombatOrder_Move{Move: combatCell(-1, 2)} },
		"missing cell":    func(v *o.CombatOrders) { v.Orders[0].Order = &o.CombatOrder_AttackGround{} },
		"self attack":     func(v *o.CombatOrders) { v.Orders[2].Order = &o.CombatOrder_Attack{Attack: combatPawn("p2")} },
		"fire mode unset": func(v *o.CombatOrders) { v.Orders[3].Order = &o.CombatOrder_FireMode{} },
		"door with pawn":  func(v *o.CombatOrders) { v.Orders[6].Pawn = combatPawn("p0") },
		"door no mode":    func(v *o.CombatOrders) { v.Orders[6].GetDoor().Mode = nil },
		"door no cell":    func(v *o.CombatOrders) { v.Orders[6].GetDoor().Cell = nil },
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
		combatResult(2, "p2", false, CombatRefusalDraftOwnership, ""),
		combatResult(3, "p3", true, "", ""),
		combatResult(4, "p3", true, "", "Wait_Combat"),
		combatResult(5, "p4", true, "", ""),
		combatResult(6, "", false, CombatRefusalNotADoor, ""),
	}
}

func combatReceipt(results []*r.CombatOrderResult, applied bool) *r.Receipt {
	v := buildingAdmission()
	evidence := &r.EffectEvidence{Effect: &r.EffectEvidence_CombatOrders{CombatOrders: &r.CombatOrdersEffect{Results: results}}}
	if applied {
		v.Outcome = &r.Receipt_Applied{Applied: &r.Applied{Observed: evidence}}
	} else {
		v.Outcome = &r.Receipt_NoChange{NoChange: &r.NoChange{Observed: evidence, Detail: proto.String("Every combat order was refused.")}}
	}
	return v
}

func TestCombatOrderResultsDecode(t *testing.T) {
	got, err := CombatOrderResults(combatReceipt(combatTestResults(), true), combatTestOrders())
	if err != nil {
		t.Fatal(err)
	}
	want := []CombatOrderResult{
		{0, "p0", true, "", "AttackStatic"}, {1, "p1", true, "", "Goto"}, {2, "p2", false, "draft_ownership", ""},
		{3, "p3", true, "", ""}, {4, "p3", true, "", "Wait_Combat"}, {5, "p4", true, "", ""}, {6, "", false, "not_a_door", ""},
	}
	for i := range want {
		if got[i] != want[i] {
			t.Errorf("result %d = %+v, want %+v", i, got[i], want[i])
		}
	}
	allRefused := []*r.CombatOrderResult{combatResult(0, "p0", false, CombatRefusalStaleSnapshot, "")}
	one := &o.CombatOrders{Orders: combatTestOrders().Orders[:1]}
	if got, err := CombatOrderResults(combatReceipt(allRefused, false), one); err != nil || got[0].Refusal != "stale_snapshot" {
		t.Errorf("no-change decode: %v %+v", err, got)
	}
	uncertain := buildingAdmission()
	uncertain.Outcome = &r.Receipt_Uncertain{Uncertain: &r.Uncertain{Detail: proto.String("x")}}
	if got, err := CombatOrderResults(uncertain, one); err != nil || got != nil {
		t.Errorf("uncertain decode: %v %+v", err, got)
	}
	for _, tc := range []struct {
		name    string
		edit    func([]*r.CombatOrderResult) []*r.CombatOrderResult
		applied bool
		want    string
	}{
		{"short", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { return v[:6] }, true, "6 results for 7 orders"},
		{"index", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { v[1].Index = proto.Uint32(3); return v }, true, "index"},
		{"pawn", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { v[1].PawnId = proto.String("p9"); return v }, true, "names pawn"},
		{"door pawn", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { v[6].PawnId = proto.String("p0"); return v }, true, "names pawn"},
		{"no reason", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { v[2].Refusal = nil; return v }, true, "known reason"},
		{"unknown reason", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { v[2].Refusal = proto.String("nope"); return v }, true, "known reason"},
		{"applied refusal", func(v []*r.CombatOrderResult) []*r.CombatOrderResult {
			v[0].Refusal = proto.String("cannot_hit")
			return v
		}, true, "applied with a refusal"},
		{"refused job", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { v[2].JobDef = proto.String("Goto"); return v }, true, "refused with a job"},
		{"outcome", func(v []*r.CombatOrderResult) []*r.CombatOrderResult { return v }, false, "disagrees"},
	} {
		_, err := CombatOrderResults(combatReceipt(tc.edit(combatTestResults()), tc.applied), combatTestOrders())
		if err == nil || !strings.Contains(err.Error(), tc.want) {
			t.Errorf("%s: err %v, want %q", tc.name, err, tc.want)
		}
	}
}

func TestCombatOrdersIssue(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != "rimgovernor/operations_execute" {
			t.Fatal(arg.Tool)
		}
		draftTestRequest(t, arg, &o.ExecuteRequest{Precondition: buildingPre(), Operation: combatOrdersOperation(combatTestOrders())})
		return pbResult(&o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: combatReceipt(combatTestResults(), true)}}), nil
	}}, time.Second)
	control, err := NewCombatOrdersControl(client)
	if err != nil {
		t.Fatal(err)
	}
	results, _, _, err := control.Issue(context.Background(), buildingPre(), combatTestOrders())
	if err != nil || len(results) != 7 || results[2].Refusal != CombatRefusalDraftOwnership {
		t.Fatalf("issue: %v %+v", err, results)
	}
	var logged bytes.Buffer
	previous := slog.Default()
	slog.SetDefault(slog.New(slog.NewTextHandler(&logged, nil)))
	defer slog.SetDefault(previous)
	if _, _, _, err := control.Issue(context.Background(), buildingPre(), combatTestOrders()); err != nil {
		t.Fatal(err)
	}
	// combatlab metrics (#855) count these lines by this pattern.
	line := regexp.MustCompile(`\bcombat_order\b.*\boutcome=(\w+)`)
	var outcomes []string
	for _, l := range strings.Split(strings.TrimSpace(logged.String()), "\n") {
		if g := line.FindStringSubmatch(l); g != nil {
			outcomes = append(outcomes, g[1])
		}
	}
	if strings.Join(outcomes, ",") != "applied,applied,refused,applied,applied,applied,refused" {
		t.Errorf("combat_order lines: %v", outcomes)
	}
	if _, _, _, err := control.Issue(context.Background(), buildingPre(), &o.CombatOrders{}); err == nil {
		t.Fatal("empty batch reached the wire")
	}
}

// TestValidateCombatRescueAndDoorModes covers the #867 additions: the
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
	if _, err := CombatOrderResults(combatReceipt(results, true), rescue()); err != nil {
		t.Fatalf("rescue refusals: %v", err)
	}
}

// TestValidateCombatRepair covers the #900 repair order.
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
	if _, err := CombatOrderResults(combatReceipt([]*r.CombatOrderResult{combatResult(0, "p0", false, CombatRefusalCannotRepair, "")}, false), repair()); err != nil {
		t.Fatalf("repair refusal: %v", err)
	}
	if _, err := CombatOrderResults(combatReceipt([]*r.CombatOrderResult{combatResult(0, "p0", true, "", "Repair")}, true), repair()); err != nil {
		t.Fatalf("repair applied: %v", err)
	}
}
