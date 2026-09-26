package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func tendGateRow(id string, mapID int32, patient bool, reachable []string) *o.PawnState {
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(mapID)}, Colonist: proto.Bool(true),
		Dead: proto.Bool(false), Downed: proto.Bool(patient), Drafted: proto.Bool(false), InBed: proto.Bool(patient),
		Job:       &o.JobEvidence{DefName: proto.String("Wait"), PlayerForced: proto.Bool(false), QueuedJobs: proto.Uint32(0)},
		Health:    &o.PawnHealth{NeedsTend: proto.Bool(patient), Bleeding: proto.Bool(patient), LifeThreatening: proto.Bool(false)},
		Equipment: &o.PawnEquipment{Armed: proto.Bool(false)},
		Biography: &o.PawnBiography{Skills: []*o.Skill{{Definition: &o.DefinitionRef{DefName: proto.String("Medicine")}, Level: proto.Int32(8), Disabled: proto.Bool(false)}}},
		Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true), MedicalCare: proto.String("Normal"),
			Work: []*o.WorkSetting{{DefName: proto.String("Doctor"), Priority: proto.Int32(3), Disabled: proto.Bool(false)}}},
		TendDoctor: &o.PawnTendDoctor{ControlEligible: proto.Bool(true), Spawned: proto.Bool(true), HasDrafter: proto.Bool(true),
			CapacitiesOk: proto.Bool(true), WorkTypeDisabled: proto.Bool(false), ReachablePawnIds: reachable,
			Issues: []*o.ReadIssue{missing("missing_capacity")}},
		Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	return row
}

func tendGatePlanner(t *testing.T, rows []*o.PawnState) RoutineTendResult {
	t.Helper()
	reviewer, _, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount, v.WorkerCount = proto.Uint32(2), proto.Uint32(2)
	snapshot := func(withTend bool) *o.ListPawnsReply {
		page := make([]*o.PawnState, 0, len(rows))
		for _, row := range rows {
			copied := proto.Clone(row).(*o.PawnState)
			if !withTend {
				// The shared routine census does not request the tend detail,
				// so only ReadTendPawns carries it.
				copied.TendDoctor = nil
			}
			page = append(page, copied)
		}
		return &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{
			Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: page,
			Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(uint64(len(page))), Returned: proto.Uint64(uint64(len(page))), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	}
	native.pawnReply = snapshot(false)
	source := &tendGateNative{&routineTendNative{native}, snapshot(true)}
	reviewer.native = source
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineTendPlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	return result
}

// tendGateNative reports both fixture colonists in the emergency census so the
// doctor and the patient are both candidates.
type tendGateNative struct {
	*routineTendNative
	tendReply *o.ListPawnsReply
}

func (n *tendGateNative) ReadTendPawns(ctx context.Context, _ *c.Identity, _ []string) (*o.ListPawnsReply, bridge.Result, error) {
	return n.tendReply, bridge.Result{}, ctx.Err()
}

func (n *tendGateNative) ReadEmergency(ctx context.Context, identity *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, receipt, err := n.routineNative.ReadEmergency(ctx, identity)
	v.Facts.Colonists = []policy.EmergencyPawn{
		{ID: "doctor", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
		{ID: "patient", Dead: domain.Known(false), Downed: domain.Known(true), Bleeding: domain.Known(true), NeedsTend: domain.Known(true)},
	}
	return v, receipt, err
}

// The tend detail reaches the planner end to end (#657): a doctor whose native
// gates pass and who can reach the patient is ordered, and the same doctor
// walled off from that patient yields no pair at all rather than an order the
// native gate refuses, so the episode stops burning its eight attempts.
func TestRoutineTendRequiresTheNativeDoctorGates(t *testing.T) {
	t.Parallel()
	mapID := int32(0)
	for _, test := range []struct {
		name      string
		reachable []string
		change    func(*o.PawnTendDoctor)
		admitted  bool
	}{
		{name: "reachable and eligible", reachable: []string{"patient"}, admitted: true},
		{name: "unreachable", reachable: nil},
		{name: "control ineligible", reachable: []string{"patient"}, change: func(d *o.PawnTendDoctor) { d.ControlEligible = proto.Bool(false) }},
		{name: "missing capacity", reachable: []string{"patient"}, change: func(d *o.PawnTendDoctor) {
			d.CapacitiesOk, d.MissingCapacity = proto.Bool(false), proto.String("Manipulation")
		}},
		{name: "no tend detail", reachable: []string{"patient"}, change: func(d *o.PawnTendDoctor) { *d = o.PawnTendDoctor{} }},
	} {
		t.Run(test.name, func(t *testing.T) {
			t.Parallel()
			doctor := tendGateRow("doctor", mapID, false, test.reachable)
			if test.change != nil {
				test.change(doctor.TendDoctor)
			}
			result := tendGatePlanner(t, []*o.PawnState{doctor, tendGateRow("patient", mapID, true, []string{"doctor"})})
			if test.admitted {
				if result.Reason != BuildingMethodAdmitted || result.Plan == "" {
					t.Fatal(result)
				}
				return
			}
			if result.Reason != BuildingMethodUsed || result.Plan != "" || result.NativeWorkTicks != medicalWaitTicks {
				t.Fatal(result)
			}
		})
	}
}
