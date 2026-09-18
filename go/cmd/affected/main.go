// Command affected prints the checks a change needs: the Go packages to
// test and the native acceptance case areas whose inputs the change touched.
//
//	go run ./cmd/affected [-base main] [-files] [<file>...]
//
// With no files it diffs the working tree (committed, staged, unstaged and
// untracked) against -base. -files prints the changed files it considered.
// The output is one command per line, ready to run from go/:
//
//	go test ./internal/policy/... ...
//	go run ./internal/nativeaccept/cmd/acceptance run temperature/... ...
//
// and "nothing to test" when no Go file changed. cmd/test runs the go test
// line; the acceptance lines are advice: run them at the milestone, before
// landing.
package main

import (
	"flag"
	"fmt"
	"os"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/affected"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/inputs"
)

func main() {
	base := flag.String("base", "main", "revision to diff the working tree against")
	showFiles := flag.Bool("files", false, "also print the changed files considered")
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
	}
}

func fail(err error) {
	fmt.Fprintln(os.Stderr, "affected:", err)
	os.Exit(1)
}
