// Command test runs the Go checks a change owes: gofmt on the changed files,
// go vet and staticcheck on the module, then go test -short ./... (Go's test
// cache replays unchanged packages), plus the native contract probes build
// when its inputs changed (#334).
//
//	go run ./cmd/test [-base main] [-full]
//
// run from anywhere inside the worktree. It diffs the working tree
// (committed, staged, unstaged and untracked) against -base, so it is the
// edit/test loop's check as well as the pre-land one; the landing lane
// (cmd/land) does not test, so run this before landing. No acceptance
// run precedes landing; the nightly tier proves the end-to-end cases.
//
// The default run passes -short, which skips the slow tests (git-heavy,
// planner and solver suites) so it stays near 30 s; -full runs them, as
// the nightly module run does, even with no changed Go files.
package main

import (
	"flag"
	"fmt"
	"os"

	"github.com/davidarcher/RimGovernor/go/internal/affected"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/inputs"
)

func main() {
	base := flag.String("base", "main", "revision to diff the working tree against")
	full := flag.Bool("full", false, "check the whole module including slow tests, even without changes; for the end of an epic")
	flag.Parse()
	affected.Full = *full
	if err := run(*base); err != nil {
		fmt.Fprintln(os.Stderr, "test:", err)
		os.Exit(1)
	}
}

func run(base string) error {
	cwd, err := os.Getwd()
	if err != nil {
		return err
	}
	repo, ok := na.FindRepo(cwd)
	if !ok {
		return fmt.Errorf("not inside a git checkout: %s", cwd)
	}
	changed, err := affected.ChangedFiles(repo, base)
	if err != nil {
		return err
	}
	return affected.Test(repo, changed)
}
