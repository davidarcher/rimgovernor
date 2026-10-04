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

// The read's worker class decides what an acting permit gives the colony; a
// def name that says otherwise does not change it.
func TestPermitCategoryFollowsWorkerClass(t *testing.T) {
	for worker, want := range map[string]PermitCategory{
		"RoyalTitlePermitWorker_CallAid":       PermitAid,
		"RoyalTitlePermitWorker_CallLaborers":  PermitAid,
		"RoyalTitlePermitWorker_CallShuttle":   PermitDropPod,
		"RoyalTitlePermitWorker_DropResources": PermitDropPod,
		"RoyalTitlePermitWorker_OrbitalStrike": PermitOther,
	} {
		if got := permitCategory(RoyalPermit{Name: "TradeSomething", Worker: worker}); got != want {
			t.Fatalf("%s: %s want %s", worker, got, want)
		}
	}
	// Passive permits carry no worker of their own: the def name decides.
	if got := permitCategory(RoyalPermit{Name: "TradeSettlement", Worker: "RoyalTitlePermitWorker"}); got != PermitTrade {
		t.Fatal(got)
	}
}

// The chosen permit becomes the choose_permit pawn setting the goal commits, and the
// goal measures spent once nothing worthwhile is takeable.
func TestPermitIntentIsTheRoyaltyWriteAndGoalMeasuresSpent(t *testing.T) {
	got, ok := NextPermit(permitFacts(1))
	if !ok {
		t.Fatal("a permit is takeable")
	}
	setting, err := got.Intent().Setting()
	faction, permit, ok := setting.ChoosePermit()
	if err != nil || !ok || setting.Pawn() != "Alice" || faction != "Empire" || permit != "CallMilitaryAidSmall" {
		t.Fatalf("%+v %v", setting, err)
	}
	if spent, known := PermitsSpent(domain.Known(permitFacts(1))).Value(); !known || spent {
		t.Fatal("points for a worthwhile permit are not spent", spent, known)
	}
	if spent, known := PermitsSpent(domain.Known(permitFacts(0))).Value(); !known || !spent {
		t.Fatal("no points is spent", spent, known)
	}
	if _, known := PermitsSpent(domain.Unknown[RoyaltyFacts]()).Value(); known {
		t.Fatal("spent known without the royalty read")
	}
}

func TestPermitsRaiseMaintainPermitsOnlyWhileOneIsTakeable(t *testing.T) {
	has := func(royalty domain.Fact[RoyaltyFacts]) bool {
		f := stableRoutine()
		f.Royalty = royalty
		for _, g := range needs(t, f, RoutineLatches{}).Goals {
			if g.ID == MaintainPermits {
				return true
			}
		}
		return false
	}
	if !has(domain.Known(permitFacts(1))) {
		t.Fatal("a takeable permit raised no MaintainPermits goal")
	}
	if has(domain.Known(permitFacts(0))) || has(domain.Unknown[RoyaltyFacts]()) {
		t.Fatal("no points or no read raised MaintainPermits")
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
