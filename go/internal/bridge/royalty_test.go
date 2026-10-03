package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func royaltyRead() *o.RoyaltyFacts {
	return &o.RoyaltyFacts{
		Context: authorityTestContext(7),
		Ladder: []*o.RoyalTitleRung{
			{DefName: proto.String("Knight"), Seniority: proto.Int32(100), FavorNeeded: proto.Int32(6), ThroneMinImpressiveness: proto.Int32(55), ThroneMinArea: proto.Int32(30), ThroneThings: []string{"Throne"}, ThroneAssigned: proto.Bool(true),
				BedroomMinImpressiveness: proto.Int32(50), BedroomFloored: proto.Bool(true), BedroomThings: []*o.BedroomThingRequirement{{AnyOf: []string{"EndTable"}, Count: proto.Int32(1)}}},
			{DefName: proto.String("Yeoman")},
		},
		Ceremonies: []*o.BestowingCeremony{{Quest: proto.String("Quest_4"), Pawn: &c.Ref{Id: proto.String("Human12")}, Bestower: &c.Ref{Id: proto.String("Human30")}, FactionDef: proto.String("Empire"),
			Title: proto.String("Knight"), Accepted: proto.Bool(true), BestowerWaiting: proto.Bool(true), Spot: &c.Cell{X: proto.Int32(4), Z: proto.Int32(9)}, Attendees: []*c.Ref{{Id: proto.String("Human13")}}}},
		Permits: []*o.RoyalPermitDef{
			{DefName: proto.String("CallLaborerPack"), MinTitle: proto.String("Knight"), PermitPoints: proto.Int32(1), Acts: proto.Bool(true), FavorCost: proto.Int32(6), CooldownDays: proto.Float64(30)},
			{DefName: proto.String("TradeSettlement"), Acts: proto.Bool(false)},
		},
		Pawns: []*o.PawnRoyalty{{Pawn: &c.Ref{Id: proto.String("Human12")}, Holdings: []*o.PawnRoyalHolding{
			{FactionDef: proto.String("Empire"), Title: proto.String("Knight"), Favor: proto.Int32(3), PermitPoints: proto.Int32(0), Permits: []string{"CallLaborerPack"}, PermitCooldowns: []*o.PermitCooldown{{Permit: proto.String("CallLaborerPack"), LastUsedTick: proto.Int32(100), CooldownRemainingTicks: proto.Int32(500)}}},
			{FactionDef: proto.String("Other")},
		}, Psycasts: []*o.PawnPsycast{
			{DefName: proto.String("Skip"), Level: proto.Int32(1), PsyfocusCost: proto.Float64(0.1), Entropy: proto.Float64(12), TargetKind: o.PsycastTargetKind_PSYCAST_TARGET_KIND_CELL, CooldownTicks: proto.Int32(900), CooldownRemainingTicks: proto.Int32(40)},
			{DefName: proto.String("Burden")},
		}, Psyfocus: proto.Float64(0.6), Entropy: proto.Float64(10), EntropyMax: proto.Float64(100)}},
		Thrones: []*o.RoyalThrone{{Thing: &c.Ref{Id: proto.String("Throne_1")}, DefName: proto.String("Throne"), Owner: &c.Ref{Id: proto.String("Human12")}}, {Thing: &c.Ref{Id: proto.String("Throne_2")}, DefName: proto.String("Throne")}},
		Neuroformers: []*o.NeuroformerStock{
			{DefName: proto.String("PsychicAmplifier"), Held: proto.Int32(2), Craftable: proto.Bool(false), Tradeable: proto.Bool(true)},
			{DefName: proto.String("Neurotrainer_Skip"), TeachesPsycast: proto.String("Skip")},
		},
	}
}

// TestDecodeRoyaltyFacts (#1599): recorded facts decode, and an absent
// scalar stays unknown rather than zero.
func TestDecodeRoyaltyFacts(t *testing.T) {
	facts, err := DecodeRoyaltyFacts(royaltyRead(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if len(facts.Ladder) != 2 || facts.Ladder[0].Title != "Knight" {
		t.Fatalf("ladder %+v", facts.Ladder)
	}
	if n, ok := facts.Ladder[0].FavorNeeded.Value(); !ok || n != 6 {
		t.Fatalf("favor needed %v %v", n, ok)
	}
	if _, ok := facts.Ladder[1].FavorNeeded.Value(); ok {
		t.Fatal("absent favor needed read as known")
	}
	knight := facts.Ladder[0]
	if n, ok := knight.ThroneMinImpressiveness.Value(); !ok || n != 55 {
		t.Fatalf("throne impressiveness %v %v", n, ok)
	}
	if n, ok := knight.ThroneMinArea.Value(); !ok || n != 30 {
		t.Fatalf("throne area %v %v", n, ok)
	}
	if assigned, ok := knight.ThroneAssigned.Value(); !ok || !assigned || len(knight.ThroneThings) != 1 || knight.ThroneThings[0] != "Throne" {
		t.Fatalf("throne %+v", knight)
	}
	if _, ok := facts.Ladder[1].ThroneMinArea.Value(); ok {
		t.Fatal("absent throne area read as known")
	}
	call := facts.Permits["CallLaborerPack"]
	if acts, _ := call.Acts.Value(); !acts {
		t.Fatalf("call permit %+v", call)
	}
	if cost, ok := call.FavorCost.Value(); !ok || cost != 6 {
		t.Fatalf("favor cost %v %v", cost, ok)
	}
	if _, ok := facts.Permits["TradeSettlement"].FavorCost.Value(); ok {
		t.Fatal("passive permit has a favor cost")
	}
	holdings := facts.Holders[policy.PawnID("Human12")]
	if len(holdings) != 2 || holdings[0].Title != "Knight" || len(holdings[0].Permits) != 1 {
		t.Fatalf("holdings %+v", holdings)
	}
	if favor, ok := holdings[0].Favor.Value(); !ok || favor != 3 {
		t.Fatalf("favor %v %v", favor, ok)
	}
	if _, ok := holdings[1].Favor.Value(); ok {
		t.Fatal("absent favor read as known")
	}
	casts := facts.Psycasts[policy.PawnID("Human12")]
	if len(casts) != 2 || casts[0].Target != policy.PsycastTargetCell {
		t.Fatalf("psycasts %+v", casts)
	}
	if n, ok := casts[0].CooldownTicks.Value(); !ok || n != 900 {
		t.Fatalf("cooldown %v %v", n, ok)
	}
	if n, ok := casts[0].CooldownRemaining.Value(); !ok || n != 40 {
		t.Fatalf("cooldown remaining %v %v", n, ok)
	}
	if _, ok := casts[1].CooldownRemaining.Value(); ok {
		t.Fatal("absent cooldown remaining read as known")
	}
	state := facts.Casters[policy.PawnID("Human12")]
	if f, ok := state.Psyfocus.Value(); !ok || f != 0.6 {
		t.Fatalf("psyfocus %v %v", f, ok)
	}
	if m, ok := state.EntropyMax.Value(); !ok || m != 100 {
		t.Fatalf("entropy max %v %v", m, ok)
	}
	if _, ok := casts[1].PsyfocusCost.Value(); ok || casts[1].Target != "" {
		t.Fatalf("absent psycast facts read as known: %+v", casts[1])
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
}

// TestDecodeRoyaltyFactsCeremonyAndBedroom (#1602, #1605): the bestowing
// ceremony and the rung's bedroom requirements decode; an absent flag stays
// unknown.
func TestDecodeRoyaltyFactsCeremonyAndBedroom(t *testing.T) {
	facts, err := DecodeRoyaltyFacts(royaltyRead(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	rung := facts.Ladder[0]
	if n, ok := rung.BedroomMinImpressiveness.Value(); !ok || n != 50 || len(rung.BedroomThings) != 1 || rung.BedroomThings[0].AnyOf[0] != "EndTable" || rung.BedroomThings[0].Count != 1 {
		t.Fatalf("bedroom %+v", rung)
	}
	if _, ok := rung.BedroomMinArea.Value(); ok {
		t.Fatal("absent bedroom area read as known")
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

func TestDecodeRoyaltyFactsRefusesMalformedRows(t *testing.T) {
	for _, change := range []string{"ceremony-quest", "ceremony-duplicate", "ceremony-attendee", "bedroom-count", "bedroom-def", "world", "title-duplicate", "title-id", "throne-id", "permit-duplicate", "permit-min-title", "pawn-duplicate", "pawn-id", "holding-faction", "holding-permit", "psycast-duplicate", "psycast-cost", "psycast-target", "neuroformer-duplicate", "neuroformer-held", "throne-duplicate", "throne-owner"} {
		t.Run(change, func(t *testing.T) {
			v := royaltyRead()
			switch change {
			case "ceremony-quest":
				v.Ceremonies[0].Quest = proto.String("")
			case "ceremony-duplicate":
				v.Ceremonies = append(v.Ceremonies, v.Ceremonies[0])
			case "ceremony-attendee":
				v.Ceremonies[0].Attendees[0].Id = proto.String("")
			case "bedroom-count":
				v.Ladder[0].BedroomThings[0].Count = proto.Int32(0)
			case "bedroom-def":
				v.Ladder[0].BedroomThings[0].AnyOf = []string{""}
			case "world":
				v.Context.Identity.LoadToken = proto.String("other")
			case "title-duplicate":
				v.Ladder = append(v.Ladder, v.Ladder[0])
			case "title-id":
				v.Ladder[0].DefName = proto.String("")
			case "throne-id":
				v.Ladder[0].ThroneThings = []string{""}
			case "permit-duplicate":
				v.Permits = append(v.Permits, v.Permits[0])
			case "permit-min-title":
				v.Permits[0].MinTitle = proto.String("")
			case "pawn-duplicate":
				v.Pawns = append(v.Pawns, v.Pawns[0])
			case "pawn-id":
				v.Pawns[0].Pawn.Id = proto.String("")
			case "holding-faction":
				v.Pawns[0].Holdings[0].FactionDef = proto.String("")
			case "holding-permit":
				v.Pawns[0].Holdings[0].Permits = []string{""}
			case "psycast-duplicate":
				v.Pawns[0].Psycasts = append(v.Pawns[0].Psycasts, v.Pawns[0].Psycasts[0])
			case "psycast-cost":
				v.Pawns[0].Psycasts[0].PsyfocusCost = proto.Float64(1.5)
			case "psycast-target":
				v.Pawns[0].Psycasts[0].TargetKind = o.PsycastTargetKind(99)
			case "neuroformer-duplicate":
				v.Neuroformers = append(v.Neuroformers, v.Neuroformers[0])
			case "throne-duplicate":
				v.Thrones = append(v.Thrones, v.Thrones[0])
			case "throne-owner":
				v.Thrones[0].Owner.Id = proto.String(" ")
			case "neuroformer-held":
				v.Neuroformers[0].Held = proto.Int32(-1)
			}
			if _, err := DecodeRoyaltyFacts(v, pbIdentity()); err == nil {
				t.Fatal("malformed royalty facts accepted")
			}
		})
	}
}
