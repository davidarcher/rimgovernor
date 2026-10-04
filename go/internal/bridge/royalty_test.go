package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func royaltyRead() *o.RoyaltyFacts {
	return &o.RoyaltyFacts{
		Context: authorityTestContext(7),
		Ladder: []*o.RoyalTitleRung{
			{DefName: proto.String("Knight"), Seniority: proto.Int32(100), FavorNeeded: proto.Int32(6), BedroomMinImpressiveness: proto.Int32(50), BedroomFloored: proto.Bool(true), BedroomThings: []*o.BedroomThingRequirement{{AnyOf: []string{"EndTable"}, Count: proto.Int32(1)}}},
			{DefName: proto.String("Yeoman")},
		},
		Permits: []*o.RoyalPermitDef{
			{DefName: proto.String("CallLaborerPack"), MinTitle: proto.String("Knight"), PermitPoints: proto.Int32(1), Acts: proto.Bool(true), FavorCost: proto.Int32(6), CooldownDays: proto.Float64(30), WorkerClass: proto.String("RoyalTitlePermitWorker_CallLaborers")},
			{DefName: proto.String("TradeSettlement"), Acts: proto.Bool(false)},
		},
	}
}

// royaltyPawns is the pawn rows the royalty facts merge with (#1876): Human12
// carries the holdings, psycasts and psycaster state.
func royaltyPawns() *o.PawnSnapshot {
	return &o.PawnSnapshot{Pawns: []*o.PawnState{
		{Pawn: &o.EntityRef{Id: proto.String("Human1")}},
		{Pawn: &o.EntityRef{Id: proto.String("Human12")}, Royalty: &o.PawnRoyalty{Holdings: []*o.PawnRoyalHolding{
			{FactionDef: proto.String("Empire"), Title: proto.String("Knight"), Favor: proto.Int32(3), PermitPoints: proto.Int32(0), Permits: []string{"CallLaborerPack"}, PermitCooldowns: []*o.PermitCooldown{{Permit: proto.String("CallLaborerPack"), LastUsedTick: proto.Int32(100), CooldownRemainingTicks: proto.Int32(500)}}},
			{FactionDef: proto.String("Other")},
		}, Psycasts: []*o.PawnPsycast{
			{DefName: proto.String("Skip"), Level: proto.Int32(1), PsyfocusCost: proto.Float64(0.1), Entropy: proto.Float64(12), TargetKind: o.PsycastTargetKind_PSYCAST_TARGET_KIND_CELL, CooldownTicks: proto.Int32(900), CooldownRemainingTicks: proto.Int32(40)},
			{DefName: proto.String("Burden")},
		}, Psyfocus: proto.Float64(0.6), Entropy: proto.Float64(10), EntropyMax: proto.Float64(100)}},
	}}
}

// decodeRoyalty decodes the colony read and merges the pawn rows over it.
func decodeRoyalty(read *o.RoyaltyFacts, pawns *o.PawnSnapshot) (*policy.RoyaltyFacts, error) {
	facts, err := DecodeRoyaltyFacts(read, pbIdentity())
	if err != nil {
		return nil, err
	}
	merged, err := WithPawnRoyalty(*facts, pawns)
	return &merged, err
}

// TestWithPawnRoyaltyRefusesMalformedRows: a pawn's royalty block that is
// invalid, or whose read failed, leaves royalty unknown (#1876).
func TestWithPawnRoyaltyRefusesMalformedRows(t *testing.T) {
	for _, change := range []string{"pawn-id", "holding-faction", "holding-permit", "psycast-duplicate", "psycast-cost", "psycast-target", "read-issue", "no-rows"} {
		t.Run(change, func(t *testing.T) {
			pawns := royaltyPawns()
			row := pawns.Pawns[1]
			switch change {
			case "pawn-id":
				row.Pawn.Id = proto.String("")
			case "holding-faction":
				row.Royalty.Holdings[0].FactionDef = proto.String("")
			case "holding-permit":
				row.Royalty.Holdings[0].Permits = []string{""}
			case "psycast-duplicate":
				row.Royalty.Psycasts = append(row.Royalty.Psycasts, row.Royalty.Psycasts[0])
			case "psycast-cost":
				row.Royalty.Psycasts[0].PsyfocusCost = proto.Float64(1.5)
			case "psycast-target":
				row.Royalty.Psycasts[0].TargetKind = o.PsycastTargetKind(99)
			case "read-issue":
				row.Royalty = nil
				row.Issues = []*o.ReadIssue{{Field: proto.String("royalty")}}
			case "no-rows":
				pawns = nil
			}
			if _, err := decodeRoyalty(royaltyRead(), pawns); err == nil {
				t.Fatal("malformed royalty pawn rows accepted")
			}
		})
	}
}

// TestDecodeRoyaltyFacts (#1599): recorded facts decode, and an absent
// scalar stays unknown rather than zero.
func TestDecodeRoyaltyFacts(t *testing.T) {
	facts, err := decodeRoyalty(royaltyRead(), royaltyPawns())
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
	if _, ok := facts.Ladder[0].Throne.Value(); ok {
		t.Fatal("the native read supplied a throne requirement: the def mirror owns it")
	}
	call := facts.Permits["CallLaborerPack"]
	if call.Worker != "RoyalTitlePermitWorker_CallLaborers" || facts.Permits["TradeSettlement"].Worker != "" {
		t.Fatalf("worker classes %q %q", call.Worker, facts.Permits["TradeSettlement"].Worker)
	}
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
}

// TestDecodeRoyaltyFactsBedroom (#1605): the rung's bedroom requirements
// decode; an absent flag stays unknown.
func TestDecodeRoyaltyFactsBedroom(t *testing.T) {
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
}

func TestDecodeRoyaltyFactsRefusesMalformedRows(t *testing.T) {
	for _, change := range []string{"bedroom-count", "bedroom-def", "world", "title-duplicate", "title-id", "permit-duplicate", "permit-min-title"} {
		t.Run(change, func(t *testing.T) {
			v := royaltyRead()
			switch change {
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
			case "permit-duplicate":
				v.Permits = append(v.Permits, v.Permits[0])
			case "permit-min-title":
				v.Permits[0].MinTitle = proto.String("")
			}
			if _, err := DecodeRoyaltyFacts(v, pbIdentity()); err == nil {
				t.Fatal("malformed royalty facts accepted")
			}
		})
	}
}
