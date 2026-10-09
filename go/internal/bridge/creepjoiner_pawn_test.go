package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

func joinerRow(triggered bool, traits []string, hediffs ...*o.Hediff) *o.PawnState {
	row := &o.PawnState{
		Pawn: &o.EntityRef{Id: proto.String("joiner")}, Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false),
		MentalState: nil, Issues: []*o.ReadIssue{{Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}},
		Anomaly:   &o.PawnAnomaly{Entity: proto.Bool(false), Creepjoiner: &o.CreepJoinerState{Form: proto.String("Form"), Benefit: proto.String("Benefit"), DownsideTriggered: proto.Bool(triggered)}},
		Biography: &o.PawnBiography{},
		Health:    &o.PawnHealth{Hediffs: hediffs, HediffCompleteness: &o.Completeness{Filtered: proto.Uint64(0)}},
		Equipment: &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("rifle")},
	}
	for _, t := range traits {
		row.Biography.Traits = append(row.Biography.Traits, &o.Trait{DefName: proto.String(t)})
	}
	return row
}

func hediff(def string, visible bool) *o.Hediff {
	return &o.Hediff{DefName: proto.String(def), Visible: proto.Bool(visible)}
}

// TestCreepJoinerPawnAndHandLift: the downside check's facts come
// from a recorded colonist row: traits, visible hediffs only, the tracker's
// flag, availability and the weapon in hand; a row with no Anomaly block is
// no creepjoiner, and an unread read stays unknown.
func TestCreepJoinerPawnAndHandLift(t *testing.T) {
	trait := func(def string) *d.Opt_BackstoryTrait {
		return &d.Opt_BackstoryTrait{Value: &d.BackstoryTrait{Def: def}}
	}
	catalog := &DefinitionCatalog{Defs: map[protoreflect.FullName]map[string]proto.Message{
		(&d.CreepJoinerDownsideDef{}).ProtoReflect().Descriptor().FullName(): {
			"A": &d.CreepJoinerDownsideDef{Traits: []*d.Opt_BackstoryTrait{trait("TraitX")}},
			"B": &d.CreepJoinerDownsideDef{Hediffs: []string{"HediffY"}, Traits: []*d.Opt_BackstoryTrait{trait("TraitZ")}}}}}
	downsides := catalog.CreepJoinerDownsides()
	if !downsides.Traits["TraitX"] || !downsides.Traits["TraitZ"] || !downsides.Hediffs["HediffY"] || len(downsides.Traits) != 2 {
		t.Fatal("downside defs not indexed", downsides)
	}
	if none := (*DefinitionCatalog)(nil).CreepJoinerDownsides(); len(none.Traits)+len(none.Hediffs) != 0 {
		t.Fatal("a game without Anomaly has downside defs", none)
	}

	hidden := joinerRow(false, []string{"Kind"}, hediff("Scar", true), hediff("HediffY", false))
	if downsides.ArmsHold(CreepJoinerPawn(hidden)) == "" {
		t.Fatal("a creepjoiner with a hidden downside hediff was released")
	}
	shown := joinerRow(false, []string{"Kind", "TraitX"}, hediff("Scar", true))
	if downsides.ArmsHold(CreepJoinerPawn(shown)) != "" {
		t.Fatal("a creepjoiner showing a downside trait was held")
	}
	shown = joinerRow(false, nil, hediff("HediffY", true))
	if downsides.ArmsHold(CreepJoinerPawn(shown)) != "" {
		t.Fatal("a creepjoiner showing a downside hediff was held")
	}
	if downsides.ArmsHold(CreepJoinerPawn(joinerRow(true, nil))) != "" {
		t.Fatal("a creepjoiner whose downside fired was held")
	}

	// No Anomaly block: no creepjoiner. An unread tracker holds.
	noAnomaly := joinerRow(false, nil)
	noAnomaly.Anomaly = nil
	if downsides.ArmsHold(CreepJoinerPawn(noAnomaly)) != "" {
		t.Fatal("a game without Anomaly held a colonist")
	}
	unread := joinerRow(false, nil)
	unread.Anomaly = &o.PawnAnomaly{Issues: []*o.ReadIssue{{Field: proto.String("creepjoiner"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}}
	if downsides.ArmsHold(CreepJoinerPawn(unread)) == "" {
		t.Fatal("an unread creepjoiner tracker was released")
	}
	// A partial hediff read cannot show that nothing is visible.
	partial := joinerRow(false, nil)
	partial.Health.HediffCompleteness.Filtered = proto.Uint64(1)
	if _, known := CreepJoinerPawn(partial).Hediffs.Value(); known {
		t.Fatal("a filtered hediff list was taken as the visible ones")
	}

	hand := CreepJoinerHand(hidden)
	if weapon, known := hand.Weapon.Value(); !known || weapon != "rifle" || hand.Pawn != "joiner" {
		t.Fatal("weapon in hand", hand)
	}
	if available, known := hand.Available.Value(); !known || !available {
		t.Fatal("availability", hand)
	}
	drafted := joinerRow(false, nil)
	drafted.Drafted = proto.Bool(true)
	if available, known := CreepJoinerHand(drafted).Available.Value(); !known || available {
		t.Fatal("a drafted colonist can take an order")
	}
	bare := joinerRow(false, nil)
	bare.Equipment = &o.PawnEquipment{Armed: proto.Bool(false)}
	if weapon, known := CreepJoinerHand(bare).Weapon.Value(); !known || weapon != "" {
		t.Fatal("an unarmed colonist holds a weapon", weapon)
	}
	noPrimary := joinerRow(false, nil)
	noPrimary.Equipment = &o.PawnEquipment{Armed: proto.Bool(true)}
	if _, known := CreepJoinerHand(noPrimary).Weapon.Value(); known {
		t.Fatal("an armed colonist with no primary id was taken as holding a known weapon")
	}
	var _ policy.CreepJoinerHand = hand

	// An unset allowed area is an unrestricted pawn; a failed read is unknown.
	if _, known := CreepJoinerHand(hidden).Area.Value(); known {
		t.Fatal("a row with no settings has a known allowed area")
	}
	restricted := joinerRow(false, nil)
	restricted.Settings = &o.PawnSettings{AllowedAreaId: proto.String("Area_Allowed_3")}
	if area, known := CreepJoinerHand(restricted).Area.Value(); !known || area != "Area_Allowed_3" {
		t.Fatal("allowed area", area, known)
	}
	restricted.Settings = &o.PawnSettings{}
	if area, known := CreepJoinerHand(restricted).Area.Value(); !known || area != "" {
		t.Fatal("an unset allowed area is not unrestricted", area, known)
	}
	restricted.Settings = &o.PawnSettings{Issues: []*o.ReadIssue{{Field: proto.String("allowed_area_id")}}}
	if _, known := CreepJoinerHand(restricted).Area.Value(); known {
		t.Fatal("a failed allowed area read is known")
	}
	hungry := joinerRow(false, nil)
	hungry.Needs = &o.PawnNeeds{HungerCategory: o.HungerCategory_HUNGER_CATEGORY_HUNGRY.Enum()}
	if v, known := CreepJoinerHand(hungry).Hungry.Value(); !known || !v {
		t.Fatal("a hungry colonist is not hungry")
	}
	hungry.Needs = &o.PawnNeeds{HungerCategory: o.HungerCategory_HUNGER_CATEGORY_FED.Enum()}
	if v, known := CreepJoinerHand(hungry).Hungry.Value(); !known || v {
		t.Fatal("a fed colonist is hungry")
	}
	if _, known := CreepJoinerHand(hidden).Hungry.Value(); known {
		t.Fatal("a row with no needs has known hunger")
	}
}

// TestRecordedCatalogAnomalyFromMirror: the recorded all-DLC catalog reports
// Anomaly and its downside defs' traits and hediffs from the def mirror; a
// catalog without the rows reports neither.
func TestRecordedCatalogAnomalyFromMirror(t *testing.T) {
	catalog := fullCatalog(t)
	if !catalog.HasAnomaly() {
		t.Fatal("the recorded catalog has no Anomaly")
	}
	if downsides := catalog.CreepJoinerDownsides(); len(downsides.Traits)+len(downsides.Hediffs) == 0 {
		t.Fatal("the recorded catalog has no creepjoiner downside traits or hediffs")
	}
	if (&DefinitionCatalog{}).HasAnomaly() || (*DefinitionCatalog)(nil).HasAnomaly() {
		t.Fatal("a catalog with no EntityCategoryDef rows has Anomaly")
	}
}
