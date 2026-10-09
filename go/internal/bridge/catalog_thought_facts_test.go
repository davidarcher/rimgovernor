package bridge

import (
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

func thoughtCatalog(rows ...*d.ThoughtDef) *DefinitionCatalog {
	full := (&d.ThoughtDef{}).ProtoReflect().Descriptor().FullName()
	byName := map[string]proto.Message{}
	for _, r := range rows {
		byName[r.DefName] = r
	}
	return &DefinitionCatalog{Defs: map[protoreflect.FullName]map[string]proto.Message{full: byName}}
}

func thoughtStage(v float32) *d.Opt_ThoughtStage {
	return &d.Opt_ThoughtStage{Value: &d.ThoughtStage{BaseMoodEffect: v}}
}

func TestThoughtFactsProjection(t *testing.T) {
	catalog := thoughtCatalog(
		&d.ThoughtDef{DefName: "SleptInBarracks", Stages: []*d.Opt_ThoughtStage{thoughtStage(-1), {}, thoughtStage(-4), thoughtStage(2)},
			NullifyingTraits: []string{"Greedy"}, NullifyingPrecepts: []string{"Barracks_Fine"},
			RequiredTraits: []string{"Nudist"}, MinExpectation: "Moderate"},
		&d.ThoughtDef{DefName: "ModdedWorry", Stages: []*d.Opt_ThoughtStage{thoughtStage(-2)}},
		&d.ThoughtDef{DefName: "Cheer", Stages: []*d.Opt_ThoughtStage{thoughtStage(3), thoughtStage(5)}},
	)
	got, ok := catalog.ThoughtFacts("SleptInBarracks")
	if !ok || got.WorstOffset != -4 {
		t.Fatalf("worst stage = %+v", got)
	}
	if !slices.Equal(got.NullifyingTraits, []string{"Greedy"}) || !slices.Equal(got.NullifyingPrecepts, []string{"Barracks_Fine"}) ||
		!slices.Equal(got.RequiredTraits, []string{"Nudist"}) || got.MinExpectation != "Moderate" {
		t.Fatalf("immunity data = %+v", got)
	}
	if !strings.Contains(got.Dependency, "room_role:Barracks") {
		t.Fatalf("dependency = %q", got.Dependency)
	}
	mod, _ := catalog.ThoughtFacts("ModdedWorry")
	if mod.Dependency != policy.ThoughtUnclassified || mod.Owner != "" {
		t.Fatalf("modded = %+v", mod)
	}
	if cheer, _ := catalog.ThoughtFacts("Cheer"); cheer.WorstOffset != 3 {
		t.Fatalf("positive worst = %v", cheer.WorstOffset)
	}
	if _, ok := catalog.ThoughtFacts("Nope"); ok {
		t.Fatal("absent def projected")
	}
	if len(catalog.AllThoughtFacts()) != 3 {
		t.Fatal("AllThoughtFacts size")
	}
}

// TestThoughtFactsFromRecordedCatalog projects real rows of the recorded
// game catalog.
func TestThoughtFactsFromRecordedCatalog(t *testing.T) {
	catalog := fullCatalog(t)
	got, ok := catalog.ThoughtFacts("SleptInBarracks")
	if !ok {
		t.Fatal("recorded catalog lacks SleptInBarracks")
	}
	if got.WorstOffset >= 0 || !strings.Contains(got.Dependency, "room_role:Barracks") {
		t.Fatalf("SleptInBarracks = %+v", got)
	}
	all := catalog.AllThoughtFacts()
	if len(all) == 0 {
		t.Fatal("no thought facts")
	}
	for name, f := range all {
		if f.Dependency == "" || f.Def != name {
			t.Fatalf("%s = %+v", name, f)
		}
	}
	if _, ok := all["AteAwfulMeal"]; ok {
		t.Error("AteAwfulMeal is not a ThoughtDef")
	}
}
