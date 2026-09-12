package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type moodRoutineNative struct {
	*routineNative
	mode string
}

func (n *moodRoutineNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	r, receipt, err := n.routineNative.ReadEmergency(ctx, id)
	for i := 0; i < int(n.reply.GetObserved().GetColonistCount()); i++ {
		r.Facts.Colonists = append(r.Facts.Colonists, policy.EmergencyPawn{ID: policy.PawnID(fmt.Sprintf("pawn-%d", i)), Dead: domain.Known(false), Downed: domain.Known(false)})
	}
	return r, receipt, err
}
func (n *moodRoutineNative) ReadRoutinePawns(ctx context.Context, id *c.Identity, ids []string) (*o.ListPawnsReply, bridge.Result, error) {
	s := &o.PawnSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(uint64(len(ids))), Returned: proto.Uint64(uint64(len(ids))), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
	for i, key := range ids {
		p := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(key), DefName: proto.String("Human"), MapId: proto.Int32(id.GetMapId()), Position: &c.Cell{X: proto.Int32(0), Z: proto.Int32(0)}}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Needs: &o.PawnNeeds{Mood: proto.Float64(.9), BreakThresholdMinor: proto.Float64(.3), Food: proto.Float64(.9), Rest: proto.Float64(.9), Joy: proto.Float64(.9)}, Issues: []*o.ReadIssue{{Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}}
		if i == 0 && n.mode != "clear" {
			p.Issues = nil
			if n.mode == "mental" {
				p.MentalState = proto.String("Wander_Sad")
			}
		}
		s.Pawns = append(s.Pawns, p)
	}
	return &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: s}}, bridge.Result{}, ctx.Err()
}

func TestRoutineMoodMentalHoldRequiresObservedClearance(t *testing.T) {
	s, f := schedulerFixture(t)
	base := schedulerRoutine(t, s, f)
	n := &moodRoutineNative{routineNative: base, mode: "mental"}
	s.config.Routine.native = n
	for _, mode := range []string{"mental", "unknown", "clear"} {
		n.mode = mode
		out, err := s.Step(context.Background())
		if mode != "clear" {
			if !errors.Is(err, executor.ErrHeld) || out.Routine == nil || f.writes != 0 || out.Routine.Review.Mood == nil || !out.Routine.Review.Mood.States[0].MentalRisk {
				t.Fatal(mode, out, err, f.writes)
			}
		} else if err != nil || f.writes != 1 || out.Routine.Review.Mood.States[0].Active {
			t.Fatal(mode, out, err, f.writes)
		}
	}
}
