package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func permitFacts(points int, held ...string) RoyaltyFacts {
	permit := func(name, min string, cost int) RoyalPermit {
		return RoyalPermit{Name: name, MinTitle: domain.Known(min), PermitPoints: domain.Known(cost)}
	}
	return RoyaltyFacts{
		Ladder: throneLadder(),
		Permits: map[string]RoyalPermit{
			"CallMilitaryAidSmall": permit("CallMilitaryAidSmall", "Yeoman", 1),
			"TradeSettlement":      permit("TradeSettlement", "Yeoman", 1),
			"CallTransportShuttle": permit("CallTransportShuttle", "Knight", 2),
			"PsycastNovice":        permit("PsycastNovice", "Yeoman", 1),
			"CallMilitaryAidLarge": permit("CallMilitaryAidLarge", "Baron", 2),
		},
		Holders: map[PawnID][]RoyalHolding{"Alice": {{FactionDef: "Empire", Title: "Yeoman",
			PermitPoints: domain.Known(points), Permits: held}}},
	}
}

func rankedNames(f RoyaltyFacts) []string {
	var out []string
	for _, c := range RankPermits(f) {
		out = append(out, c.Permit)
	}
	return out
}

func TestRankPermitsOrdersByColonyValueTakeableFirst(t *testing.T) {
	got := rankedNames(permitFacts(1))
	want := []string{"CallMilitaryAidSmall", "TradeSettlement", "PsycastNovice", "CallMilitaryAidLarge", "CallTransportShuttle"}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("got %v want %v", got, want)
		}
	}
}

func TestRankPermitsReasons(t *testing.T) {
	reasons := map[string]PermitReason{}
	for _, c := range RankPermits(permitFacts(1)) {
		reasons[c.Permit] = c.Reason
	}
	if reasons["CallTransportShuttle"] != PermitTitleShort || reasons["CallMilitaryAidLarge"] != PermitTitleShort {
		t.Fatalf("%v", reasons)
	}
	for _, c := range RankPermits(permitFacts(0)) {
		if c.Take || c.Reason != PermitPointsShort && c.Reason != PermitTitleShort {
			t.Fatalf("%+v", c)
		}
	}
	if _, ok := NextPermit(permitFacts(0)); ok {
		t.Fatal("no points, no permit")
	}
}

func TestNextPermitSkipsHeldAndPrefersPsycastForPsycasters(t *testing.T) {
	got, ok := NextPermit(permitFacts(1, "CallMilitaryAidSmall"))
	if !ok || got.Permit != "TradeSettlement" || got.Category != PermitTrade {
		t.Fatalf("%+v %v", got, ok)
	}
	f := permitFacts(1, "CallMilitaryAidSmall")
	f.Psycasts = map[PawnID][]Psycast{"Alice": {{Def: "Skip"}}}
	got, ok = NextPermit(f)
	if !ok || got.Permit != "PsycastNovice" || got.Intent() != (PermitIntent{"Alice", "Empire", "PsycastNovice"}) {
		t.Fatalf("%+v %v", got, ok)
	}
}

func TestRankPermitsHoldsOnUnknownFacts(t *testing.T) {
	f := permitFacts(1)
	f.Holders["Alice"][0].PermitPoints = domain.Fact[int]{}
	for _, c := range RankPermits(f) {
		if c.Take || c.Reason != PermitUnknownCost {
			t.Fatalf("%+v", c)
		}
	}
	f = permitFacts(5)
	f.Holders["Alice"][0].Title = ""
	if len(RankPermits(f)) != 0 {
		t.Fatal("untitled holder takes no permit")
	}
}
