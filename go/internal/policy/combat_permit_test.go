package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func permitFight(hostiles int, mutate func(*RoyaltyFacts)) CombatView {
	view := CombatView{
		Pawns:     []CombatPawnState{{ID: "d1", Cell: domain.Known(domain.Cell{X: 10, Z: 10})}},
		Defenders: []SquadDefenderFacts{squadDefender("d1", true)},
		Orderable: []domain.PawnID{"d1"},
	}
	for i := 0; i < hostiles; i++ {
		id := domain.PawnID("h" + string(rune('1'+i)))
		view.Pawns = append(view.Pawns, CombatPawnState{ID: id, Cell: domain.Known(domain.Cell{X: 60 + int32(i), Z: 10})})
		view.Threats = append(view.Threats, SquadThreatFacts{ID: PawnID(id), BodySize: domain.Known(1.0)})
	}
	royalty := RoyaltyFacts{
		Permits: map[string]RoyalPermit{
			"CallMilitaryAidSmall": {Name: "CallMilitaryAidSmall", Acts: domain.Known(true), FavorCost: domain.Known(2)},
			"CallOrbitalStrike":    {Name: "CallOrbitalStrike", Acts: domain.Known(true), FavorCost: domain.Known(3)},
			"CallLaborerGang":      {Name: "CallLaborerGang", Acts: domain.Known(true), FavorCost: domain.Known(1)},
			"TradeSettlement":      {Name: "TradeSettlement", Acts: domain.Known(false)},
		},
		Holders: map[PawnID][]RoyalHolding{"d1": {{
			FactionDef: "Empire", Title: "Yeoman", Favor: domain.Known(10),
			Permits: []string{"CallMilitaryAidSmall", "CallOrbitalStrike", "CallLaborerGang", "TradeSettlement"},
			Cooldowns: map[string]PermitCooldown{
				"CallMilitaryAidSmall": {RemainingTicks: domain.Known(0)},
				"CallOrbitalStrike":    {RemainingTicks: domain.Known(0)},
				"CallLaborerGang":      {RemainingTicks: domain.Known(0)},
			},
		}}},
	}
	if mutate != nil {
		mutate(&royalty)
	}
	view.Royalty = domain.Known(royalty)
	return view
}

func TestPermitCallsWhenOutmatched(t *testing.T) {
	var m CombatMemory
	calls := permitCalls(permitFight(3, nil), &m)
	want := []CombatOrder{
		{Pawn: "d1", Kind: OrderPermit, Cell: domain.Cell{X: 10, Z: 10}, Faction: "Empire", Permit: "CallMilitaryAidSmall", Reason: ReasonPermit},
		{Pawn: "d1", Kind: OrderPermit, Cell: domain.Cell{X: 60, Z: 10}, Faction: "Empire", Permit: "CallOrbitalStrike", Reason: ReasonPermit},
	}
	if len(calls) != 2 || calls[0] != want[0] || calls[1] != want[1] {
		t.Fatalf("calls %+v, want %+v", calls, want)
	}
	if again := permitCalls(permitFight(3, nil), &m); len(again) != 0 {
		t.Fatalf("called twice in one fight: %+v", again)
	}
}

func TestPermitCallsHold(t *testing.T) {
	cases := []struct {
		name     string
		hostiles int
		mutate   func(*RoyaltyFacts)
		clear    bool
	}{
		{name: "not outmatched", hostiles: 1},
		{name: "on cooldown", hostiles: 3, mutate: func(f *RoyaltyFacts) {
			f.Holders["d1"][0].Cooldowns = map[string]PermitCooldown{
				"CallMilitaryAidSmall": {RemainingTicks: domain.Known(900)},
				"CallOrbitalStrike":    {RemainingTicks: domain.Known(900)},
			}
		}},
		{name: "cooldown unread", hostiles: 3, mutate: func(f *RoyaltyFacts) { f.Holders["d1"][0].Cooldowns = nil }},
		{name: "favor short", hostiles: 3, mutate: func(f *RoyaltyFacts) { f.Holders["d1"][0].Favor = domain.Known(1) }},
		{name: "favor unread", hostiles: 3, mutate: func(f *RoyaltyFacts) { f.Holders["d1"][0].Favor = domain.Fact[int]{} }},
		{name: "permit not held", hostiles: 3, mutate: func(f *RoyaltyFacts) { f.Holders["d1"][0].Permits = []string{"CallLaborerGang"} }},
		{name: "royalty unread", hostiles: 3, clear: true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			view := permitFight(c.hostiles, c.mutate)
			if c.clear {
				view.Royalty = domain.Fact[RoyaltyFacts]{}
			}
			var m CombatMemory
			if calls := permitCalls(view, &m); len(calls) != 0 {
				t.Fatalf("calls %+v", calls)
			}
		})
	}
}

func TestStrikeSkipsClumpNearColonists(t *testing.T) {
	view := permitFight(3, nil)
	view.Pawns[1].Cell = domain.Known(domain.Cell{X: 15, Z: 10})
	view.Pawns[2].Cell = domain.Known(domain.Cell{X: 16, Z: 10})
	view.Pawns[3].Cell = domain.Known(domain.Cell{X: 17, Z: 10})
	var m CombatMemory
	for _, c := range permitCalls(view, &m) {
		if c.Permit == "CallOrbitalStrike" {
			t.Fatalf("struck at friendly cell: %+v", c)
		}
	}
}

func TestPermitCallRidesDecideCombatWithoutIssue(t *testing.T) {
	view := permitFight(3, nil)
	orders, _, next := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	calls := 0
	for _, o := range orders {
		if o.Kind == OrderPermit {
			calls++
		}
	}
	if calls == 0 || len(next.Permitted) != calls {
		t.Fatalf("calls %d, permitted %v, orders %+v", calls, next.Permitted, orders)
	}
}
