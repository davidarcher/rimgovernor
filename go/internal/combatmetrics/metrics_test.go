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

func TestAggregateNoReads(t *testing.T) {
	if m := Aggregate("x", nil, nil); m != New("x") {
		t.Fatalf("%+v", m)
	}
}
