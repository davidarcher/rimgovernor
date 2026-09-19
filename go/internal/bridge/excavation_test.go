package bridge

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func excavationTestTarget() ExcavationTarget {
	v, _ := domain.NewExcavation(domain.Cell{X: 1, Z: 2}, "Granite")
	return ExcavationTarget{v, "excavate-cas"}
}
func excavationTestEffect() *r.EffectEvidence {
	return &r.EffectEvidence{Effect: &r.EffectEvidence_Excavation{Excavation: &r.ExcavationEffect{Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}, MineableDefName: proto.String("Granite"), Designated: proto.Bool(true), AdoptedExistingDesignation: proto.Bool(false), Cleared: proto.Bool(false), Cancelled: proto.Bool(false)}}}
}
func TestExcavationFixedWriteAndExactAdmission(t *testing.T) {
	receipt := draftTestReceipt()
	receipt.Outcome = &r.Receipt_Applied{Applied: &r.Applied{Observed: excavationTestEffect()}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/operations_execute" {
			t.Fatal(arg.Tool)
		}
		draftTestRequest(t, arg, &op.ExecuteRequest{Precondition: buildingPre(), Operation: excavationOperation(excavationTestTarget(), false)})
		return pbResult(&op.ExecuteReply{Outcome: &op.ExecuteReply_Receipt{Receipt: receipt}}), nil
	}}, time.Second)
	writer, _ := NewExcavationControl(client)
	if _, _, err := writer.Excavate(context.Background(), buildingPre(), excavationTestTarget()); err != nil {
		t.Fatal(err)
	}
	want := ExcavationAttempt{pbIdentity(), buildingPre().Attempt, 1, excavationTestTarget().Excavation}
	for _, edit := range []func(*r.Receipt){
		func(v *r.Receipt) { v.Attempt.AttemptId = proto.Uint64(2) },
		func(v *r.Receipt) { v.GetApplied().Observed.GetExcavation().MineableDefName = proto.String("Marble") },
		func(v *r.Receipt) { v.GetApplied().Observed.GetExcavation().Cell.Z = proto.Int32(3) },
		func(v *r.Receipt) { v.GetApplied().Observed.GetExcavation().Designated = proto.Bool(false) },
		func(v *r.Receipt) { v.GetApplied().Observed.GetExcavation().Cleared = proto.Bool(true) },
		func(v *r.Receipt) { v.GetApplied().Observed.GetExcavation().Cancelled = nil },
	} {
		v := proto.Clone(receipt).(*r.Receipt)
		edit(v)
		if excavationReceipt(v, want) == nil {
			t.Fatal("foreign, undesignated or inconsistent excavation admitted", v)
		}
	}
	// An already cleared cell is adopted as done: applied without a
	// designation but with the cleared evidence.
	adopted := proto.Clone(receipt).(*r.Receipt)
	adopted.GetApplied().Observed.GetExcavation().Designated = proto.Bool(false)
	adopted.GetApplied().Observed.GetExcavation().Cleared = proto.Bool(true)
	if err := excavationReceipt(adopted, want); err != nil {
		t.Fatal(err)
	}
}

func TestValidateExcavationEffectRejectsContradictoryStates(t *testing.T) {
	excavation := excavationTestTarget().Excavation
	if err := ValidateExcavationEffect(excavationTestEffect(), excavation); err != nil {
		t.Fatal(err)
	}
	for _, edit := range []func(*r.ExcavationEffect){
		func(d *r.ExcavationEffect) { d.Cleared, d.Designated = proto.Bool(true), proto.Bool(true) },
		func(d *r.ExcavationEffect) {
			d.Cleared, d.Designated, d.Cancelled = proto.Bool(true), proto.Bool(false), proto.Bool(true)
		},
		func(d *r.ExcavationEffect) { d.Designated, d.Cancelled = proto.Bool(true), proto.Bool(true) },
		func(d *r.ExcavationEffect) { d.AdoptedExistingDesignation = nil },
	} {
		v := excavationTestEffect()
		edit(v.GetExcavation())
		if ValidateExcavationEffect(v, excavation) == nil {
			t.Fatal("contradictory excavation effect accepted", v)
		}
	}
	cleared := excavationTestEffect()
	cleared.GetExcavation().Designated, cleared.GetExcavation().Cleared = proto.Bool(false), proto.Bool(true)
	if err := ValidateExcavationEffect(cleared, excavation); err != nil {
		t.Fatal(err)
	}
}

func excavationSiteContext() *c.ObservationContext {
	return &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(7), NativeGeneration: proto.Uint64(1)}
}
func excavationSiteRow(x, z int32, fogged bool) *o.ExcavationCell {
	row := &o.ExcavationCell{Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Fogged: proto.Bool(fogged), MineDesignated: proto.Bool(false), Eligible: proto.Bool(false)}
	if fogged {
		row.Blocker = proto.String("Unknown excavation geometry")
		return row
	}
	row.MineableDefName, row.HitPoints, row.RoofDefName, row.HoldsRoof, row.Walkable, row.Eligible = proto.String("Granite"), proto.Int32(900), proto.String("RoofRockThick"), proto.Bool(true), proto.Bool(false), proto.Bool(true)
	row.Snapshot = &o.SnapshotRef{Token: proto.String("excavate-tok")}
	return row
}
func excavationSiteReply(rows ...*o.ExcavationCell) *o.ExcavationSiteReply {
	return &o.ExcavationSiteReply{Outcome: &o.ExcavationSiteReply_Observed{Observed: &o.ExcavationSiteSnapshot{
		Context: excavationSiteContext(), Cells: rows,
		SupportAfterRemoval: o.ExcavationSupport_EXCAVATION_SUPPORT_SUPPORTED, RoofCellsChecked: proto.Uint32(12),
		CollapsePending: proto.Bool(false), WorkerAvailable: proto.Bool(true), WorkerIds: []string{"Human1"}, AccessReachable: proto.Bool(true),
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
	}}}
}
func readExcavationSite(t *testing.T, reply *o.ExcavationSiteReply, cells []domain.Cell) (ExcavationSite, error) {
	t.Helper()
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/observations_read_excavation_site" {
			t.Fatal(arg.Tool)
		}
		return pbResult(reply), nil
	}}, time.Second)
	site, _, err := client.ReadExcavationSite(context.Background(), pbIdentity(), cells, domain.Cell{X: 0, Z: 2})
	return site, err
}

func TestReadExcavationSiteDecodesRowsInRequestOrderAndKeepsFogUnknown(t *testing.T) {
	cells := []domain.Cell{{X: 1, Z: 2}, {X: 2, Z: 2}}
	reply := excavationSiteReply(excavationSiteRow(1, 2, false), excavationSiteRow(2, 2, true))
	reply.GetObserved().SupportAfterRemoval = o.ExcavationSupport_EXCAVATION_SUPPORT_UNKNOWN
	site, err := readExcavationSite(t, reply, cells)
	if err != nil {
		t.Fatal(err)
	}
	if len(site.Cells) != 2 || site.Cells[0].Cell != cells[0] || site.Cells[1].Cell != cells[1] || site.Support != policy.ExcavationSupportUnknown {
		t.Fatal(site)
	}
	visible, fogged := site.Cells[0], site.Cells[1]
	if visible.Fogged || !visible.Eligible || visible.Definition != "Granite" || visible.Roof != "RoofRockThick" || visible.Token != "excavate-tok" || !visible.HoldsRoof {
		t.Fatal(visible)
	}
	if !fogged.Fogged || fogged.Eligible || fogged.Definition != "" || fogged.Token != "" {
		t.Fatal(fogged)
	}
	if !site.WorkerAvailable || !site.AccessReachable || len(site.Workers) != 1 || site.RoofCellsChecked != 12 {
		t.Fatal(site)
	}
}

func TestReadExcavationSiteRejectsInconsistentReplies(t *testing.T) {
	cells := []domain.Cell{{X: 1, Z: 2}}
	for name, edit := range map[string]func(*o.ExcavationSiteSnapshot){
		"row order":         func(v *o.ExcavationSiteSnapshot) { v.Cells[0].Cell.X = proto.Int32(5) },
		"row count":         func(v *o.ExcavationSiteSnapshot) { v.Cells = append(v.Cells, excavationSiteRow(2, 2, false)) },
		"fogged with facts": func(v *o.ExcavationSiteSnapshot) { v.Cells[0].Fogged = proto.Bool(true) },
		"eligible blocked": func(v *o.ExcavationSiteSnapshot) {
			v.Cells[0].Blocker = proto.String("Faction-owned excavation target is protected")
		},
		"support unspecified": func(v *o.ExcavationSiteSnapshot) {
			v.SupportAfterRemoval = o.ExcavationSupport_EXCAVATION_SUPPORT_UNSPECIFIED
		},
		"collapse pending": func(v *o.ExcavationSiteSnapshot) { v.CollapsePending = proto.Bool(true) },
		"workers without access": func(v *o.ExcavationSiteSnapshot) {
			v.AccessReachable = proto.Bool(false)
		},
		"worker flag without ids": func(v *o.ExcavationSiteSnapshot) { v.WorkerIds = nil },
		"incomplete":              func(v *o.ExcavationSiteSnapshot) { v.Completeness.Page.Complete = proto.Bool(false) },
		"foreign identity": func(v *o.ExcavationSiteSnapshot) {
			v.Context.Identity.ColonyId = proto.String("other")
		},
	} {
		t.Run(name, func(t *testing.T) {
			reply := excavationSiteReply(excavationSiteRow(1, 2, false))
			edit(reply.GetObserved())
			if _, err := readExcavationSite(t, reply, cells); err == nil {
				t.Fatal("inconsistent excavation site accepted")
			}
		})
	}
	if _, err := readExcavationSite(t, excavationSiteReply(), nil); err == nil {
		t.Fatal("empty request accepted")
	}
	if _, err := readExcavationSite(t, excavationSiteReply(), []domain.Cell{{X: 1, Z: 2}, {X: 1, Z: 2}}); err == nil {
		t.Fatal("duplicate cells accepted")
	}
}

func TestObserveExcavationRequiresClearedCompletionAndDesignatedPending(t *testing.T) {
	admitted := draftTestReceipt()
	admitted.Outcome = &r.Receipt_Applied{Applied: &r.Applied{Observed: excavationTestEffect()}}
	w := ExcavationAttempt{pbIdentity(), buildingPre().Attempt, 1, excavationTestTarget().Excavation}
	progress := func(effect *r.EffectEvidence, kind string) *r.ProgressReply {
		p := &r.Progress{Attempt: buildingPre().Attempt, Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(11), NativeGeneration: proto.Uint64(1)}, CompleteInspection: proto.Bool(kind != "pending")}
		switch kind {
		case "completed":
			p.Effect = &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: effect}}
		case "pending":
			p.Effect = &r.Progress_Pending{Pending: &r.PendingEffect{Evidence: effect}}
		case "unsuccessful":
			p.Effect = &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{Reason: r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED.Enum(), Evidence: effect}}
		}
		return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: p}}
	}
	cleared := excavationTestEffect()
	cleared.GetExcavation().Designated, cleared.GetExcavation().Cleared = proto.Bool(false), proto.Bool(true)
	cancelled := excavationTestEffect()
	cancelled.GetExcavation().Designated, cancelled.GetExcavation().Cancelled, cancelled.GetExcavation().Blocker = proto.Bool(false), proto.Bool(true), proto.String("Removal would leave unsupported roof")
	cases := []struct {
		name  string
		reply *r.ProgressReply
		ok    bool
	}{
		{"completed cleared", progress(cleared, "completed"), true},
		{"completed still rock", progress(excavationTestEffect(), "completed"), false},
		{"pending designated", progress(excavationTestEffect(), "pending"), true},
		{"pending cleared", progress(cleared, "pending"), false},
		{"unsuccessful cancelled", progress(cancelled, "unsuccessful"), true},
		{"unsuccessful designated", progress(excavationTestEffect(), "unsuccessful"), false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(tc.reply), nil
			}}, time.Second)
			_, _, err := client.ObserveExcavation(context.Background(), w, admitted)
			if (err == nil) != tc.ok {
				t.Fatal(err)
			}
		})
	}
}
