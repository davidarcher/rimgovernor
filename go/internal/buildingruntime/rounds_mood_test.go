package buildingruntime

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type moodRoundsNative struct {
	*roundsNative
	mode string
}

func (n *moodRoundsNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	r, receipt, err := n.roundsNative.ReadEmergency(ctx, id)
	for i := 0; i < int(n.reply.GetObserved().GetColonistCount()); i++ {
		r.Facts.Colonists = append(r.Facts.Colonists, policy.EmergencyPawn{ID: policy.PawnID(fmt.Sprintf("pawn-%d", i)), Dead: domain.Known(false), Downed: domain.Known(false)})
	}
	return r, receipt, err
}
func (n *moodRoundsNative) ReadRoundsPawns(ctx context.Context, id *c.Identity, ids []string) (*o.ListPawnsReply, bridge.Result, error) {
	s := &o.PawnSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}
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

// A mental break only ends as ticks pass, so it must not hold the step: the
// window is still evaluated while the break is observed or unverified. (The
// store proves it declares no emergency either.)
func TestRoundsMoodMentalBreakDoesNotHoldTheClock(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	base := schedulerRounds(t, s, f)
	n := &moodRoundsNative{roundsNative: base, mode: "mental"}
	s.config.Rounds.native = n
	for _, mode := range []string{"mental", "unknown"} {
		n.mode = mode
		out, err := s.Step(context.Background())
		if err != nil || f.writes != 1 {
			t.Fatal(mode, out, err, f.writes)
		}
		if mode == "mental" && (out.Rounds == nil || out.Rounds.Review.Mood == nil || !out.Rounds.Review.Mood.States[0].MentalRisk || !out.Rounds.Review.Mood.States[0].Active) {
			t.Fatal(mode, out.Rounds)
		}
	}
}

func (n *moodRoundsNative) ReadRoundsFrame(ctx context.Context, id *c.Identity) (bridge.RoundsFrame, error) {
	return fakeFrame(ctx, n, id)
}
