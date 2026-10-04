package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func royaltyColonyRead() *o.RoyaltyColonyFacts {
	return &o.RoyaltyColonyFacts{
		Ceremonies: []*o.BestowingCeremony{{Quest: proto.String("Quest_4"), Pawn: &c.Ref{Id: proto.String("Human12")}, Bestower: &c.Ref{Id: proto.String("Human30")}, FactionDef: proto.String("Empire"),
			Title: proto.String("Knight"), Accepted: proto.Bool(true), BestowerWaiting: proto.Bool(true), Spot: &c.Cell{X: proto.Int32(4), Z: proto.Int32(9)}, Attendees: []*c.Ref{{Id: proto.String("Human13")}}}},
		Thrones: []*o.RoyalThrone{{Thing: &c.Ref{Id: proto.String("Throne_1")}, DefName: proto.String("Throne"), Owner: &c.Ref{Id: proto.String("Human12")}}, {Thing: &c.Ref{Id: proto.String("Throne_2")}, DefName: proto.String("Throne")}},
		Neuroformers: []*o.NeuroformerStock{
			{DefName: proto.String("PsychicAmplifier"), Held: proto.Int32(2), Craftable: proto.Bool(false), Tradeable: proto.Bool(true)},
			{DefName: proto.String("Neurotrainer_Skip"), TeachesPsycast: proto.String("Skip")},
		},
	}
}

// TestDecodeRoyaltyColony (#1877): the neuroformer stock, bestowing ceremony
// and throne owners decode; an absent flag stays unknown rather than zero.
func TestDecodeRoyaltyColony(t *testing.T) {
	facts, err := DecodeRoyaltyColony(royaltyColonyRead())
	if err != nil {
		t.Fatal(err)
	}
	amp := facts.Neuroformers["PsychicAmplifier"]
	if held, ok := amp.Held.Value(); !ok || held != 2 {
		t.Fatalf("held %v %v", held, ok)
	}
	if tradeable, ok := amp.Tradeable.Value(); !ok || !tradeable {
		t.Fatalf("tradeable %v %v", tradeable, ok)
	}
	if craftable, ok := amp.Craftable.Value(); !ok || craftable {
		t.Fatalf("craftable %v %v", craftable, ok)
	}
	trainer := facts.Neuroformers["Neurotrainer_Skip"]
	if trainer.TeachesPsycast != "Skip" {
		t.Fatalf("trainer %+v", trainer)
	}
	if _, ok := trainer.Held.Value(); ok {
		t.Fatal("absent held read as known")
	}
	// The throne-owner fact (#1601): an owned throne and an unowned one.
	if len(facts.Thrones) != 2 || facts.Thrones[0] != (policy.RoyalThrone{ID: "Throne_1", Def: "Throne", Owner: "Human12"}) || facts.Thrones[1] != (policy.RoyalThrone{ID: "Throne_2", Def: "Throne"}) {
		t.Fatalf("thrones %+v", facts.Thrones)
	}
	if len(facts.Ceremonies) != 1 {
		t.Fatalf("ceremonies %+v", facts.Ceremonies)
	}
	cm := facts.Ceremonies[0]
	if cm.Quest != "Quest_4" || cm.Pawn != "Human12" || cm.Bestower != "Human30" || cm.Title != "Knight" || len(cm.Attendees) != 1 || cm.Attendees[0] != "Human13" {
		t.Fatalf("ceremony %+v", cm)
	}
	if w, ok := cm.BestowerWaiting.Value(); !ok || !w {
		t.Fatal("bestower waiting")
	}
	if _, ok := cm.Started.Value(); ok {
		t.Fatal("absent started read as known")
	}
	if spot, ok := cm.Spot.Value(); !ok || spot.X != 4 || spot.Z != 9 {
		t.Fatalf("spot %+v", spot)
	}
}

func TestDecodeRoyaltyColonyRefusesMalformedRows(t *testing.T) {
	for _, change := range []string{"ceremony-quest", "ceremony-duplicate", "ceremony-attendee", "neuroformer-duplicate", "neuroformer-held", "throne-duplicate", "throne-owner"} {
		t.Run(change, func(t *testing.T) {
			v := royaltyColonyRead()
			switch change {
			case "ceremony-quest":
				v.Ceremonies[0].Quest = proto.String("")
			case "ceremony-duplicate":
				v.Ceremonies = append(v.Ceremonies, v.Ceremonies[0])
			case "ceremony-attendee":
				v.Ceremonies[0].Attendees[0].Id = proto.String("")
			case "neuroformer-duplicate":
				v.Neuroformers = append(v.Neuroformers, v.Neuroformers[0])
			case "neuroformer-held":
				v.Neuroformers[0].Held = proto.Int32(-1)
			case "throne-duplicate":
				v.Thrones = append(v.Thrones, v.Thrones[0])
			case "throne-owner":
				v.Thrones[0].Owner.Id = proto.String(" ")
			}
			if _, err := DecodeRoyaltyColony(v); err == nil {
				t.Fatal("malformed royalty colony facts accepted")
			}
		})
	}
	if _, err := DecodeRoyaltyColony(nil); err == nil {
		t.Fatal("missing royalty colony facts accepted")
	}
}
