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
	catalog := royalTitleCatalog(sets.GetRoyalTitleDefs()...)
	permits := map[string]proto.Message{}
	for _, row := range sets.GetRoyalTitlePermitDefs() {
		permits[row.GetDefName()] = row
	}
	catalog.Defs[(&d.RoyalTitlePermitDef{}).ProtoReflect().Descriptor().FullName()] = permits
	return catalog
}

// TestThroneRequirementsOfTheRecordedTitles: Knight and Baron decode
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

// TestWithTitleDefsFillsTheLadder: the ladder is every title row by
// seniority with its favor, bedroom and throne requirements, the permits are
// every permit row, and the holdings of the read are untouched; NextThroneNeed
// then names the title worked toward with that title's requirements.
func TestWithTitleDefsFillsTheLadder(t *testing.T) {
	catalog := recordedRoyalTitles(t)
	read := policy.RoyaltyFacts{
		Holders: map[policy.PawnID][]policy.RoyalHolding{"Alice": {{FactionDef: "Empire", Title: "Knight", Favor: domain.Known(0)}}},
	}
	facts, err := catalog.WithTitleDefs(read)
	if err != nil {
		t.Fatal(err)
	}
	if len(read.Ladder) != 0 || read.Permits != nil {
		t.Fatal("the input read was written")
	}
	if len(facts.Ladder) != 3 || facts.Ladder[0].Title != "Yeoman" || facts.Ladder[1].Title != "Knight" || facts.Ladder[2].Title != "Baron" {
		t.Fatalf("ladder %+v", facts.Ladder)
	}
	for i, want := range []int{6, 8, 14} {
		if n, ok := facts.Ladder[i].FavorNeeded.Value(); !ok || n != want {
			t.Fatalf("favor needed of %s: %v %v", facts.Ladder[i].Title, n, ok)
		}
	}
	if n, ok := facts.Ladder[1].Seniority.Value(); !ok || n != 300 {
		t.Fatalf("seniority %v %v", n, ok)
	}
	knight := facts.Ladder[1]
	if n, ok := knight.BedroomMinArea.Value(); !ok || n != 24 {
		t.Fatalf("bedroom area %v %v", n, ok)
	}
	if n, ok := knight.BedroomMinImpressiveness.Value(); !ok || n != 40 {
		t.Fatalf("bedroom impressiveness %v %v", n, ok)
	}
	if floored, ok := knight.BedroomFloored.Value(); !ok || !floored {
		t.Fatal("bedroom floor")
	}
	wantThings := []policy.BedroomThing{{AnyOf: []policy.Resource{"DoubleBed", "RoyalBed", "DeathrestCasket"}, Count: 1}, {AnyOf: []policy.Resource{"EndTable"}, Count: 1}, {AnyOf: []policy.Resource{"Dresser"}, Count: 1}}
	if !reflect.DeepEqual(knight.BedroomThings, wantThings) {
		t.Fatalf("bedroom things %+v", knight.BedroomThings)
	}
	yeoman := facts.Ladder[0]
	if _, ok := yeoman.BedroomMinArea.Value(); ok || len(yeoman.BedroomThings) != 0 {
		t.Fatalf("a title with no bedroom requirement: %+v", yeoman)
	}
	if _, ok := yeoman.BedroomFloored.Value(); ok {
		t.Fatal("absent floor read as known")
	}
	drop, passive := facts.Permits["SilverDrop"], facts.Permits["TradeSettlement"]
	if drop.Worker != "RoyalTitlePermitWorker_DropResources" || passive.Worker != "RoyalTitlePermitWorker" {
		t.Fatalf("worker classes %q %q", drop.Worker, passive.Worker)
	}
	if acts, ok := drop.Acts.Value(); !ok || !acts {
		t.Fatalf("call permit %+v", drop)
	}
	if cost, ok := drop.FavorCost.Value(); !ok || cost != 6 {
		t.Fatalf("favor cost %v %v", cost, ok)
	}
	if title, ok := drop.MinTitle.Value(); !ok || title != "Knight" {
		t.Fatalf("min title %q %v", title, ok)
	}
	if days, ok := drop.CooldownDays.Value(); !ok || days != 45 {
		t.Fatalf("cooldown %v %v", days, ok)
	}
	if _, ok := passive.FavorCost.Value(); ok {
		t.Fatal("passive permit has a favor cost")
	}
	if _, ok := passive.MinTitle.Value(); ok {
		t.Fatal("permit with no minimum title read as known")
	}
	if acts, ok := passive.Acts.Value(); !ok || acts {
		t.Fatalf("passive permit %+v", passive)
	}
	need, ok := policy.NextThroneNeed(facts)
	if !ok || need.Title != "Baron" || need.MinArea != 60 || need.MinImpressiveness != 120 || len(need.FloorTags) != 1 || need.FloorTags[0] != "FineFloor" || need.Counts[0].Count != 4 {
		t.Fatalf("a Knight is owed the Baron's room: %+v %v", need, ok)
	}
	read.Holders["Alice"][0].Title = "Yeoman"
	facts, _ = catalog.WithTitleDefs(read)
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
	if _, err := royalTitleCatalog().WithTitleDefs(policy.RoyaltyFacts{}); !errors.Is(err, ErrRoyaltyDefs) {
		t.Errorf("a catalog with no title rows: %v", err)
	}
	if _, err := royalTitleCatalog(&d.RoyalTitleDef{DefName: "T", ThroneRoomRequirements: []*d.Opt_RoomRequirementAny{req(nil)}}).WithTitleDefs(policy.RoyaltyFacts{}); !errors.Is(err, ErrThroneRequirements) {
		t.Errorf("a title with an unreadable requirement: %v", err)
	}
	if _, err := (*DefinitionCatalog)(nil).WithTitleDefs(policy.RoyaltyFacts{}); !errors.Is(err, ErrRoyaltyDefs) {
		t.Errorf("no catalog: %v", err)
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

// TestForbiddenDefsFollowBuildingTagsAndAltars: the defs a title forbids are
// those whose building tags meet the requirement's tags, plus altars only
// when altars are forbidden.
func TestForbiddenDefsFollowBuildingTagsAndAltars(t *testing.T) {
	building := func(tags ...string) *d.ThingDef {
		return &d.ThingDef{Building: &d.BuildingProperties{BuildingTags: tags}}
	}
	catalog := &DefinitionCatalog{ThingDefs: map[string]*d.ThingDef{
		"Bed":    building("Bed"),
		"Bench":  building("Production", "Other"),
		"Lamp":   building(),
		"Shrine": {IsAltar: true},
		"Wall":   {},
	}}
	req := policy.ThroneRequirements{ForbiddenBuildingTags: []string{"Bed", "Production"}}
	if got := catalog.forbiddenDefs(req); !reflect.DeepEqual(got, []string{"Bed", "Bench"}) {
		t.Errorf("tags only: %v", got)
	}
	req.ForbidAltars = true
	if got := catalog.forbiddenDefs(req); !reflect.DeepEqual(got, []string{"Bed", "Bench", "Shrine"}) {
		t.Errorf("with altars: %v", got)
	}
	if got := catalog.forbiddenDefs(policy.ThroneRequirements{}); got != nil {
		t.Errorf("nothing forbidden: %v", got)
	}
}
