package main

import (
	"encoding/json"
	"fmt"
	"io"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/logview"
)

const traceUsage = "usage: rimgovernor trace [--json] (--profile <dir> | <absolute logs.jsonl>) [<trace_id>]"

// trace renders one trace of the OTLP sink as a span tree (spans from
// traces.jsonl with the log rows from logs.jsonl logged inside them, offsets,
// durations and statuses; a native call that never returned shows "(no reply
// recorded)"), or, without a trace id, lists the sink's traces so a reader can
// find the slow step. Read-only; rotated segments are read too. --json is
// jq-friendly: an array of traces (index) or one trace with its spans and rows.
func trace(args []string, out, errors io.Writer) int {
	asJSON := false
	profile := ""
	var positional []string
	for i := 0; i < len(args); i++ {
		switch arg := args[i]; {
		case arg == "--json":
			asJSON = true
		case arg == "--profile" && i+1 < len(args):
			i++
			profile = args[i]
		case len(arg) > 0 && arg[0] != '-':
			positional = append(positional, arg)
		default:
			fmt.Fprintln(errors, traceUsage)
			return 2
		}
	}
	// With --profile the positionals are the optional trace id; otherwise the
	// ring comes first.
	file := ""
	if profile == "" && len(positional) > 0 {
		file, positional = positional[0], positional[1:]
	}
	if (profile == "") == (file == "") || len(positional) > 1 || (file != "" && !filepath.IsAbs(file)) {
		fmt.Fprintln(errors, traceUsage)
		return 2
	}
	traces, err := bridge.ReadTraces(logview.Path(profile, file))
	if err != nil {
		fmt.Fprintf(errors, "trace: %v\n", err)
		return 1
	}
	encode := func(value any) int {
		encoder := json.NewEncoder(out)
		encoder.SetIndent("", "  ")
		if err := encoder.Encode(value); err != nil {
			fmt.Fprintf(errors, "trace: %v\n", err)
			return 1
		}
		return 0
	}
	if len(positional) == 0 {
		if asJSON {
			index := make([]map[string]any, 0, len(traces))
			for _, t := range traces {
				index = append(index, map[string]any{"trace_id": t.TraceID, "start": float64(t.Start().UnixNano()) / 1e9, "span_ms": t.SpanMs(), "spans": len(t.Spans), "rows": len(t.Rows), "root": t.Root})
			}
			return encode(index)
		}
		bridge.WriteTraceIndex(out, traces)
		return 0
	}
	found, ok := bridge.FindTrace(traces, positional[0])
	if !ok {
		fmt.Fprintf(errors, "trace: no span or row carries trace_id %s\n", positional[0])
		return 1
	}
	if asJSON {
		return encode(found.JSON())
	}
	bridge.WriteTraceReport(out, found)
	return 0
}
