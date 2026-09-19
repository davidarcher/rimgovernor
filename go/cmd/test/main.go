// Command test runs the Go tests a change affects: the packages holding
// the changed files and every in-module package importing them, as
// cmd/affected computes them (all packages when go.mod or go.sum changed),
// after gofmt, go vet and staticcheck on the change (#334).
//
//	go run ./cmd/test [-base main]
//
// run from anywhere inside the worktree. It diffs the working tree
// (committed, staged, unstaged and untracked) against -base, so it is the
// edit/test loop's check as well as the pre-land one; the landing lane
// (cmd/land) does not test, so run this before landing. Affected
// acceptance case areas are named, not run: the one acceptance run before
// landing is the smoke tier it prints (`acceptance suite -tier smoke`,
// #387); the nightly full tier proves the named areas, or `-tier land`
// proves them before landing when the change warrants it. Hand the
// output to `cmd/land -results`.
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
	flag.Parse()
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
	return affected.Test(repo, changed, base)
}
