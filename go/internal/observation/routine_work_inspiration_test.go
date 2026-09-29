package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// TestWorkPawnInspiration keeps unknown (field absent) distinct from none
// (known "") and carries a defName into the pawn profile (#1187).
func TestWorkPawnInspiration(t *testing.T) {
	for _, tc := range []struct {
		name  string
		field *string
		want  string
		known bool
	}{
		{"absent", nil, "", false},
		{"none", proto.String(""), "", true},
		{"inspired", proto.String("Inspired_Creativity"), "Inspired_Creativity", true},
	} {
		row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("Human1")}, Inspiration: tc.field}
		profile := policy.BuildProfile(WorkPawnRow(row))
		got, known := profile.Inspiration.Value()
		if known != tc.known || got != tc.want {
			t.Fatalf("%s: got %q known=%v, want %q known=%v", tc.name, got, known, tc.want, tc.known)
		}
	}
}
