package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestRoutineDefenseRequiresConsistentCompletePawnDetails(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"armed", "unarmed", "unknown-equipment", "missing-pawn", "colony-count", "conflicting-downed", "stale-tick", "stale-native"} {
		t.Run(change, func(t *testing.T) {
			r, db, _, _, n := routineFixture(t)
			r.native = &routineMedicalNative{routineNative: n}
			n.reply.GetObserved().ColonistCount = proto.Uint32(1)
			n.reply.GetObserved().WorkerCount = proto.Uint32(1)
			row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(n.reply.GetObserved().Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Issues: []*o.ReadIssue{{Field: proto.String("pawn.snapshot"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}}}
			snapshot := &o.PawnSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
			n.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: snapshot}}
			want := domain.NeedUnknown
			switch change {
			case "armed":
				want = domain.NeedRecovered
			case "unarmed":
				row.Equipment.Armed = proto.Bool(false)
				want = domain.NeedDeficit
			case "unknown-equipment":
				row.Equipment = nil
			case "missing-pawn":
				snapshot.Pawns = nil
				snapshot.Completeness.Matched = proto.Uint64(0)
				snapshot.Completeness.Returned = proto.Uint64(0)
			case "colony-count":
				n.reply.GetObserved().ColonistCount = proto.Uint32(2)
			case "conflicting-downed":
				row.Downed = proto.Bool(true)
			case "stale-tick":
				snapshot.Context.Tick = proto.Int64(snapshot.Context.GetTick() + 1)
			case "stale-native":
				snapshot.Context.NativeGeneration = proto.Uint64(snapshot.Context.GetNativeGeneration() + 1)
			}
			out, err := r.Step(context.Background())
			if change == "stale-tick" || change == "stale-native" {
				if err == nil {
					t.Fatal("mixed observation committed")
				}
				stored, e := db.LoadRoutineReview(context.Background())
				if e != nil || stored.Revision != 0 {
					t.Fatal(stored, e)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			for _, assessment := range out.Needs.Assessments {
				if assessment.ID == policy.EnsureBasicDefense && assessment.Need != want {
					t.Fatal(change, assessment, want)
				}
			}
		})
	}
}
