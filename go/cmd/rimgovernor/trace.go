package main

import (
	"encoding/json"
	"fmt"
	"io"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// trace renders one trace of a flight recording as a waterfall (the rows a
// scheduler step or Worker dispatch left, in order, with offsets, phases
// and the companion's own timing), or, without a trace id, lists the
// recording's traces so a reader can find the slow step. Read-only over
// the recording, like phases; rotated segments are read too.
func trace(args []string, out, errors io.Writer) int {
	asJSON := false
	var positional []string
	for _, arg := range args {
		switch {
		case arg == "--json":
			asJSON = true
		case len(arg) > 0 && arg[0] != '-':
			positional = append(positional, arg)
		default:
			positional = nil
		}
	}
	if len(positional) == 0 || len(positional) > 2 || !filepath.IsAbs(positional[0]) {
		fmt.Fprintln(errors, "usage: rimgovernor trace [--json] <absolute flight-recorder.jsonl> [<trace_id>]")
		return 2
	}
	records, err := bridge.ReadTimeline(positional[0])
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
	if len(positional) == 1 {
		traces := bridge.SummarizeTraces(records)
		if asJSON {
			index := make([]map[string]any, 0, len(traces))
			for _, t := range traces {
				index = append(index, map[string]any{"trace_id": t.TraceID, "start": t.Start(), "span_ms": t.SpanMs(), "rows": len(t.Rows), "kinds": t.Kinds, "root": t.Root})
			}
			return encode(index)
		}
		bridge.WriteTraceIndex(out, traces)
		return 0
	}
	found, ok := bridge.FindTrace(records, positional[1])
	if !ok {
		fmt.Fprintf(errors, "trace: no rows carry trace_id %s\n", positional[1])
		return 1
	}
	if asJSON {
		rows := make([]map[string]any, 0, len(found.Rows))
		for _, row := range found.Rows {
			rows = append(rows, map[string]any{"sequence": row.Sequence, "wall_time": row.WallTime, "kind": row.Kind, "context": row.Context, "payload": row.Payload})
		}
		return encode(rows)
	}
	bridge.WriteTraceReport(out, found)
	return 0
}
