package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func favorPrisonerSheetRow(id string) TradeSheetRowFact {
	return TradeSheetRowFact{
		LineID: "#" + id, DefName: "Human", PawnID: id, ColonyCount: 1, SellPrice: 3, SellPriceKnown: true,
		TraderWillTrade: true, TraderWillTradeKnown: true, CurrencyKnown: true, Pawn: true, PawnKnown: true,
		GuestStatus: "Prisoner", PrisonerSecure: true, PrisonerSecureKnown: true, PawnDownedKnown: true,
	}
}

func favorPrisonerFacts(rows ...TradeSheetRowFact) TradeSelectionFacts {
	facts := favorFacts(rows...)
	facts.FavorPrisoners = map[string]bool{}
	for _, row := range rows {
		facts.FavorPrisoners[row.PawnID] = true
	}
	return facts
}

func TestSelectFavorSaleSellsASurplusPrisoner(t *testing.T) {
	got := SelectFavorSale(favorPrisonerFacts(favorPrisonerSheetRow("p")), 50)
	if got.Refused || len(got.Selected) != 1 || got.Selected[0] != (TradeSelectionLine{LineID: "#p", DefName: "Human", Count: -1}) {
		t.Fatalf("selection %+v", got)
	}
	// AcceptTrade names a floor for the row's definition.
	if e := got.Evidence[0]; !e.Matched || e.Item != "Human" || e.RetainedTarget != 0 {
		t.Fatalf("evidence %+v", got.Evidence)
	}
}

func TestSelectFavorSaleHoldsAnIneligiblePrisonerRow(t *testing.T) {
	edit := func(f func(*TradeSheetRowFact)) TradeSheetRowFact {
		row := favorPrisonerSheetRow("p")
		f(&row)
		return row
	}
	for name, row := range map[string]TradeSheetRowFact{
		"faction-owned home": edit(func(r *TradeSheetRowFact) { r.ExtraHomeFaction = "Faction_3" }),
		"quest host":         edit(func(r *TradeSheetRowFact) { r.ExtraHostFaction = "Faction_3" }),
		"downed":             edit(func(r *TradeSheetRowFact) { r.PawnDowned = true }),
		"downed unknown":     edit(func(r *TradeSheetRowFact) { r.PawnDownedKnown = false }),
		"unsecure":           edit(func(r *TradeSheetRowFact) { r.PrisonerSecure = false }),
		"secure unknown":     edit(func(r *TradeSheetRowFact) { r.PrisonerSecureKnown = false }),
		"slave":              edit(func(r *TradeSheetRowFact) { r.GuestStatus = "Slave" }),
		"guest":              edit(func(r *TradeSheetRowFact) { r.GuestStatus = "Guest" }),
		"status unknown":     edit(func(r *TradeSheetRowFact) { r.GuestStatus = "" }),
		"unpriced":           edit(func(r *TradeSheetRowFact) { r.SellPriceKnown = false }),
		"won't trade":        edit(func(r *TradeSheetRowFact) { r.TraderWillTrade = false }),
	} {
		if got := SelectFavorSale(favorPrisonerFacts(row), 50); got.Refused || len(got.Selected) != 0 {
			t.Fatalf("%s: selected %+v", name, got)
		}
	}
	// A prisoner the surplus set does not name (a colonist, a recruit) is never sold.
	facts := favorPrisonerFacts(favorPrisonerSheetRow("p"))
	facts.FavorPrisoners = nil
	if got := SelectFavorSale(facts, 50); len(got.Selected) != 0 {
		t.Fatalf("not surplus: selected %+v", got)
	}
}

// prisonerRounds is a stable colony of 3 holding the given prisoners under a
// ruleset; a nil ideology is a game without Ideology.
func prisonerRounds(ideology *Ideoligion, rows ...PrisonerFacts) RoundsFacts {
	f := stableRounds()
	f.Prisoners = domain.Known(rows)
	colony := PrisonerColony{Colonists: 3, BestSkill: core.BestSkill}
	f.IdeologyInstalled = domain.Known(false)
	if ideology != nil {
		f.IdeologyInstalled, f.Ideology = domain.Known(true), domain.Known(*ideology)
		colony.IdeologyActive, colony.Ideology = true, f.Ideology
	}
	f.PrisonerColony = domain.Known(colony)
	return f
}

// surplusRow is a worthless, unrecruitable prisoner who cannot labor, with nothing queued.
func surplusRow(id string) PrisonerFacts {
	row := prisonerRow(id, false, domain.PrisonerInteractionMaintain, 0, 1, frail)
	row.CreepJoiner, row.QueuedSurgeries = domain.Known(false), domain.Known(0)
	return row
}

func TestSurplusPrisonersExcludesEveryUse(t *testing.T) {
	edit := func(f func(*PrisonerFacts)) PrisonerFacts {
		row := surplusRow("p")
		f(&row)
		return row
	}
	if got := prisonerRounds(nil, surplusRow("p")).SurplusPrisoners(false); !got["p"] || len(got) != 1 {
		t.Fatalf("surplus %v", got)
	}
	for name, row := range map[string]PrisonerFacts{
		"recruit-wanted":     edit(func(r *PrisonerFacts) { r.Recruitable, r.Prospect = domain.Known(true), domain.Known(strong) }),
		"creepjoiner":        edit(func(r *PrisonerFacts) { r.CreepJoiner = domain.Known(true) }),
		"creepjoiner unread": edit(func(r *PrisonerFacts) { r.CreepJoiner = domain.Unknown[bool]() }),
		"recruitable unread": edit(func(r *PrisonerFacts) { r.Recruitable = domain.Unknown[bool]() }),
		"prospect unread":    edit(func(r *PrisonerFacts) { r.Prospect = domain.Unknown[PrisonerProspect]() }),
		"queued surgery":     edit(func(r *PrisonerFacts) { r.QueuedSurgeries = domain.Known(1) }),
		"queue unread":       edit(func(r *PrisonerFacts) { r.QueuedSurgeries = domain.Unknown[int]() }),
		"executing":          edit(func(r *PrisonerFacts) { r.Executing = true }),
		"dead":               edit(func(r *PrisonerFacts) { r.Dead = domain.Known(true) }),
	} {
		if got := prisonerRounds(nil, row).SurplusPrisoners(false); len(got) != 0 {
			t.Fatalf("%s: surplus %v", name, got)
		}
	}
	// Unknown census or medical facts hold everything.
	f := prisonerRounds(nil, surplusRow("p"))
	f.Prisoners = domain.Unknown[[]PrisonerFacts]()
	if got := f.SurplusPrisoners(false); len(got) != 0 {
		t.Fatalf("no census: %v", got)
	}
	f = prisonerRounds(nil, surplusRow("p"))
	f.MedicalPawns = domain.Unknown[[]CarePawn]()
	if got := f.SurplusPrisoners(false); len(got) != 0 {
		t.Fatalf("no medical census: %v", got)
	}
}

func TestSurplusPrisonersSkipsAnOrganHarvestCandidate(t *testing.T) {
	// With the silver runway short, the harvest would sell a lung from a
	// worthless prisoner: that prisoner is the organ plan's, not surplus.
	harvest := harvestPrisoner("p", 0)
	harvest.CreepJoiner = domain.Known(false)
	f := prisonerRounds(nil, harvest, surplusRow("q"))
	if got := f.SurplusPrisoners(true); got["p"] || !got["q"] {
		t.Fatalf("short runway: surplus %v", got)
	}
	if got := f.SurplusPrisoners(false); !got["p"] || !got["q"] {
		t.Fatalf("no harvest need: surplus %v", got)
	}
}

func TestSurplusPrisonersSkipsOneMaintainPopulationWouldEnslave(t *testing.T) {
	ideo := ruleIdeoligion(PreceptDef{Name: "Slavery_Test"})
	row := surplusRow("p")
	row.Prospect = domain.Known(weak)
	f := prisonerRounds(&ideo, row)
	f.Ideology = domain.Known(ideo)
	if got := f.SurplusPrisoners(false); len(got) != 0 {
		t.Fatalf("a labouring prisoner in a slaving colony is surplus: %v", got)
	}
}

func TestSurplusPrisonersObeysPrecepts(t *testing.T) {
	ideology := func(effects ...PreceptEffect) *Ideoligion {
		i := ruleIdeoligion(PreceptDef{Name: "Trade_Test", Effects: effects})
		return &i
	}
	for name, c := range map[string]struct {
		ideology *Ideoligion
		unread   bool
		sold     bool
	}{
		"no Ideology":  {nil, false, true},
		"no precept":   {ideology(), false, true},
		"approved":     {ideology(took(SoldPrisonerEvent, 3)), false, true},
		"mood cost":    {ideology(took(SoldPrisonerEvent, -4)), false, false},
		"witness cost": {ideology(saw(SoldPrisonerEvent, -2)), false, false},
		"refused":      {ideology(unwilling(SoldPrisonerEvent, nil)), false, false},
		"unread":       {nil, true, false},
	} {
		f := prisonerRounds(c.ideology, surplusRow("p"))
		if c.unread {
			f.IdeologyInstalled, f.Ideology = domain.Known(true), domain.Unknown[Ideoligion]()
		}
		if got := f.SurplusPrisoners(false)["p"]; got != c.sold {
			t.Fatalf("%s: sold %v", name, got)
		}
	}
}

func TestFavorPrisonerNeedRequiresACollector(t *testing.T) {
	collector := domain.Known([]TraderFacts{{ID: "t", Kind: TributeCollectorKind, CanTrade: true}})
	merchant := domain.Known([]TraderFacts{{ID: "t", Kind: "Caravan_Outlander_BulkGoods", CanTrade: true}})
	base := domain.Known(TradeNeed{})
	surplus := map[string]bool{"p": true}
	need := func(traders domain.Fact[[]TraderFacts], surplus map[string]bool) int64 {
		n, _ := FavorPrisonerNeed(base, traders, surplus).Value()
		return n.FavorPrisoners
	}
	if got := need(collector, surplus); got != 1 {
		t.Fatalf("collector with a surplus prisoner needs %d", got)
	}
	for name, got := range map[string]int64{
		"no collector": need(merchant, surplus),
		"no surplus":   need(collector, nil),
		"no census":    need(domain.Unknown[[]TraderFacts](), surplus),
	} {
		if got != 0 {
			t.Fatalf("%s: need %d", name, got)
		}
	}
	if n, _ := FavorPrisonerNeed(domain.Known(TradeNeed{FavorPrisoners: 1}), collector, nil).Value(); !n.Any() {
		t.Fatal("a prisoner need is not a need")
	}
}
