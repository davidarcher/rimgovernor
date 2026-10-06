// Command recoveryshadow summarizes the recovery shadow comparison of one
// flight recording (#2300, epic #2291): the recovery_shadow rows (what the
// old clearance path decided next to what the recovery queue decided) and the
// recovery_batch rows (#2298), so one release can be reviewed for unexplained
// divergences before execution switches (#2301).
//
//	go run ./cmd/recoveryshadow <flight.jsonl | output dir>
package main

import (
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// maxExamples bounds the unexplained examples the report prints per class.
const maxExamples = 5

// Example is one unexplained divergence with where it happened.
type Example struct {
	Tick           int64
	ID, Old, Queue string
}

// ClassCount is one divergence class's tally.
type ClassCount struct {
	Class    string
	Expected bool
	Count    int
	Examples []Example
}

// Summary is the report: shadow rows by verdict, divergences by class
// (expected ones tagged), and the recovery_batch rows by verdict and reason.
type Summary struct {
	Reviews  int
	Verdicts map[string]int
	Classes  []ClassCount
	Batches  map[string]int
}

// Unexplained counts the divergences no expected class accounts for.
func (s Summary) Unexplained() int {
	n := 0
	for _, c := range s.Classes {
		if !c.Expected {
			n += c.Count
		}
	}
	return n
}

// Summarize tallies the recovery_shadow and recovery_batch rows.
func Summarize(rows []nativeaccept.FlightRow) Summary {
	s := Summary{Verdicts: map[string]int{}, Batches: map[string]int{}}
	byClass := map[string]*ClassCount{}
	for _, row := range rows {
		verdict, _ := row.Payload["verdict"].(string)
		reason, _ := row.Payload["reason"].(string)
		switch row.Kind {
		case "recovery_batch":
			s.Batches[verdict+"/"+reason]++
		case "recovery_shadow":
			s.Reviews++
			s.Verdicts[verdict]++
			attrs, _ := row.Payload["attrs"].(map[string]any)
			list, _ := attrs["divergences"].([]any)
			for _, item := range list {
				d, _ := item.(map[string]any)
				class, _ := d["class"].(string)
				expected, _ := d["expected"].(bool)
				c := byClass[class]
				if c == nil {
					c = &ClassCount{Class: class, Expected: expected}
					byClass[class] = c
				}
				c.Count++
				if !expected && len(c.Examples) < maxExamples {
					id, _ := d["id"].(string)
					before, _ := d["old"].(string)
					after, _ := d["queue"].(string)
					c.Examples = append(c.Examples, Example{Tick: row.Tick, ID: id, Old: before, Queue: after})
				}
			}
		}
	}
	for _, c := range byClass {
		s.Classes = append(s.Classes, *c)
	}
	sort.Slice(s.Classes, func(i, j int) bool {
		a, b := s.Classes[i], s.Classes[j]
		if a.Expected != b.Expected {
			return !a.Expected
		}
		return a.Class < b.Class
	})
	return s
}

func sortedKeys(m map[string]int) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}

// Print renders the summary as plain text.
func (s Summary) Print(w io.Writer) {
	fmt.Fprintf(w, "recovery shadow: %d reviews, %d unexplained divergences (tier 1 is not supplied to the queue: room obstructions are not ranked first)\n", s.Reviews, s.Unexplained())
	for _, v := range sortedKeys(s.Verdicts) {
		fmt.Fprintf(w, "  verdict %-9s %d\n", v, s.Verdicts[v])
	}
	for _, c := range s.Classes {
		tag := "UNEXPLAINED"
		if c.Expected {
			tag = "expected"
		}
		fmt.Fprintf(w, "  %-11s %-18s %d\n", tag, c.Class, c.Count)
		for _, e := range c.Examples {
			fmt.Fprintf(w, "      tick %d %s: old %s, queue %s\n", e.Tick, e.ID, e.Old, e.Queue)
		}
	}
	if len(s.Batches) > 0 {
		fmt.Fprintln(w, "recovery batch rows:")
		for _, k := range sortedKeys(s.Batches) {
			fmt.Fprintf(w, "  %-22s %d\n", k, s.Batches[k])
		}
	}
}

func main() {
	if len(os.Args) != 2 {
		fmt.Fprintln(os.Stderr, "usage: recoveryshadow <flight.jsonl | output dir>")
		os.Exit(2)
	}
	path := os.Args[1]
	if info, err := os.Stat(path); err == nil && info.IsDir() {
		path = nativeaccept.FlightRecorderPath(path)
	}
	rows, err := nativeaccept.ReadFlight(filepath.Clean(path))
	if err != nil {
		fmt.Fprintln(os.Stderr, "recoveryshadow:", err)
		os.Exit(1)
	}
	Summarize(rows).Print(os.Stdout)
}
