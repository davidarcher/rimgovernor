package observation

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func thought(def string, total float64) *o.Thought {
	return &o.Thought{DefName: proto.String(def), Label: proto.String(def), Count: proto.Uint32(1), MoodOffsetEach: proto.Float64(total), MoodOffsetTotal: proto.Float64(total)}
}

func TestMoodThoughtsKeepNegativePressurePerDef(t *testing.T) {
	row := &o.PawnState{Social: &o.PawnSocial{
		Memories:    []*o.Thought{thought("SleptOutside", -4), thought("Insulted", -5), thought("Insulted", -5), thought("AteFineMeal", 5)},
		Situational: []*o.Thought{thought("NeedJoy", -20), thought("Comfortable", 4)},
	}}
	rows, known := MoodThoughts(row).Value()
	if !known {
		t.Fatal("readable social block left thoughts unknown")
	}
	want := []policy.MoodThought{{Def: "NeedJoy", Offset: -20}, {Def: "Insulted", Offset: -10}, {Def: "SleptOutside", Offset: -4}}
	if !reflect.DeepEqual(rows, want) {
		t.Fatalf("thoughts = %+v, want %+v", rows, want)
	}
	if _, known = MoodThoughts(&o.PawnState{}).Value(); known {
		t.Fatal("missing social block read as no pressure")
	}
	skipped := &o.PawnState{Issues: []*o.ReadIssue{{Field: proto.String("social")}}, Social: &o.PawnSocial{}}
	if _, known = MoodThoughts(skipped).Value(); known {
		t.Fatal("skipped social block read as no pressure")
	}
	partial := &o.PawnState{Social: &o.PawnSocial{Memories: []*o.Thought{thought("SleptOutside", -4)}, Issues: []*o.ReadIssue{{Field: proto.String("situational")}}}}
	if _, known = MoodThoughts(partial).Value(); known {
		t.Fatal("unreadable situational cache read as known pressure")
	}
	if rows, known = MoodThoughts(&o.PawnState{Social: &o.PawnSocial{}}).Value(); !known || len(rows) != 0 {
		t.Fatal("empty social block is known zero pressure", rows, known)
	}
}
