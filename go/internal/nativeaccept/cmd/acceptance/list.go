package main

import (
	"flag"
	"fmt"
	"io"
	"strings"
	"text/tabwriter"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cost"
)

const listUsage = `  acceptance list [-cost [-baseline <result.json|metrics.jsonl>]] [<case>|<area>/...]...`

// list prints the registry, or the named cases and areas (`<area>/...`),
// one per line with its scope. -cost adds each case's baseline wall and
// boot time (#283) and a total for the set; without a baseline row a case
// is "untimed". The baseline is a suite result.json or a metrics series.
func list(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("list", flag.ContinueOnError)
	fs.SetOutput(stderr)
	withCost := fs.Bool("cost", false, "show each case's baseline wall and boot time and the set's total")
	baselinePath := fs.String("baseline", "", "suite result.json or metrics.jsonl the costs come from")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	selected, err := selectCases(fs.Args())
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	if !*withCost {
		for _, c := range selected {
			fmt.Fprintf(stdout, "%s\t%s\n", c.Name, c.Scope)
		}
		return 0
	}
	var table *cost.Table
	if *baselinePath != "" {
		if table, err = cost.Load(*baselinePath); err != nil {
			fmt.Fprintln(stderr, err)
			return 2
		}
	}
	names := make([]string, 0, len(selected))
	tw := tabwriter.NewWriter(stdout, 0, 0, 2, ' ', 0)
	fmt.Fprintln(tw, "case\twall\tboot\tscope")
	for _, c := range selected {
		names = append(names, c.Name)
		wall, boot := "untimed", ""
		if r, ok := table.Of(c.Name); ok {
			wall, boot = cost.Format(r.Wall), cost.Format(r.Boot)
		}
		fmt.Fprintf(tw, "%s\t%s\t%s\t%s\n", c.Name, wall, boot, c.Scope)
	}
	tw.Flush()
	summary := cost.Summarize(table, names)
	switch {
	case table == nil:
		fmt.Fprintf(stdout, "total: %s (no -baseline given)\n", summary)
	default:
		fmt.Fprintf(stdout, "total: %s (baseline %s)\n", summary, table.Path)
	}
	return 0
}

// selectCases resolves names to registry cases in registry order: a bare
// name is one case, "<area>/..." every case of the area, and no names the
// whole registry.
func selectCases(names []string) ([]cases.Case, error) {
	all := cases.All()
	if len(names) == 0 {
		return all, nil
	}
	want := map[string]bool{}
	for _, name := range names {
		matched := false
		for _, c := range all {
			if c.Name == name || (strings.HasSuffix(name, "/...") && strings.HasPrefix(c.Name, strings.TrimSuffix(name, "..."))) {
				want[c.Name] = true
				matched = true
			}
		}
		if !matched {
			return nil, fmt.Errorf("unknown case %q (see `acceptance list`)", name)
		}
	}
	selected := make([]cases.Case, 0, len(want))
	for _, c := range all {
		if want[c.Name] {
			selected = append(selected, c)
		}
	}
	return selected, nil
}
