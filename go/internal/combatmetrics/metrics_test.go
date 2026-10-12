package combatmetrics

import "testing"

// A fight scored from plain rows: the hostile's last pawn goes down on a
// ledger hit, which narrows the resolution to that hit's tick.
func TestAggregateResolvesOnDecisiveHit(t *testing.T) {
	staged := map[string]string{"c": Colonist, "h": Hostile}
	m := Aggregate("x", staged, []Read{
		{Tick: 100, Pawns: []Pawn{{ID: "c"}, {ID: "h"}}},
		{Tick: 600, Pawns: []Pawn{{ID: "c"}, {ID: "h", Downed: true}}, Damage: []Hit{
			{Tick: 300, Victim: "c", VictimSide: Colonist, InstigatorSide: Hostile, Dealt: 2.04},
			{Tick: 450, Victim: "h", VictimSide: Hostile, InstigatorSide: Colonist, Dealt: 9, Downed: true},
		}},
	})
	if m.Winner != Colonist || m.ResolvedTick != 450 || m.FirstContactTick != 300 || m.ContactToResolution != 150 ||
		m.EnemyDowns != 1 || m.ColonistDamage != 2 || m.Ticks != 500 {
		t.Fatalf("%+v", m)
	}
}

// The native read omits a dead hostile; its dead:true ledger hit makes it a
// death, while a hostile absent without one is still fled.
func TestAggregateLedgerDeadHostileIsNotFled(t *testing.T) {
	staged := map[string]string{"c": Colonist, "dead": Hostile, "gone": Hostile}
	m := Aggregate("x", staged, []Read{
		{Tick: 100, Pawns: []Pawn{{ID: "c"}, {ID: "dead"}, {ID: "gone"}}},
		{Tick: 600, Pawns: []Pawn{{ID: "c"}}, Damage: []Hit{
			{Tick: 450, Victim: "dead", VictimSide: Hostile, InstigatorSide: Colonist, Dealt: 9, Dead: true},
		}},
	})
	if m.EnemyDeaths != 1 || m.EnemyFled != 1 || m.Winner != Colonist {
		t.Fatalf("%+v", m)
	}
}

// The native read omits a dead colonist as well (#2989): the ledger's dead:true
// hit makes it a death rather than a colonist who simply is not there, and a
// colonist still lying downed in the read stays a down.
func TestAggregateLedgerDeadColonistIsADeath(t *testing.T) {
	staged := map[string]string{"dead": Colonist, "down": Colonist, "h": Hostile}
	m := Aggregate("x", staged, []Read{
		{Tick: 100, Pawns: []Pawn{{ID: "dead"}, {ID: "down"}, {ID: "h"}}},
		{Tick: 600, Pawns: []Pawn{{ID: "down", Downed: true}, {ID: "h"}}, Damage: []Hit{
			{Tick: 300, Victim: "dead", VictimSide: Colonist, InstigatorSide: Hostile, Dealt: 11, Downed: true, Dead: true},
			{Tick: 400, Victim: "down", VictimSide: Colonist, InstigatorSide: Hostile, Dealt: 22, Downed: true},
		}},
	})
	if m.ColonistDeaths != 1 || m.ColonistDowns != 1 || m.Winner != Hostile {
		t.Fatalf("%+v", m)
	}
}

func TestAggregateNoReads(t *testing.T) {
	if m := Aggregate("x", nil, nil); m != New("x") {
		t.Fatalf("%+v", m)
	}
}
