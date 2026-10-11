package combatlab

import (
	"bufio"
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// Bundle files a combat-fixture run records. ReadsFile holds every
// raw test/lab_stage read reply, one JSON object per line, in tick order;
// MetricsFile is the aggregate; CombatRowsFile, when a run served the planner,
// carries its clock_stop, combat_order and dispatch rows.
const (
	ReadsFile      = "combat_reads.jsonl"
	MetricsFile    = "combat_metrics.json"
	CombatRowsFile = "combat_rows.jsonl"
)

// Aggregate scores a run's raw test/lab_stage read replies (tick order)
// with the shared combatmetrics scoring.
func Aggregate(fixture string, staged map[string]string, reads []map[string]any) combatmetrics.Metrics {
	rows := make([]combatmetrics.Read, len(reads))
	for i, read := range reads {
		rows[i] = readRows(read)
	}
	return combatmetrics.Aggregate(fixture, staged, rows)
}

// readRows adapts one raw read reply to the scoring rows.
func readRows(read map[string]any) combatmetrics.Read {
	out := combatmetrics.Read{Tick: int(na.AsNumber(read["tick"])), DamageDropped: int(na.AsNumber(read["damageDropped"]))}
	for _, r := range na.AsSlice(read["pawns"]) {
		row, _ := na.AsMap(r)
		dead, _ := na.AsBool(row["dead"])
		downed, _ := na.AsBool(row["downed"])
		fleeing, _ := na.AsBool(row["fleeing"])
		out.Pawns = append(out.Pawns, combatmetrics.Pawn{ID: na.AsString(row["id"]), Dead: dead, Downed: downed, Fleeing: fleeing})
	}
	for _, r := range na.AsSlice(read["damage"]) {
		row, _ := na.AsMap(r)
		downed, _ := na.AsBool(row["downed"])
		dead, _ := na.AsBool(row["dead"])
		out.Damage = append(out.Damage, combatmetrics.Hit{
			Tick: int(na.AsNumber(row["tick"])), Victim: na.AsString(row["victim"]), VictimSide: na.AsString(row["victimSide"]),
			Instigator: na.AsString(row["instigator"]), InstigatorSide: na.AsString(row["instigatorSide"]),
			Dealt: na.AsNumber(row["dealt"]), Downed: downed, Dead: dead,
		})
	}
	return out
}

// ScanLogs adds the rows' combat orders and step latency: each
// combat stop's resume_latency_ms, each combat_order row's verdict, and the
// routine defense planner's actions (drafts, holds) from the worker's
// outcome rows, issued once one completes, refused when one only ever
// refused. A missing file is a run no planner served.
func ScanLogs(m *combatmetrics.Metrics, path string) error {
	rows, err := na.ReadLogs(path)
	if err != nil {
		return err
	}
	var latencies []int64
	actions := map[string]string{}
	for _, row := range rows {
		f := row.Fields()
		switch {
		case row.Kind == "clock_stop":
			if v, ok := f["resume_latency_ms"]; ok {
				latencies = append(latencies, int64(na.AsNumber(v)))
			}
		case row.Kind == "combat_order":
			if na.AsString(f["verdict"]) == "refused" {
				m.OrdersRefused++
			} else {
				m.OrdersIssued++
			}
		case row.Kind == "dispatch":
			action := na.AsString(f["target"])
			if !strings.HasPrefix(action, "routine-defense-") {
				continue
			}
			verdict := na.AsString(f["verdict"])
			switch {
			case na.AsString(f["stage_after"]) == "completed" || verdict == "completed":
				actions[action] = "issued"
			case (len(na.AsSlice(f["refused"])) > 0 || verdict == "refused") && actions[action] == "":
				actions[action] = "refused"
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
	return nil
}

// CombatRow reports whether a row is one ScanLogs reads: the
// bundle keeps only these.
func CombatRow(row na.LogRow) bool {
	return row.Kind == "combat_order" || row.Kind == "clock_stop" || row.Kind == "dispatch" || row.Kind == "clock_step" || row.Kind == "planner_step" && na.AsString(row.Fields()["scope"]) == "immediate"
}

// StagedSides maps a staging's pawn ids to their sides.
func StagedSides(s Staged) map[string]string {
	out := map[string]string{}
	for _, id := range s.Colonists() {
		out[id] = Colonist
	}
	// Manhunters and mechanoids are the enemy side of their fixtures.
	for _, ids := range [][]string{s.Hostiles(), s.Manhunters(), s.Mechs()} {
		for _, id := range ids {
			out[id] = Hostile
		}
	}
	return out
}

// AggregateBundle recomputes a recorded run's metrics from its bundle
// directory: the reads file, whose first line (the read right after
// staging) carries staged {id: side}, and the rows when present.
func AggregateBundle(dir, fixture string) (combatmetrics.Metrics, error) {
	data, err := os.ReadFile(filepath.Join(dir, ReadsFile))
	if err != nil {
		return combatmetrics.Metrics{}, err
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
			return combatmetrics.Metrics{}, fmt.Errorf("%s: %w", ReadsFile, err)
		}
		reads = append(reads, row)
	}
	if err := sc.Err(); err != nil {
		return combatmetrics.Metrics{}, err
	}
	if len(reads) == 0 {
		return combatmetrics.Metrics{}, fmt.Errorf("%s: no reads", ReadsFile)
	}
	staged := map[string]string{}
	first, _ := na.AsMap(reads[0]["staged"])
	for id, side := range first {
		staged[id] = na.AsString(side)
	}
	m := Aggregate(fixture, staged, reads)
	return m, ScanLogs(&m, filepath.Join(dir, CombatRowsFile))
}
