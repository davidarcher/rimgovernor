// Package combatmetrics scores a combat fight from plain per-tick rows, so
// the real-game combat lab and the simulator report fights with one
// definition. It knows nothing of the bridge read format or the logs.
package combatmetrics

import "math"

// Sides a staged pawn fights on.
const (
	Colonist = "colonist"
	Hostile  = "hostile"
)

// Version is the Metrics schema version: bump it when a field changes
// meaning, and re-record the baselines.
const Version = 1

// Metrics is one combat-fixture run: evidence, not a gate. The committed
// combat lab baselines are this shape.
type Metrics struct {
	Version int    `json:"version"`
	Fixture string `json:"fixture"`
	Ticks   int    `json:"ticks"` // game ticks run after staging

	ColonistDamage float64 `json:"colonistDamage"`
	ColonistDowns  int     `json:"colonistDowns"`
	ColonistDeaths int     `json:"colonistDeaths"`
	EnemyDowns     int     `json:"enemyDowns"`
	EnemyDeaths    int     `json:"enemyDeaths"`
	EnemyFled      int     `json:"enemyFled"`

	// FirstContactTick is the first hit either way, ResolvedTick the read
	// where one side was all down, dead or gone; ContactToResolution is
	// their difference, -1 when the run ended unresolved or without contact.
	FirstContactTick    int    `json:"firstContactTick"`
	ResolvedTick        int    `json:"resolvedTick"`
	ContactToResolution int    `json:"contactToResolutionTicks"`
	Winner              string `json:"winner"` // colonist, hostile or "" unresolved

	FriendlyFireHits int `json:"friendlyFireHits"`
	DamageDropped    int `json:"damageDropped,omitempty"`

	// OrdersIssued and OrdersRefused count combat orders from the log
	// rows: combat_order rows and the routine defense planner's
	// actions, issued once one completes, refused when one only ever
	// carried a refusal.
	OrdersIssued  int `json:"ordersIssued"`
	OrdersRefused int `json:"ordersRefused"`
	// StepLatencyP95Ms is the p95 of the combat stops' resume_latency_ms,
	// -1 when no planner served the run.
	StepLatencyP95Ms int64 `json:"stepLatencyP95Ms"`
}

// Pawn is one pawn's state in a read.
type Pawn struct {
	ID      string
	Dead    bool
	Downed  bool
	Fleeing bool
}

// Hit is one damage ledger row of a read. Downed and Dead say the hit
// left the victim down or dead.
type Hit struct {
	Tick           int
	Victim         string
	VictimSide     string
	Instigator     string
	InstigatorSide string
	Dealt          float64
	Downed         bool
	Dead           bool
}

// Read is one read of the fight: the pawns on the map, the damage ledger
// cumulative since the staging, and the ledger rows dropped for size.
type Read struct {
	Tick          int
	Pawns         []Pawn
	Damage        []Hit
	DamageDropped int
}

// New is the metrics of a run that has no reads yet.
func New(fixture string) Metrics {
	return Metrics{Version: Version, Fixture: fixture, FirstContactTick: -1, ResolvedTick: -1, ContactToResolution: -1, StepLatencyP95Ms: -1}
}

func index(pawns []Pawn) map[string]Pawn {
	out := make(map[string]Pawn, len(pawns))
	for _, p := range pawns {
		out[p.ID] = p
	}
	return out
}

// Aggregate folds a run's reads (tick order) into metrics. staged are the
// pawn ids and sides the staging placed: a staged pawn missing from a later
// read left the map, which for a hostile is fled.
func Aggregate(fixture string, staged map[string]string, reads []Read) Metrics {
	m := New(fixture)
	if len(reads) == 0 {
		return m
	}
	start := reads[0].Tick
	for i, read := range reads {
		m.Ticks = read.Tick - start
		// The staging read resolves nothing: raiders still in their drop pods
		// are not on the map yet and would read as a side already gone.
		if m.ResolvedTick < 0 && i > 0 {
			pawns := index(read.Pawns)
			if side := resolved(staged, pawns); side != "" {
				m.ResolvedTick, m.Winner = decisiveHit(staged, pawns, side, read.Damage, read.Tick), side
			}
		}
	}
	last := reads[len(reads)-1]
	final := index(last.Pawns)
	for id, side := range staged {
		row, present := final[id]
		switch {
		case side == Colonist && row.Dead:
			m.ColonistDeaths++
		case side == Colonist && row.Downed:
			m.ColonistDowns++
		case side == Hostile && row.Dead:
			m.EnemyDeaths++
		case side == Hostile && row.Downed:
			m.EnemyDowns++
		case side == Hostile && (!present || row.Fleeing):
			m.EnemyFled++
		}
	}
	// The ledger is cumulative since the staging: the last read holds it all.
	for _, h := range last.Damage {
		if m.FirstContactTick < 0 || h.Tick < m.FirstContactTick {
			m.FirstContactTick = h.Tick
		}
		if h.VictimSide == Colonist {
			m.ColonistDamage += h.Dealt
			if h.InstigatorSide == Colonist {
				m.FriendlyFireHits++
			}
		}
	}
	m.ColonistDamage = math.Round(m.ColonistDamage*10) / 10
	m.DamageDropped = max(0, last.DamageDropped)
	if m.FirstContactTick >= 0 && m.ResolvedTick >= m.FirstContactTick {
		m.ContactToResolution = m.ResolvedTick - m.FirstContactTick
	}
	return m
}

// decisiveHit narrows a resolution first seen on a read at tick to the
// ledger hit that downed or killed the loser's last standing pawn: a
// served run reads only once after its window. A loser that left the map
// or went down without a hit (bleeding) keeps the read's tick.
func decisiveHit(staged map[string]string, pawns map[string]Pawn, winner string, damage []Hit, tick int) int {
	first := map[string]int{}
	for _, h := range damage {
		if (h.Downed || h.Dead) && h.Tick <= tick {
			if prev, ok := first[h.Victim]; !ok || h.Tick < prev {
				first[h.Victim] = h.Tick
			}
		}
	}
	last := -1
	for id, side := range staged {
		if side == winner {
			continue
		}
		at, ok := first[id]
		if _, present := pawns[id]; !ok || !present {
			return tick
		}
		last = max(last, at)
	}
	if last < 0 {
		return tick
	}
	return last
}

// resolved names the side left standing once every staged pawn of the
// other side is down, dead or off the map; "" while both fight.
func resolved(staged map[string]string, pawns map[string]Pawn) string {
	standing := map[string]int{}
	for id, side := range staged {
		p, ok := pawns[id]
		if !ok {
			continue
		}
		// An undrafted colonist flees raiders and comes back; only a
		// fleeing hostile has left the fight.
		if !p.Dead && !p.Downed && !(p.Fleeing && side == Hostile) {
			standing[side]++
		}
	}
	switch {
	case standing[Hostile] == 0:
		return Colonist
	case standing[Colonist] == 0:
		return Hostile
	}
	return ""
}
