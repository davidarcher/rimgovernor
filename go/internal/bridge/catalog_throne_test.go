package bridge

import (
	"errors"
	"os"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/encoding/prototext"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// royalTitleCatalog is a catalog holding the given RoyalTitleDef rows.
func royalTitleCatalog(rows ...*d.RoyalTitleDef) *DefinitionCatalog {
	byName := map[string]proto.Message{}
	for _, row := range rows {
		byName[row.GetDefName()] = row
	}
	return &DefinitionCatalog{Defs: map[protoreflect.FullName]map[string]proto.Message{(&d.RoyalTitleDef{}).ProtoReflect().Descriptor().FullName(): byName}}
}

// recordedRoyalTitles is the Yeoman, Knight and Baron rows transcribed from
// RoyalTitles_Empire.xml.
func recordedRoyalTitles(t *testing.T) *DefinitionCatalog {
	t.Helper()
	raw, err := os.ReadFile("testdata/royal_title_defs.textproto")
	if err != nil {
		t.Fatal(err)
	}
	var sets d.DefSets
	if err := prototext.Unmarshal(raw, &sets); err != nil {
		t.Fatal(err)
	}
	return royalTitleCatalog(sets.GetRoyalTitleDefs()...)
}

// TestThroneRequirementsOfTheRecordedTitles (#1861): Knight and Baron decode
// into the area, impressiveness, throne, floor, braziers, columns,
// instrument, glowing and forbidden-building requirements; Yeoman's empty
// list asks for no throne.
func TestThroneRequirementsOfTheRecordedTitles(t *testing.T) {
	catalog := recordedRoyalTitles(t)
	forbidden := []string{"Production", "Bed", "Biotech", "Anomaly"}
	braziers := []string{"Brazier", "DarklightBrazier"}
	want := map[string]policy.ThroneRequirements{
		"Yeoman": {},
		"Knight": {
			Things: []string{"Throne", "GrandThrone"}, Assigned: true, MinArea: 30, MinImpressiveness: 60,
			FloorTags: []string{"Floor", "FineFloor"}, FloorLabel: "RoomRequirementAllFloored",
			AnyOfCounts: []policy.ThingAnyOfCount{{Things: braziers, Count: 2}},
			Counts:      []policy.ThingCount{{Def: "Column", Count: 2}},
			AnyOf:       [][]string{{"Harp", "Harpsichord", "Piano"}},
			Glowing:     [][]string{braziers}, ForbiddenBuildingTags: forbidden, ForbidAltars: true,
		},
		"Baron": {
			Things: []string{"GrandThrone"}, Assigned: true, MinArea: 60, MinImpressiveness: 120,
			FloorTags: []string{"FineFloor"}, FloorLabel: "RoomRequirementAllFineFloored",
			AnyOfCounts: []policy.ThingAnyOfCount{{Things: braziers, Count: 2}},
			Counts:      []policy.ThingCount{{Def: "Column", Count: 4}, {Def: "Drape", Count: 2}},
			AnyOf:       [][]string{{"Harpsichord", "Piano"}},
			Glowing:     [][]string{braziers}, ForbiddenBuildingTags: forbidden, ForbidAltars: true,
		},
	}
	for title, req := range want {
		got, err := catalog.ThroneRequirements(title)
		if err != nil || !reflect.DeepEqual(got, req) {
			t.Errorf("%s: %+v, %v; want %+v", title, got, err, req)
		}
	}
}

// TestWithThroneRequirementsFillsTheLadder: every rung of a royalty read
// gets its mirror requirement and the read itself is untouched; NextThroneNeed
// then names the title worked toward with that title's requirements.
func TestWithThroneRequirementsFillsTheLadder(t *testing.T) {
	catalog := recordedRoyalTitles(t)
	read := policy.RoyaltyFacts{
		Ladder:  []policy.RoyalRung{{Title: "Yeoman", FavorNeeded: domain.Known(6)}, {Title: "Knight", FavorNeeded: domain.Known(10)}, {Title: "Baron", FavorNeeded: domain.Known(16)}},
		Holders: map[policy.PawnID][]policy.RoyalHolding{"Alice": {{FactionDef: "Empire", Title: "Knight", Favor: domain.Known(0)}}},
	}
	facts, err := catalog.WithThroneRequirements(read)
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := read.Ladder[1].Throne.Value(); ok {
		t.Fatal("the input ladder was written")
	}
	need, ok := policy.NextThroneNeed(facts)
	if !ok || need.Title != "Baron" || need.MinArea != 60 || need.MinImpressiveness != 120 || len(need.FloorTags) != 1 || need.FloorTags[0] != "FineFloor" || need.Counts[0].Count != 4 {
		t.Fatalf("a Knight is owed the Baron's room: %+v %v", need, ok)
	}
	read.Holders["Alice"][0].Title = "Yeoman"
	facts, _ = catalog.WithThroneRequirements(read)
	if need, ok = policy.NextThroneNeed(facts); !ok || need.Title != "Knight" || len(need.FloorTags) != 2 {
		t.Fatalf("a Yeoman is owed the Knight's room: %+v %v", need, ok)
	}
}

// TestThroneRequirementErrors: a missing row, a missing catalog, an unset or
// malformed requirement and a throne without an area are named errors.
func TestThroneRequirementErrors(t *testing.T) {
	req := func(m *d.RoomRequirementAny) *d.Opt_RoomRequirementAny { return &d.Opt_RoomRequirementAny{Value: m} }
	area := &d.RoomRequirementAny{Value: &d.RoomRequirementAny_RoomRequirement_Area{RoomRequirement_Area: &d.RoomRequirement_Area{Area: 30}}}
	throne := &d.RoomRequirementAny{Value: &d.RoomRequirementAny_RoomRequirement_HasAssignedThroneAnyOf{RoomRequirement_HasAssignedThroneAnyOf: &d.RoomRequirement_HasAssignedThroneAnyOf{Things: []string{"Throne"}}}}
	count := func(n int32) *d.RoomRequirementAny {
		return &d.RoomRequirementAny{Value: &d.RoomRequirementAny_RoomRequirement_ThingCount{RoomRequirement_ThingCount: &d.RoomRequirement_ThingCount{ThingDef: "Column", Count: n}}}
	}
	cases := []struct {
		name    string
		catalog *DefinitionCatalog
		want    string
	}{
		{"no catalog", nil, "no definition catalog"},
		{"no row", royalTitleCatalog(), "no RoyalTitleDef row"},
		{"unset requirement", royalTitleCatalog(&d.RoyalTitleDef{DefName: "T", ThroneRoomRequirements: []*d.Opt_RoomRequirementAny{req(nil)}}), "unset"},
		{"unread kind", royalTitleCatalog(&d.RoyalTitleDef{DefName: "T", ThroneRoomRequirements: []*d.Opt_RoomRequirementAny{req(&d.RoomRequirementAny{})}}), "unset"},
		{"zero count", royalTitleCatalog(&d.RoyalTitleDef{DefName: "T", ThroneRoomRequirements: []*d.Opt_RoomRequirementAny{req(throne), req(area), req(count(0))}}), "count is 0"},
		{"throne without area", royalTitleCatalog(&d.RoyalTitleDef{DefName: "T", ThroneRoomRequirements: []*d.Opt_RoomRequirementAny{req(throne)}}), "without an Area"},
		{"repeated area", royalTitleCatalog(&d.RoyalTitleDef{DefName: "T", ThroneRoomRequirements: []*d.Opt_RoomRequirementAny{req(area), req(area)}}), "repeated Area"},
		{"empty things", royalTitleCatalog(&d.RoyalTitleDef{DefName: "T", ThroneRoomRequirements: []*d.Opt_RoomRequirementAny{req(&d.RoomRequirementAny{Value: &d.RoomRequirementAny_RoomRequirement_ThingAnyOf{RoomRequirement_ThingAnyOf: &d.RoomRequirement_ThingAnyOf{}}})}}), "names no def"},
	}
	for _, c := range cases {
		_, err := c.catalog.ThroneRequirements("T")
		if !errors.Is(err, ErrThroneRequirements) || !strings.Contains(err.Error(), c.want) || !strings.Contains(err.Error(), "title T") {
			t.Errorf("%s: %v", c.name, err)
		}
	}
	if _, err := recordedRoyalTitles(t).WithThroneRequirements(policy.RoyaltyFacts{Ladder: []policy.RoyalRung{{Title: "Duke"}}}); !errors.Is(err, ErrThroneRequirements) {
		t.Errorf("a ladder title the mirror lacks: %v", err)
	}
}

// TestThroneForbiddenTagsMergeTheSet: buildingTags and the resolved
// buildingTagsSet read as one deduplicated list.
func TestThroneForbiddenTagsMergeTheSet(t *testing.T) {
	forbid := &d.RoomRequirementAny{Value: &d.RoomRequirementAny_RoomRequirement_ForbiddenBuildings{RoomRequirement_ForbiddenBuildings: &d.RoomRequirement_ForbiddenBuildings{BuildingTags: []string{"Bed"}, BuildingTagsSet: []string{"Bed", "Production"}}}}
	got, err := royalTitleCatalog(&d.RoyalTitleDef{DefName: "T", ThroneRoomRequirements: []*d.Opt_RoomRequirementAny{{Value: forbid}}}).ThroneRequirements("T")
	if err != nil || !reflect.DeepEqual(got.ForbiddenBuildingTags, []string{"Bed", "Production"}) {
		t.Fatalf("%+v %v", got, err)
	}
}
