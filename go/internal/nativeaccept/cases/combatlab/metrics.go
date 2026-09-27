package combatlab

import (
	"bufio"
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// Bundle files a combat-fixture run records (#855). ReadsFile holds every
// raw test/lab_stage read reply, one JSON object per line, in tick order;
// MetricsFile is the aggregate; ServiceLogFile, when a run served the
// planner, carries the scheduler's combat_stop lines (#849).
const (
	ReadsFile      = "combat_reads.jsonl"
	MetricsFile    = "combat_metrics.json"
	ServiceLogFile = "service.log"
)

// Metrics is one combat-fixture run: evidence, not a gate. The committed
// baselines under baselines/ are this shape.
type Metrics struct {
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

	// OrdersIssued and OrdersRefused count combat orders from the service
	// log; zero until the combat order op (#850) logs them.
	OrdersIssued  int `json:"ordersIssued"`
	OrdersRefused int `json:"ordersRefused"`
	// StepLatencyP95Ms is the p95 of combat_stop resume_latency_ms (#849),
	// -1 when no planner served the run.
	StepLatencyP95Ms int64 `json:"stepLatencyP95Ms"`
}

// Hit is one damage ledger row of a read.
type Hit struct {
	Tick           int     `json:"tick"`
	Victim         string  `json:"victim"`
	VictimSide     string  `json:"victimSide"`
	Instigator     string  `json:"instigator"`
	InstigatorSide string  `json:"instigatorSide"`
	Dealt          float64 `json:"dealt"`
}

// Aggregate folds a run's read replies (tick order) into metrics. staged
// are the pawn ids and sides the staging placed: a staged pawn missing
// from a later read left the map, which for a hostile is fled.
func Aggregate(fixture string, staged map[string]string, reads []map[string]any) Metrics {
	m := Metrics{Fixture: fixture, FirstContactTick: -1, ResolvedTick: -1, ContactToResolution: -1, StepLatencyP95Ms: -1}
	if len(reads) == 0 {
		return m
	}
	start := int(na.AsNumber(reads[0]["tick"]))
	for _, read := range reads {
		tick := int(na.AsNumber(read["tick"]))
		m.Ticks = tick - start
		pawns := map[string]map[string]any{}
		for _, r := range na.AsSlice(read["pawns"]) {
			row, _ := na.AsMap(r)
			pawns[na.AsString(row["id"])] = row
		}
		if m.ResolvedTick < 0 {
			if side := resolved(staged, pawns); side != "" {
				m.ResolvedTick, m.Winner = tick, side
			}
		}
	}
	last := reads[len(reads)-1]
	final := map[string]map[string]any{}
	for _, r := range na.AsSlice(last["pawns"]) {
		row, _ := na.AsMap(r)
		final[na.AsString(row["id"])] = row
	}
	for id, side := range staged {
		row, present := final[id]
		dead, _ := na.AsBool(row["dead"])
		downed, _ := na.AsBool(row["downed"])
		fleeing, _ := na.AsBool(row["fleeing"])
		switch {
		case side == Colonist && dead:
			m.ColonistDeaths++
		case side == Colonist && downed:
			m.ColonistDowns++
		case side == Hostile && dead:
			m.EnemyDeaths++
		case side == Hostile && downed:
			m.EnemyDowns++
		case side == Hostile && (!present || fleeing):
			m.EnemyFled++
		}
	}
	// The ledger is cumulative since the staging: the last read holds it all.
	for _, r := range na.AsSlice(last["damage"]) {
		row, _ := na.AsMap(r)
		h := Hit{Tick: int(na.AsNumber(row["tick"])), VictimSide: na.AsString(row["victimSide"]), InstigatorSide: na.AsString(row["instigatorSide"]), Dealt: na.AsNumber(row["dealt"])}
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
	m.DamageDropped = max(0, int(na.AsNumber(last["damageDropped"])))
	if m.FirstContactTick >= 0 && m.ResolvedTick >= m.FirstContactTick {
		m.ContactToResolution = m.ResolvedTick - m.FirstContactTick
	}
	return m
}

// resolved names the side left standing once every staged pawn of the
// other side is down, dead or off the map; "" while both fight.
func resolved(staged map[string]string, pawns map[string]map[string]any) string {
	standing := map[string]int{}
	for id, side := range staged {
		row, ok := pawns[id]
		if !ok {
			continue
		}
		dead, _ := na.AsBool(row["dead"])
		downed, _ := na.AsBool(row["downed"])
		fleeing, _ := na.AsBool(row["fleeing"])
		// An undrafted colonist flees raiders and comes back; only a
		// fleeing hostile has left the fight.
		if !dead && !downed && !(fleeing && side == Hostile) {
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

var (
	resumeLatency = regexp.MustCompile(`\bcombat_stop\b.*\bresume_latency_ms=(\d+)`)
	// The combat order op (#850) logs combat_order lines with outcome=.
	orderLine = regexp.MustCompile(`\bcombat_order\b.*\boutcome=(\w+)`)
)

// ScanServiceLog adds the service log's combat orders and step latency.
func (m *Metrics) ScanServiceLog(path string) error {
	f, err := os.Open(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		return err
	}
	defer f.Close()
	var latencies []int64
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 1<<20), 1<<24)
	for sc.Scan() {
		line := sc.Text()
		if g := resumeLatency.FindStringSubmatch(line); g != nil {
			v, _ := strconv.ParseInt(g[1], 10, 64)
			latencies = append(latencies, v)
		}
		if g := orderLine.FindStringSubmatch(line); g != nil {
			if g[1] == "refused" {
				m.OrdersRefused++
			} else {
				m.OrdersIssued++
			}
		}
	}
	if len(latencies) > 0 {
		sort.Slice(latencies, func(i, j int) bool { return latencies[i] < latencies[j] })
		m.StepLatencyP95Ms = latencies[(len(latencies)*95+99)/100-1]
	}
	return sc.Err()
}

// StagedSides maps a staging's pawn ids to their sides.
func StagedSides(s Staged) map[string]string {
	out := map[string]string{}
	for _, id := range s.Colonists() {
		out[id] = Colonist
	}
	for _, id := range s.Hostiles() {
		out[id] = Hostile
	}
	return out
}

// AggregateBundle recomputes a recorded run's metrics from its bundle
// directory: the reads file, whose first line (the read right after
// staging) carries staged {id: side}, and the service log when present.
func AggregateBundle(dir, fixture string) (Metrics, error) {
	data, err := os.ReadFile(filepath.Join(dir, ReadsFile))
	if err != nil {
		return Metrics{}, err
	}
	var reads []map[string]any
	sc := bufio.NewScanner(bytes.NewReader(data))
	sc.Buffer(make([]byte, 1<<20), 1<<26)
	for sc.Scan() {
		if len(sc.Bytes()) == 0 {
			continue
		}
		var row map[string]any
		if err := json.Unmarshal(sc.Bytes(), &row); err != nil {
			return Metrics{}, fmt.Errorf("%s: %w", ReadsFile, err)
		}
		reads = append(reads, row)
	}
	if err := sc.Err(); err != nil {
		return Metrics{}, err
	}
	if len(reads) == 0 {
		return Metrics{}, fmt.Errorf("%s: no reads", ReadsFile)
	}
	staged := map[string]string{}
	first, _ := na.AsMap(reads[0]["staged"])
	for id, side := range first {
		staged[id] = na.AsString(side)
	}
	m := Aggregate(fixture, staged, reads)
	return m, m.ScanServiceLog(filepath.Join(dir, ServiceLogFile))
}
