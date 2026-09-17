package main

import (
	"encoding/json"
	"fmt"
	"io"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// phases is the read-only throughput sampler: it aggregates a flight
// recorder's per-call phase timings (see bridge.SummarizePhases) without
// touching the game or the running controller. The recorder path is the one
// given to serve --flight-recorder; rotated segments are read too.
func phases(args []string, out, errors io.Writer) int {
	asJSON := false
	path := ""
	for _, arg := range args {
		switch {
		case arg == "--json":
			asJSON = true
		case path == "" && len(arg) > 0 && arg[0] != '-':
			path = arg
		default:
			fmt.Fprintln(errors, "usage: rimgovernor phases [--json] <flight-recorder.jsonl>")
			return 2
		}
	}
	if path == "" || !filepath.IsAbs(path) {
		fmt.Fprintln(errors, "usage: rimgovernor phases [--json] <absolute flight-recorder.jsonl>")
		return 2
	}
	records, err := bridge.ReadTimeline(path)
	if err != nil {
		fmt.Fprintf(errors, "phases: %v\n", err)
		return 1
	}
	summary := bridge.SummarizePhases(records)
	if asJSON {
		encoder := json.NewEncoder(out)
		encoder.SetIndent("", "  ")
		if err := encoder.Encode(summary); err != nil {
			fmt.Fprintf(errors, "phases: %v\n", err)
			return 1
		}
		return 0
	}
	bridge.WritePhaseReport(out, summary)
	return 0
}
