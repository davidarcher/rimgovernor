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

// MetricsVersion is the Metrics schema version: bump it when a field
// changes meaning, and re-record the baselines.
const MetricsVersion = 1

// Metrics is one combat-fixture run: evidence, not a gate. The committed
// baselines under baselines/ are this shape.
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

	// OrdersIssued and OrdersRefused count combat orders from the service
	// log: combat_order lines (#850) and the routine defense planner's
	// actions, issued once one completes, refused when one only ever
	// carried a refusal.
	OrdersIssued  int `json:"ordersIssued"`
	OrdersRefused int `json:"ordersRefused"`
	// StepLatencyP95Ms is the p95 of the combat stops' resume_latency_ms
	// (#849), -1 when no planner served the run.
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
	m := Metrics{Version: MetricsVersion, Fixture: fixture, FirstContactTick: -1, ResolvedTick: -1, ContactToResolution: -1, StepLatencyP95Ms: -1}
	if len(reads) == 0 {
		return m
	}
	start := int(na.AsNumber(reads[0]["tick"]))
	for i, read := range reads {
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
				if i > 0 {
					m.ResolvedTick = decisiveHit(staged, pawns, side, na.AsSlice(read["damage"]), tick)
				}
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

// decisiveHit narrows a resolution first seen on a read at tick to the
// ledger hit that downed or killed the loser's last standing pawn: a
// served run reads only once after its window. A loser that left the map
// or went down without a hit (bleeding) keeps the read's tick.
func decisiveHit(staged map[string]string, pawns map[string]map[string]any, winner string, damage []any, tick int) int {
	first := map[string]int{}
	for _, r := range damage {
		row, _ := na.AsMap(r)
		downed, _ := na.AsBool(row["downed"])
		dead, _ := na.AsBool(row["dead"])
		id, at := na.AsString(row["victim"]), int(na.AsNumber(row["tick"]))
		if (downed || dead) && at <= tick {
			if prev, ok := first[id]; !ok || at < prev {
				first[id] = at
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
	resumeLatency = regexp.MustCompile(`(\bcombat_stop\b|combat window stopped).*\bresume_latency_ms=(\d+)`)
	// The combat order op (#850) logs combat_order lines with outcome=.
	orderLine = regexp.MustCompile(`\bcombat_order\b.*\boutcome=(\w+)`)
	// The routine defense planner's actions (drafts, holds) report through
	// the worker.
	defenseAction = regexp.MustCompile(`worker outcome action=(routine-defense-\S+) .*\bstage_after=(\w+).*\brefused=\[([^\]]*)\]`)
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
	actions := map[string]string{}
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 1<<20), 1<<24)
	for sc.Scan() {
		line := sc.Text()
		if g := resumeLatency.FindStringSubmatch(line); g != nil {
			v, _ := strconv.ParseInt(g[2], 10, 64)
			latencies = append(latencies, v)
		}
		if g := defenseAction.FindStringSubmatch(line); g != nil {
			switch {
			case g[2] == "completed":
				actions[g[1]] = "issued"
			case g[3] != "" && actions[g[1]] == "":
				actions[g[1]] = "refused"
			}
		}
		if g := orderLine.FindStringSubmatch(line); g != nil {
			if g[1] == "refused" {
				m.OrdersRefused++
			} else {
				m.OrdersIssued++
			}
		}
	}
	for _, outcome := range actions {
		if outcome == "issued" {
			m.OrdersIssued++
		} else {
			m.OrdersRefused++
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
	// Manhunters and mechanoids are the enemy side of their fixtures (#1146).
	for _, ids := range [][]string{s.Hostiles(), s.Manhunters(), s.Mechs()} {
		for _, id := range ids {
			out[id] = Hostile
		}
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
