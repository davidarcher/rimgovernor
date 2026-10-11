package main

import (
	"encoding/json"
	"fmt"
	"io"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/logview"
)

const phasesUsage = "usage: rimgovernor phases [--json] (--profile <dir> | <absolute logs.jsonl>)"

// phases is the read-only throughput sampler: it aggregates the OTLP log
// ring's per-call phase timings (see bridge.SummarizePhases) without touching
// the game or the running controller. The ring is <profile>/flight/otlp/logs.jsonl
// (beside the path given to serve --flight-recorder); rotated segments are
// read too.
func phases(args []string, out, errors io.Writer) int {
	asJSON := false
	profile, file := "", ""
	for i := 0; i < len(args); i++ {
		switch arg := args[i]; {
		case arg == "--json":
			asJSON = true
		case arg == "--profile" && i+1 < len(args):
			i++
			profile = args[i]
		case file == "" && len(arg) > 0 && arg[0] != '-':
			file = arg
		default:
			fmt.Fprintln(errors, phasesUsage)
			return 2
		}
	}
	if (profile == "") == (file == "") || (file != "" && !filepath.IsAbs(file)) {
		fmt.Fprintln(errors, phasesUsage)
		return 2
	}
	records, err := bridge.ReadOTLPTimeline(logview.Path(profile, file))
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
