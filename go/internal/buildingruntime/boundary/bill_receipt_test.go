package boundary

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// evidenceWriter answers every action with an applied receipt carrying one
// canned piece of effect evidence.
type evidenceWriter struct{ evidence *r.EffectEvidence }

func (w evidenceWriter) Apply(_ context.Context, identity *c.Identity, actions []*o.Action) (*o.ApplyReply, bridge.Result, error) {
	reply := &o.ApplyReply{}
	for _, action := range actions {
		ctx := &c.ObservationContext{Identity: proto.Clone(identity).(*c.Identity), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}
		applied := &r.Receipt{AdmittedContext: ctx, Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: w.evidence}}}
		reply.Results = append(reply.Results, &o.ActionResult{Key: action.Key, Outcome: &o.ActionResult_Applied{Applied: applied}})
	}
	return reply, bridge.Result{}, nil
}

type fixedLease struct{}

func (fixedLease) Lease(domain.GenerationSnapshot) (string, error) { return "lease", nil }

func placementOf(t *testing.T, action domain.Action) executor.Placement {
	t.Helper()
	snapshot := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "plan", Revision: 1, Native: 1}
	return executor.Placement{Action: action, Snapshot: snapshot, Attempt: 1, Tick: 10}
}

// An applied bill placement's receipt carries the native bill id its evidence
// names (#2410): BillEffect for an ordinary bill, SurgeryEffect for a medical
// one; a removal or evidence without an id carries none.
func TestDispatchIntentsCarriesTheNativeBillID(t *testing.T) {
	t.Parallel()
	bill, err := domain.NewProductionBill("Bench_1", "Make_Pemmican", domain.StockTarget, 20)
	if err != nil {
		t.Fatal(err)
	}
	place, err := domain.NewProductionBillAction("place", bill)
	if err != nil {
		t.Fatal(err)
	}
	surgery, err := domain.NewSurgery("Human12", "RemoveAppendix", domain.NoSurgeryPart, false)
	if err != nil {
		t.Fatal(err)
	}
	operate, err := domain.NewSurgeryAction("operate", surgery)
	if err != nil {
		t.Fatal(err)
	}
	removal, err := domain.NewRemoveProductionBill("Bench_1", "Bill_Production_77")
	if err != nil {
		t.Fatal(err)
	}
	remove, err := domain.NewRemoveProductionBillAction("remove", removal)
	if err != nil {
		t.Fatal(err)
	}
	billEvidence := &r.EffectEvidence{Effect: &r.EffectEvidence_Bill{Bill: &r.BillEffect{Bill: &c.Ref{Id: proto.String("Bill_Production_77")}}}}
	surgeryEvidence := &r.EffectEvidence{Effect: &r.EffectEvidence_SurgeryBill{SurgeryBill: &r.SurgeryEffect{Bill: &c.Ref{Id: proto.String("Bill_Medical_9")}}}}
	for _, tc := range []struct {
		name     string
		action   domain.Action
		evidence *r.EffectEvidence
		want     string
	}{
		{"production bill", place, billEvidence, "Bill_Production_77"},
		{"surgery", operate, surgeryEvidence, "Bill_Medical_9"},
		{"no id in the evidence", place, &r.EffectEvidence{}, ""},
		{"removal", remove, billEvidence, ""},
	} {
		out, err := DispatchIntents(context.Background(), fixedLease{}, []executor.Placement{placementOf(t, tc.action)}, evidenceWriter{tc.evidence})
		if err != nil || len(out) != 1 || out[0].Kind != domain.ReceiptAccepted || out[0].Bill != tc.want {
			t.Fatal(tc.name, out, err)
		}
	}
}
