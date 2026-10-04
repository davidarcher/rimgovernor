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

func TestDecodeRoyaltyFactsRefusesMalformedRows(t *testing.T) {
	for _, change := range []string{"world"} {
		t.Run(change, func(t *testing.T) {
			v := royaltyRead()
			switch change {
			case "world":
				v.Context.Identity.LoadToken = proto.String("other")
			}
			if _, err := DecodeRoyaltyFacts(v, pbIdentity()); err == nil {
				t.Fatal("malformed royalty facts accepted")
			}
		})
	}
}
