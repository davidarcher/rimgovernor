package postmortem

import (
	"fmt"
	"strings"
)

// --- stage graph -------------------------------------------------------

// stageGraph reads the run's declared stages (result.json "stages", #329)
// as a graph: the bundle the run opened on (staged_from), each stage's
// outcome (hit, captured, uncached, failed) and the stage a -through run
// ended on (staged_through, #527). A run whose failure sits past a hit
// stage never replayed that stage's block, which is the first thing to
// know before reading the colony.
func stageGraph(report map[string]any) Section {
	s := Section{Name: "stage graph"}
	var rows []map[string]any
	switch v := report["stages"].(type) {
	case []map[string]any:
		rows = v
	case []any:
		for _, r := range v {
			if row, ok := r.(map[string]any); ok {
				rows = append(rows, row)
			}
		}
	}
	if len(rows) == 0 {
		s.Note = "result.json has no stages (the case declares none, or the run ended before its first)"
		return s
	}
	if from, ok := report["staged_from"].(map[string]any); ok {
		s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("opened on stage %s (captured %s, tick %v)", asString(from["stage"]), asString(from["captured_at"]), from["tick"]), Evidence: "result.json staged_from"})
	} else {
		s.Lines = append(s.Lines, Line{Text: "opened fresh: every stage block ran", Evidence: "result.json staged_from"})
	}
	var chain []string
	for _, row := range rows {
		name, outcome := asString(row["name"]), asString(row["outcome"])
		if outcome == "" {
			outcome = "not reached"
		}
		node := name + ":" + outcome
		if ms := wallMs(row["wall_ms"]); ms > 0 && outcome != "hit" {
			node += fmt.Sprintf(" %ds", ms/1000)
		}
		chain = append(chain, node)
	}
	s.Lines = append(s.Lines, Line{Text: strings.Join(chain, " -> "), Evidence: "result.json stages"})
	if through, ok := report["staged_through"].(map[string]any); ok {
		s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("ended after stage %s (%s): a -through run, the rest of the chain is another run's", asString(through["stage"]), asString(through["outcome"])), Evidence: "result.json staged_through"})
	}
	return s
}

// wallMs reads a wall_ms value from a live report (int64) or decoded JSON
// (float64).
func wallMs(v any) int64 {
	switch n := v.(type) {
	case int64:
		return n
	case float64:
		return int64(n)
	}
	return 0
}
