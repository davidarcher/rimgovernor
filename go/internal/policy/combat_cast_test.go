package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func psycast(def string, target PsycastTarget) Psycast {
	return Psycast{Def: def, Target: target, PsyfocusCost: domain.Known(0.1), Entropy: domain.Known(10.0), CooldownTicks: domain.Known(600), CooldownRemaining: domain.Known(0)}
}

// castFight is one caster d1 at (10,10) with a wounded ally d2 at (12,10)
// and a hostile h1 at (25,10), holding one psycast per family.
func castFight(mutate func(*RoyaltyFacts)) CombatView {
	view := CombatView{
		Pawns: []CombatPawnState{
			{ID: "d1", Cell: domain.Known(domain.Cell{X: 10, Z: 10})},
			{ID: "d2", Cell: domain.Known(domain.Cell{X: 12, Z: 10})},
			{ID: "h1", Cell: domain.Known(domain.Cell{X: 25, Z: 10})},
		},
		Defenders: []SquadDefenderFacts{squadDefender("d1", true), squadDefender("d2", true)},
		Threats:   []SquadThreatFacts{{ID: "h1", BodySize: domain.Known(1.0)}},
		Orderable: []domain.PawnID{"d1", "d2"},
	}
	royalty := RoyaltyFacts{
		Psycasts: map[PawnID][]Psycast{"d1": {
			psycast("Painblock", PsycastTargetPawn),
			psycast("Stun", PsycastTargetPawn),
			psycast("Flashstorm", PsycastTargetCell),
			psycast("Skipshield", PsycastTargetSelf),
			psycast("Skip", PsycastTargetCell),
		}},
		Casters: map[PawnID]PsycasterState{"d1": {Psyfocus: domain.Known(0.8), Entropy: domain.Known(0.0), EntropyMax: domain.Known(100.0)}},
	}
	if mutate != nil {
		mutate(&royalty)
	}
	view.Royalty = domain.Known(royalty)
	return view
}

func only(t *testing.T, calls []CombatOrder, def string) CombatOrder {
	t.Helper()
	if len(calls) != 1 || calls[0].Permit != def {
		t.Fatalf("calls %+v, want one %s", calls, def)
	}
	return calls[0]
}

func keep(f *RoyaltyFacts, defs ...string) {
	var kept []Psycast
	for _, c := range f.Psycasts["d1"] {
		for _, d := range defs {
			if c.Def == d {
				kept = append(kept, c)
			}
		}
	}
	f.Psycasts["d1"] = kept
}

func TestCastHealTargetsWoundedDefender(t *testing.T) {
	view := castFight(func(f *RoyaltyFacts) { keep(f, "Painblock") })
	view.Defenders[1].HealthFraction = domain.Known(0.3)
	var m CombatMemory
	got := only(t, castCalls(view, &m), "Painblock")
	if got.Pawn != "d1" || got.Kind != OrderCast || got.Arm != PsycastTargetPawn || got.Target != "d2" {
		t.Fatalf("order %+v", got)
	}
	if len(m.Casts) != 1 {
		t.Fatalf("casts %+v", m.Casts)
	}
}

func TestCastHealHoldsWithoutWoundedDefender(t *testing.T) {
	var m CombatMemory
	if calls := castCalls(castFight(func(f *RoyaltyFacts) { keep(f, "Painblock") }), &m); len(calls) != 0 {
		t.Fatalf("calls %+v", calls)
	}
}

func TestCastStunTargetsNearestHostile(t *testing.T) {
	view := castFight(func(f *RoyaltyFacts) { keep(f, "Stun") })
	var m CombatMemory
	got := only(t, castCalls(view, &m), "Stun")
	if got.Arm != PsycastTargetPawn || got.Target != "h1" {
		t.Fatalf("order %+v", got)
	}
}

func TestCastBurstAimsAtHostileClump(t *testing.T) {
	view := castFight(func(f *RoyaltyFacts) { keep(f, "Flashstorm") })
	var m CombatMemory
	got := only(t, castCalls(view, &m), "Flashstorm")
	if got.Arm != PsycastTargetCell || got.Cell != (domain.Cell{X: 25, Z: 10}) {
		t.Fatalf("order %+v", got)
	}
}

func TestCastBurstSkipsClumpNearColonists(t *testing.T) {
	view := castFight(func(f *RoyaltyFacts) { keep(f, "Flashstorm") })
	view.Pawns[2].Cell = domain.Known(domain.Cell{X: 15, Z: 10})
	var m CombatMemory
	if calls := castCalls(view, &m); len(calls) != 0 {
		t.Fatalf("burst on friendly cell: %+v", calls)
	}
}

func TestCastDefensiveIsSelfCastWithHostileInReach(t *testing.T) {
	view := castFight(func(f *RoyaltyFacts) { keep(f, "Skipshield") })
	var m CombatMemory
	got := only(t, castCalls(view, &m), "Skipshield")
	if got.Arm != PsycastTargetSelf || got.Target != "" || got.Cell != (domain.Cell{}) {
		t.Fatalf("order %+v", got)
	}
	view.Pawns[2].Cell = domain.Known(domain.Cell{X: 60, Z: 10})
	m = CombatMemory{}
	if calls := castCalls(view, &m); len(calls) != 0 {
		t.Fatalf("defensive cast with no hostile in reach: %+v", calls)
	}
}

func TestCastOnePerCasterHealFirst(t *testing.T) {
	view := castFight(nil)
	view.Defenders[1].HealthFraction = domain.Known(0.3)
	var m CombatMemory
	only(t, castCalls(view, &m), "Painblock")
	// The heal is marked cooling: the next stop's cast is the stun, never the
	// unclassified Skip.
	view.Tick = 10
	only(t, castCalls(view, &m), "Stun")
}

func TestCastHolds(t *testing.T) {
	cases := []struct {
		name   string
		mutate func(*RoyaltyFacts)
		clear  bool
		hunt   bool
	}{
		{name: "psyfocus short", mutate: func(f *RoyaltyFacts) {
			f.Casters["d1"] = PsycasterState{Psyfocus: domain.Known(0.05), Entropy: domain.Known(0.0), EntropyMax: domain.Known(100.0)}
		}},
		{name: "entropy would overflow", mutate: func(f *RoyaltyFacts) {
			f.Casters["d1"] = PsycasterState{Psyfocus: domain.Known(0.8), Entropy: domain.Known(95.0), EntropyMax: domain.Known(100.0)}
		}},
		{name: "psyfocus unread", mutate: func(f *RoyaltyFacts) {
			f.Casters["d1"] = PsycasterState{Entropy: domain.Known(0.0), EntropyMax: domain.Known(100.0)}
		}},
		{name: "entropy unread", mutate: func(f *RoyaltyFacts) { f.Casters["d1"] = PsycasterState{Psyfocus: domain.Known(0.8)} }},
		{name: "caster state absent", mutate: func(f *RoyaltyFacts) { delete(f.Casters, "d1") }},
		{name: "on cooldown", mutate: func(f *RoyaltyFacts) {
			for i := range f.Psycasts["d1"] {
				f.Psycasts["d1"][i].CooldownRemaining = domain.Known(300)
			}
		}},
		{name: "cooldown unread", mutate: func(f *RoyaltyFacts) {
			for i := range f.Psycasts["d1"] {
				f.Psycasts["d1"][i].CooldownRemaining = domain.Fact[int]{}
			}
		}},
		{name: "cost unread", mutate: func(f *RoyaltyFacts) {
			for i := range f.Psycasts["d1"] {
				f.Psycasts["d1"][i].PsyfocusCost = domain.Fact[float64]{}
			}
		}},
		{name: "target kind unclassified", mutate: func(f *RoyaltyFacts) {
			for i := range f.Psycasts["d1"] {
				f.Psycasts["d1"][i].Target = ""
			}
		}},
		{name: "royalty unread", clear: true},
		{name: "hunt", hunt: true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			view := castFight(c.mutate)
			view.Defenders[1].HealthFraction = domain.Known(0.3)
			if c.clear {
				view.Royalty = domain.Fact[RoyaltyFacts]{}
			}
			view.Hunt = c.hunt
			var m CombatMemory
			if calls := castCalls(view, &m); len(calls) != 0 {
				t.Fatalf("calls %+v", calls)
			}
		})
	}
}

func TestCastDownedCasterHolds(t *testing.T) {
	view := castFight(nil)
	view.Pawns[0].Downed = true
	var m CombatMemory
	if calls := castCalls(view, &m); len(calls) != 0 {
		t.Fatalf("calls %+v", calls)
	}
}

func TestCastCooldownAndSpendCarryAcrossStopsOnStaleRead(t *testing.T) {
	view := castFight(func(f *RoyaltyFacts) {
		keep(f, "Stun")
		f.Casters["d1"] = PsycasterState{Psyfocus: domain.Known(0.15), Entropy: domain.Known(0.0), EntropyMax: domain.Known(100.0)}
	})
	var m CombatMemory
	only(t, castCalls(view, &m), "Stun")
	view.Tick = 700 // cooled down, but the stale read still shows 0.15 psyfocus: 0.05 left is short
	if calls := castCalls(view, &m); len(calls) != 0 {
		t.Fatalf("recast past the remembered spend: %+v", calls)
	}
	view = castFight(func(f *RoyaltyFacts) { keep(f, "Stun") })
	m = CombatMemory{Casts: []CastMark{{Pawn: "d1", Def: "Stun", Tick: 100, Cooldown: 600}}}
	view.Tick = 300
	if calls := castCalls(view, &m); len(calls) != 0 {
		t.Fatalf("recast inside its cooldown: %+v", calls)
	}
}

func TestCastRidesDecideCombatWithoutIssue(t *testing.T) {
	view := castFight(func(f *RoyaltyFacts) { keep(f, "Stun") })
	orders, _, next := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	casts := 0
	for _, o := range orders {
		if o.Kind == OrderCast {
			casts++
		}
	}
	if casts != 1 || len(next.Casts) != 1 {
		t.Fatalf("casts %d, memory %+v, orders %+v", casts, next.Casts, orders)
	}
	for _, issued := range next.Issued {
		if issued.Kind == OrderCast {
			t.Fatalf("cast recorded as an issued order: %+v", issued)
		}
	}
}
