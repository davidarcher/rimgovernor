package bridge

import (
	"context"
	"errors"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func actionsWriter(t *testing.T, reply *o.ApplyReply) *ActionsWriter {
	t.Helper()
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != ActionsApplyMethod {
			t.Fatal(arg.Tool)
		}
		return pbResult(reply), nil
	}}, time.Second)
	writer, err := NewActionsWriter(client)
	if err != nil {
		t.Fatal(err)
	}
	return writer
}

func probeAction(key string) *o.Action {
	return &o.Action{Key: proto.String(key), Intent: &o.Action_Trade{Trade: &o.TradeIntent{Target: mapTradeTarget("t"), NegotiatorId: proto.String("n"),
		Step: &o.TradeIntent_End{End: &o.EndTrade{Kind: o.EndTradeKind_END_TRADE_KIND_CANCEL.Enum()}}}}}
}

func TestActionsApplyChecksResultsPerAction(t *testing.T) {
	applied := &o.ActionResult{Key: proto.String("a"), Outcome: &o.ActionResult_Applied{Applied: &r.Receipt{AdmittedContext: pbContext(),
		Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: &r.EffectEvidence{}}}}}}
	refused := &o.ActionResult{Key: proto.String("b"), Outcome: &o.ActionResult_Refused{Refused: &o.Refusal{Code: c.FailureCode_FAILURE_CODE_NOT_FOUND.Enum(), Reason: proto.String("gone")}}}
	actions := []*o.Action{probeAction("a"), probeAction("b")}
	for _, tc := range []struct {
		name  string
		reply *o.ApplyReply
		bad   bool
		batch bool
	}{
		{"applied and refused", &o.ApplyReply{Results: []*o.ActionResult{applied, refused}}, false, false},
		{"short", &o.ApplyReply{Results: []*o.ActionResult{applied}}, true, false},
		{"reordered", &o.ApplyReply{Results: []*o.ActionResult{refused, applied}}, true, false},
		{"batch failure", &o.ApplyReply{BatchFailure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_STALE_IDENTITY.Enum(), Detail: proto.String("load changed")}}, true, true},
	} {
		_, _, err := actionsWriter(t, tc.reply).Apply(context.Background(), pbIdentity(), actions)
		if (err != nil) != tc.bad {
			t.Fatalf("%s: %v", tc.name, err)
		}
		var native *NativeFailure
		if tc.batch != errors.As(err, &native) {
			t.Fatalf("%s: batch failure not typed: %v", tc.name, err)
		}
	}
}

func TestActionsApplyRefusesDuplicateKeys(t *testing.T) {
	writer := actionsWriter(t, &o.ApplyReply{})
	if _, _, err := writer.Apply(context.Background(), pbIdentity(), []*o.Action{probeAction("a"), probeAction("a")}); err == nil {
		t.Fatal("duplicate keys accepted")
	}
}

func TestTradeAndBuildingRegisterAsIntentKinds(t *testing.T) {
	if !domain.TradeAction.IntentMode() || !domain.BuildingAction.IntentMode() {
		t.Fatal("trade and building register as intent kinds")
	}
	value, err := domain.NewTradeEnd("trader-1", "pawn-1", domain.TradeEndCancel, false)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewTradeAction("a1", value)
	if err != nil {
		t.Fatal(err)
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	trade := wire.GetTrade()
	if wire.GetKey() != "plan/1" || mapTraderID(trade.GetTarget()) != "trader-1" || trade.GetNegotiatorId() != "pawn-1" || trade.GetEnd().GetKind() != o.EndTradeKind_END_TRADE_KIND_CANCEL {
		t.Fatalf("%v", wire)
	}
}

// An accept carries the gear things it authorizes past native's export
// protection (#1831), sorted.
func TestTradeAcceptCarriesExportThings(t *testing.T) {
	value, err := domain.NewTradeAccept("trader-1", "pawn-1", "sig-1", nil, []string{"Apparel_B", "Apparel_A"}, false, false)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewTradeAction("a1", value)
	if err != nil {
		t.Fatal(err)
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	if got := wire.GetTrade().GetAccept().GetExportThingIds(); !reflect.DeepEqual(got, []string{"Apparel_A", "Apparel_B"}) {
		t.Fatal(got)
	}
	if _, err := domain.NewTradeAccept("trader-1", "pawn-1", "sig-1", nil, []string{"Apparel_A", "Apparel_A"}, false, false); err == nil {
		t.Fatal("duplicate export thing accepted")
	}
}

// Acquisition, mine acquisition and a stall withdraw build one
// acquisition-guarded Designate; only the withdraw sets withdraw (#1046).
func TestAcquisitionKindsBuildAcquisitionDesignate(t *testing.T) {
	value, err := domain.NewAcquisition("deer-1", "Corpse_Deer", domain.Cell{X: 3, Z: 4})
	if err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct {
		build       func(domain.ActionID, domain.Acquisition) (domain.Action, error)
		withdraw    bool
		designation o.ThingDesignation
	}{{domain.NewAcquisitionAction, false, o.ThingDesignation_THING_DESIGNATION_HUNT},
		{domain.NewMineAcquisitionAction, false, o.ThingDesignation_THING_DESIGNATION_MINE},
		{domain.NewAcquisitionWithdrawAction, true, o.ThingDesignation_THING_DESIGNATION_HUNT}} {
		action, err := tc.build("a1", value)
		if err != nil {
			t.Fatal(err)
		}
		if !action.Kind().IntentMode() {
			t.Fatal("not an intent kind", action.Kind())
		}
		wire, err := IntentAction("plan/1", action)
		if err != nil {
			t.Fatal(err)
		}
		d := wire.GetDesignate()
		if d.GetTarget().GetId() != "deer-1" || d.GetExpectedDef() != "Corpse_Deer" || d.GetCell().GetX() != 3 || d.GetCell().GetZ() != 4 ||
			d.GetWithdraw() != tc.withdraw || d.GetDesignation() != tc.designation || d.GetGuard() != o.DesignationGuard_DESIGNATION_GUARD_ACQUISITION {
			t.Fatalf("%s: %v", action.Kind(), wire)
		}
	}
}

// A surgery builds one medical ProductionBillIntent (#1162); a whole-body recipe leaves
// part_index absent.
func TestSurgeryBuildsMedicalBill(t *testing.T) {
	for _, tc := range []struct {
		part int
		ack  bool
	}{{4, false}, {domain.NoSurgeryPart, true}} {
		value, err := domain.NewSurgery("Human1", "InstallPegLeg", tc.part, tc.ack)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewSurgeryAction("a1", value)
		if err != nil {
			t.Fatal(err)
		}
		if !action.Kind().IntentMode() {
			t.Fatal("surgery is not an intent kind")
		}
		wire, err := IntentAction("plan/1", action)
		if err != nil {
			t.Fatal(err)
		}
		s := wire.GetProductionBill()
		if s.GetPatient().GetId() != "Human1" || s.BenchId != nil || s.GetRecipeDef() != "InstallPegLeg" || (s.PartIndex != nil) != (tc.part >= 0) || int(s.GetPartIndex()) != max(tc.part, 0) || s.GetAcknowledgeViolation() != tc.ack {
			t.Fatalf("%v", wire)
		}
	}
}

// An auto home area action builds one AutoHomeAreaIntent carrying the value
// (#1322).
func TestAutoHomeAreaBuildsIntent(t *testing.T) {
	for _, enabled := range []bool{false, true} {
		action, err := domain.NewAutoHomeAreaAction("a1", enabled)
		if err != nil {
			t.Fatal(err)
		}
		if !action.Kind().IntentMode() {
			t.Fatal("auto_home_area is not an intent kind")
		}
		wire, err := IntentAction("plan/1", action)
		if err != nil {
			t.Fatal(err)
		}
		if v := wire.GetAutoHomeArea(); v == nil || v.Enabled == nil || v.GetEnabled() != enabled {
			t.Fatalf("%v", wire)
		}
	}
}
