// Command affected prints the checks a change needs: the Go packages to
// test and the native acceptance case areas whose inputs the change touched.
//
//	go run ./cmd/affected [-base main] [-files] [-baseline <result.json|metrics.jsonl>] [<file>...]
//
// With no files it diffs the working tree (committed, staged, unstaged and
// untracked) against -base. -files prints the changed files it considered
// and, under each acceptance line, why the area was selected: the changed
// file and the rule it reached the area through (#361).
// The output is one command per line, ready to run from go/:
//
//	go test ./internal/policy/... ...
//	go run ./internal/nativeaccept/cmd/acceptance run temperature/... ...
//
// and "nothing to test" when no Go file changed. cmd/test runs the go test
// line; the acceptance lines are advice: run them at the milestone, before
// landing. -baseline prices the affected case set from an earlier run's
// timings (#283): the total wall and boot time of the baseline's rows in
// the affected areas, as `acceptance list -cost` shows per case. Cases the
// baseline never timed are not in the total; `acceptance list -cost
// <area>/...` names them as untimed.
package main

import (
	"flag"
	"fmt"
	"os"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/affected"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cost"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/inputs"
)

func main() {
	base := flag.String("base", "main", "revision to diff the working tree against")
	showFiles := flag.Bool("files", false, "also print the changed files considered")
	baselinePath := flag.String("baseline", "", "suite result.json or metrics.jsonl to price the affected cases from")
	flag.Parse()
	cwd, err := os.Getwd()
	if err != nil {
		fail(err)
	}
	repo, ok := na.FindRepo(cwd)
	if !ok {
		fail(fmt.Errorf("not inside a git checkout: %s", cwd))
	}
	changed := flag.Args()
	if len(changed) == 0 {
		changed, err = affected.ChangedFiles(repo, *base)
		if err != nil {
			fail(err)
		}
	}
	if *showFiles {
		for _, file := range changed {
			fmt.Println("#", file)
		}
	}
	sel, err := affected.Select(repo, changed)
	if err != nil {
		fail(err)
	}
	switch {
	case sel.AllGo:
		fmt.Println("go test ./...")
	case len(sel.Packages) > 0:
		fmt.Println("go test " + strings.Join(sel.Packages, " "))
	default:
		fmt.Println("# nothing to test: no Go file changed")
	}
	if sel.AllHarnesses {
		fmt.Println("# a shared acceptance input changed (native sources or go.mod): every case is affected")
	}
	for _, area := range sel.Cases {
		fmt.Printf("go run ./internal/nativeaccept/cmd/acceptance run %s/... ...\n", area)
		if *showFiles {
			for _, why := range sel.Why[area] {
				fmt.Printf("#   %s: %s\n", area, why)
			}
		}
	}
	if sel.Probes {
		fmt.Println("task probes:build")
	}
	if *baselinePath != "" {
		table, err := cost.Load(*baselinePath)
		if err != nil {
			fail(err)
		}
		fmt.Println("# " + costLine(table, sel))
	}
}

// costLine totals the baseline's rows in the affected areas (every row
// when all harnesses are affected): "cost: 12 cases, 41m10s wall (boot
// 2m3s) timed by <baseline>".
func costLine(table *cost.Table, sel affected.Selection) string {
	var names []string
	for _, name := range table.Names() {
		area, _, _ := strings.Cut(name, "/")
		if sel.AllHarnesses || slices.Contains(sel.Cases, area) {
			names = append(names, name)
		}
	}
	if len(names) == 0 {
		return "cost: no affected case timed by " + table.Path
	}
	return fmt.Sprintf("cost: %s timed by %s", cost.Summarize(table, names), table.Path)
}

func fail(err error) {
	fmt.Fprintln(os.Stderr, "affected:", err)
	os.Exit(1)
}
