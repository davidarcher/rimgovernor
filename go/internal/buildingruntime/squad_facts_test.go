package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// noRaces is the empty race catalog.
var noRaces = policy.AnimalRaceCatalog{}

func TestSquadThreatFactsAnimalManhunterBodySize(t *testing.T) {
	row := &o.PawnState{
		Pawn:        &o.EntityRef{Id: proto.String("wolf"), DefName: proto.String("Wolf_Timber")},
		Animal:      proto.Bool(true),
		Humanlike:   proto.Bool(false),
		MentalState: proto.String("ManhunterPermanent"),
	}
	// The body size is the race row's (#1722).
	races := policy.AnimalRaceCatalog{Races: map[policy.Resource]policy.AnimalRace{"Wolf_Timber": {Def: "Wolf_Timber", BodySize: domain.Known(1.4)}}}
	facts := squadThreatFacts(row, races, domain.Unknown[bool]())
	if size, ok := facts.BodySize.Value(); !ok || size != 1.4 {
		t.Fatal("body size not decoded", facts.BodySize)
	}
	if manhunter, ok := facts.Manhunter.Value(); !ok || !manhunter {
		t.Fatal("manhunter not decoded", facts.Manhunter)
	}
	// A wild animal's equipment reads as a missing native component; it
	// still counts as unarmed, or squad defense could never target it.
	row.Equipment = &o.PawnEquipment{Issues: []*o.ReadIssue{{Field: proto.String("equipped"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NATIVE_COMPONENT_MISSING.Enum()}}}}
	if ranged, ok := squadThreatFacts(row, noRaces, domain.Unknown[bool]()).RangedEquipped.Value(); !ok || ranged {
		t.Fatal("animal must read as not ranged-equipped", facts.RangedEquipped)
	}
	row.Animal = nil
	if _, ok := squadThreatFacts(row, noRaces, domain.Unknown[bool]()).RangedEquipped.Value(); ok {
		t.Fatal("unknown kind must keep the equipment unknown")
	}
}

// TestSquadThreatFactsMeleeEntity (#1739): a melee-only entity carries its
// Anomaly facts and reads as not ranged-equipped, as an animal does; one
// whose attack native did not read as melee keeps its equipment unknown.
func TestSquadThreatFactsMeleeEntity(t *testing.T) {
	row := &o.PawnState{
		Pawn:      &o.EntityRef{Id: proto.String("beast")},
		Animal:    proto.Bool(false),
		Humanlike: proto.Bool(false),
		Anomaly:   &o.PawnAnomaly{Entity: proto.Bool(true), Mutant: proto.Bool(false), MeleeOnly: proto.Bool(true), HiddenFromPlayer: proto.Bool(true)},
	}
	facts := squadThreatFacts(row, noRaces, domain.Unknown[bool]())
	if ranged, ok := facts.RangedEquipped.Value(); !ok || ranged {
		t.Fatal("a melee-only entity must read as not ranged-equipped", facts.RangedEquipped)
	}
	if a, ok := facts.Anomaly.Value(); !ok || !positiveFact(a.HiddenFromPlayer) {
		t.Fatal("the row's Anomaly facts were not lifted", facts.Anomaly)
	}
	row.Anomaly.MeleeOnly = nil
	if _, ok := squadThreatFacts(row, noRaces, domain.Unknown[bool]()).RangedEquipped.Value(); ok {
		t.Fatal("an unread attack must keep the equipment unknown")
	}
}

func TestSquadThreatFactsNonManhunterMentalStateKnownFalse(t *testing.T) {
	row := &o.PawnState{
		Pawn:        &o.EntityRef{Id: proto.String("berserk-colonist")},
		MentalState: proto.String("Berserk"),
	}
	facts := squadThreatFacts(row, noRaces, domain.Unknown[bool]())
	if manhunter, ok := facts.Manhunter.Value(); !ok || manhunter {
		t.Fatal("expected known-false manhunter for a different mental state", facts.Manhunter)
	}
}

func TestSquadThreatFactsNoMentalStateKnownFalseViaIssue(t *testing.T) {
	row := &o.PawnState{
		Pawn: &o.EntityRef{Id: proto.String("calm-deer")},
		Issues: []*o.ReadIssue{{Field: proto.String("mental_state"), Unavailable: &c.Unavailable{
			Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum(),
		}}},
	}
	facts := squadThreatFacts(row, noRaces, domain.Unknown[bool]())
	if manhunter, ok := facts.Manhunter.Value(); !ok || manhunter {
		t.Fatal("expected known-false manhunter from a not-applicable issue", facts.Manhunter)
	}
}

func TestSquadThreatFactsMentalStateUnknownWithoutIssue(t *testing.T) {
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("unread")}}
	facts := squadThreatFacts(row, noRaces, domain.Unknown[bool]())
	if _, ok := facts.Manhunter.Value(); ok {
		t.Fatal("expected unknown manhunter without an explicit not-applicable issue")
	}
}

func TestSquadThreatFactsBodySizeUnknownWithoutARaceRow(t *testing.T) {
	row := &o.PawnState{
		Pawn:        &o.EntityRef{Id: proto.String("fogged-animal"), DefName: proto.String("Wolf_Timber")},
		AnimalState: &o.AnimalState{},
	}
	facts := squadThreatFacts(row, noRaces, domain.Unknown[bool]())
	if _, ok := facts.BodySize.Value(); ok {
		t.Fatal("expected unknown body size when the catalog holds no race row")
	}
}
