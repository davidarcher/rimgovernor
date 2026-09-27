package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// A drop-pod arrival whose pods are still closed at the frame's tick
// (#908) reaches every emergency census the frame decodes to, the combat
// read's, the routine frame's and the clock step's, as PodsOpen; an
// opened arrival and other events do not.
func TestPendingPodsReachEveryEmergencyCensus(t *testing.T) {
	t.Parallel()
	arrival := func(at int64, open int32) *mp.CombatEventRow {
		return &mp.CombatEventRow{At: &mp.Watermark{Tick: proto.Int64(at), Seq: proto.Uint32(1)}, Kind: mp.CombatLogKind_COMBAT_LOG_KIND_HOSTILE_ARRIVED.Enum(), RaidStrategy: proto.String(PodsStrategy), OpenTick: proto.Int32(open), LandingCells: []*c.Cell{combatCell(50, 60)}}
	}
	other := &mp.CombatEventRow{At: &mp.Watermark{Tick: proto.Int64(11), Seq: proto.Uint32(1)}, Kind: mp.CombatLogKind_COMBAT_LOG_KIND_HOSTILE_ARRIVED.Enum()}
	for name, tc := range map[string]struct {
		events []*mp.CombatEventRow
		want   domain.Tick
	}{
		"pending":  {[]*mp.CombatEventRow{arrival(10, 530), other}, 530},
		"open now": {[]*mp.CombatEventRow{arrival(2, 12)}, 12},
		"opened":   {[]*mp.CombatEventRow{arrival(1, 11), other}, 0},
		"none":     {[]*mp.CombatEventRow{other}, 0},
	} {
		t.Run(name, func(t *testing.T) {
			frame := bundleTestSnapshot()
			frame.CombatEvents = tc.events
			combat, err := DecodeCombat(combatFrame(frame))
			if err != nil {
				t.Fatal(err)
			}
			if got := combat.Emergency.Facts.PodsOpen; got != tc.want {
				t.Fatalf("combat PodsOpen = %d, want %d", got, tc.want)
			}
			var routine RoutineFrame
			frameReplies(frame, EmergencyObservation{}, nil, func(method string, _, reply proto.Message) {
				if method == routineFrameMethod {
					if routine, err = DecodeRoutineFrame(reply.(*o.BundleSnapshot)); err != nil {
						t.Fatal(err)
					}
				}
			})
			if got := routine.Emergency.Facts.PodsOpen; got != tc.want {
				t.Fatalf("routine PodsOpen = %d, want %d", got, tc.want)
			}
			step := bundleTestSnapshot()
			step.CombatEvents = podArrivals(tc.events)
			emergency, err := BundleEmergency(step)
			if err != nil {
				t.Fatal(err)
			}
			if got := emergency.Facts.PodsOpen; got != tc.want {
				t.Fatalf("step PodsOpen = %d, want %d", got, tc.want)
			}
		})
	}
}
