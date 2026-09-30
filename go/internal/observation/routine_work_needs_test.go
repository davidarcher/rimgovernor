package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// TestWorkPawnNeeds lifts rest, joy and mood onto WorkPawn and leaves them
// unknown when the read omits the needs block or flags it (#1312).
func TestWorkPawnNeeds(t *testing.T) {
	id := &o.EntityRef{Id: proto.String("Human1")}
	w := WorkPawnRow(&o.PawnState{Pawn: id, Needs: &o.PawnNeeds{Rest: proto.Float64(0.2), Joy: proto.Float64(0.4), Mood: proto.Float64(0.6)}})
	for name, tc := range map[string]struct {
		fact domain.Fact[float64]
		want float64
	}{"rest": {w.Rest, 0.2}, "joy": {w.Joy, 0.4}, "mood": {w.Mood, 0.6}} {
		if v, ok := tc.fact.Value(); !ok || v != tc.want {
			t.Fatalf("%s: got %v known=%v, want %v", name, v, ok, tc.want)
		}
	}
	for _, row := range []*o.PawnState{{Pawn: id}, {Pawn: id, Needs: &o.PawnNeeds{Rest: proto.Float64(0.2)}, Issues: []*o.ReadIssue{{Field: proto.String("needs")}}}} {
		w := WorkPawnRow(row)
		for _, f := range []domain.Fact[float64]{w.Rest, w.Joy, w.Mood} {
			if _, ok := f.Value(); ok {
				t.Fatalf("needs known from %v", row)
			}
		}
	}
}
