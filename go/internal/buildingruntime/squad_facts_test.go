package buildingruntime

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestSquadThreatFactsAnimalManhunterBodySize(t *testing.T) {
	row := &o.PawnState{
		Pawn:        &o.EntityRef{Id: proto.String("wolf")},
		Animal:      proto.Bool(true),
		Humanlike:   proto.Bool(false),
		MentalState: proto.String("ManhunterPermanent"),
		AnimalState: &o.AnimalState{BodySize: proto.Float64(1.4)},
	}
	facts := squadThreatFacts(row)
	if size, ok := facts.BodySize.Value(); !ok || size != 1.4 {
		t.Fatal("body size not decoded", facts.BodySize)
	}
	if manhunter, ok := facts.Manhunter.Value(); !ok || !manhunter {
		t.Fatal("manhunter not decoded", facts.Manhunter)
	}
}

func TestSquadThreatFactsNonManhunterMentalStateKnownFalse(t *testing.T) {
	row := &o.PawnState{
		Pawn:        &o.EntityRef{Id: proto.String("berserk-colonist")},
		MentalState: proto.String("Berserk"),
	}
	facts := squadThreatFacts(row)
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
	facts := squadThreatFacts(row)
	if manhunter, ok := facts.Manhunter.Value(); !ok || manhunter {
		t.Fatal("expected known-false manhunter from a not-applicable issue", facts.Manhunter)
	}
}

func TestSquadThreatFactsMentalStateUnknownWithoutIssue(t *testing.T) {
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("unread")}}
	facts := squadThreatFacts(row)
	if _, ok := facts.Manhunter.Value(); ok {
		t.Fatal("expected unknown manhunter without an explicit not-applicable issue")
	}
}

func TestSquadThreatFactsBodySizeUnknownWithIssue(t *testing.T) {
	row := &o.PawnState{
		Pawn:        &o.EntityRef{Id: proto.String("fogged-animal")},
		AnimalState: &o.AnimalState{Issues: []*o.ReadIssue{{Field: proto.String("body_size"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}}},
	}
	facts := squadThreatFacts(row)
	if _, ok := facts.BodySize.Value(); ok {
		t.Fatal("expected unknown body size when the field carries an issue")
	}
}
