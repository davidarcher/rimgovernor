package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
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
	return &o.Hediff{Definition: &o.DefinitionRef{DefName: proto.String(def)}, Visible: proto.Bool(visible)}
}

// TestCreepJoinerPawnAndHandLift (#1740): the downside check's facts come
// from a recorded colonist row: traits, visible hediffs only, the tracker's
// flag, availability and the weapon in hand; a row with no Anomaly block is
// no creepjoiner, and an unread read stays unknown.
func TestCreepJoinerPawnAndHandLift(t *testing.T) {
	catalog := &DefinitionCatalog{Anomaly: &AnomalyCatalog{CreepJoinerDownsides: map[string]*o.CreepJoinerDownsideRow{
		"A": {Traits: []string{"TraitX"}}, "B": {Hediffs: []string{"HediffY"}, Traits: []string{"TraitZ"}}}}}
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
}
