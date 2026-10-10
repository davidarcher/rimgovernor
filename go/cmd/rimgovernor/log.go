package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/logview"
)

const logUsage = "usage: rimgovernor log (--profile <dir> | <absolute flight.jsonl>) [--kind K] [--component C] [--level INFO|WARN|ERROR] [--tick a..b] [--trace ID] [--since-run] [--follow] [--json]"

// logCommand prints the diagnostic stream (flight.jsonl rows) filtered and
// with repeated decision rows collapsed into one counted line. --follow
// keeps printing rows as they are appended, uncollapsed, until ctx ends.
// Exit 2 is usage, 1 a read error.
func logCommand(ctx context.Context, args []string, out, errors io.Writer) int {
	usage := func(reason string) int {
		if reason != "" {
			fmt.Fprintln(errors, "log:", reason)
		}
		fmt.Fprintln(errors, logUsage)
		return 2
	}
	var filter logview.Filter
	var profile, file string
	asJSON, follow := false, false
	for i := 0; i < len(args); i++ {
		arg := args[i]
		value := func() (string, bool) {
			if i+1 >= len(args) {
				return "", false
			}
			i++
			return args[i], true
		}
		var ok bool
		var text string
		switch arg {
		case "--json":
			asJSON = true
		case "--follow":
			follow = true
		case "--since-run":
			filter.SinceRun = true
		case "--profile", "--kind", "--component", "--level", "--tick", "--trace":
			if text, ok = value(); !ok {
				return usage(arg + " needs a value")
			}
			switch arg {
			case "--profile":
				profile = text
			case "--kind":
				filter.Kind = text
			case "--component":
				filter.Component = text
			case "--level":
				if !logview.ValidLevel(text) {
					return usage("unknown level " + text)
				}
				filter.Level = text
			case "--tick":
				var err error
				if filter.Ticks, err = logview.ParseTickRange(text); err != nil {
					return usage(err.Error())
				}
			case "--trace":
				filter.Trace = text
			}
		default:
			if len(arg) > 0 && arg[0] != '-' && file == "" {
				file = arg
				continue
			}
			return usage("unexpected argument " + arg)
		}
	}
	if (profile == "") == (file == "") {
		return usage("give --profile or a file path, not both or neither")
	}
	if profile == "" && !filepath.IsAbs(file) {
		return usage("the file path must be absolute")
	}
	path := logview.Path(profile, file)
	records, err := bridge.ReadMergedTimeline(path)
	if err != nil {
		fmt.Fprintf(errors, "log: %v\n", err)
		return 1
	}
	encoder := json.NewEncoder(out)
	print := func(e logview.Entry) {
		if asJSON {
			_ = encoder.Encode(e)
			return
		}
		fmt.Fprintln(out, e.Line())
	}
	for _, e := range logview.Collapse(filter.Apply(records)) {
		print(e)
	}
	if !follow {
		return 0
	}
	if err := logview.Follow(ctx, path, filter, 500*time.Millisecond, func(rec bridge.TimelineRecord) { print(logview.NewEntry(rec)) }); err != nil {
		fmt.Fprintf(errors, "log: %v\n", err)
		return 1
	}
	return 0
}
