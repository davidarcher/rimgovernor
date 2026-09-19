package main

// acceptance resume (#280): continue the case a root holds paused at a
// breakpoint. A run cut by -break leaves its ring with a "break" bundle as
// the next entry and Ring.Break set; resume finds those rings under
// <root>/checkpoints (or checks the cases named) and runs them, which
// resumes from the bundle the way any ring resume does. stop discards
// them (discardBreaks).

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const resumeUsage = `
  acceptance resume [<case>...] -root <dir> [run flags]
    continues the case(s) a -break run left paused in <root> from their break bundles (the game relaunches
    on the bundle); with no case named, the one case paused there; -break again stops at a later point`

// breakRing is a ring holding a breakpoint: the case and its index.
type breakRing struct {
	name string
	dir  string
	ring *na.Ring
}

// breakRings lists the rings under root/checkpoints whose last run paused
// at a breakpoint, by case name.
func breakRings(root string) ([]breakRing, error) {
	base := filepath.Join(root, "checkpoints")
	var found []breakRing
	err := filepath.WalkDir(base, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			if path == base && os.IsNotExist(err) {
				return filepath.SkipAll
			}
			return err
		}
		if d.IsDir() || d.Name() != na.CheckpointRingFile {
			return nil
		}
		dir := filepath.Dir(path)
		ring, err := na.ReadRing(dir)
		if err != nil || ring == nil || ring.Break == nil {
			return nil
		}
		rel, err := filepath.Rel(base, dir)
		if err != nil {
			return nil
		}
		found = append(found, breakRing{name: filepath.ToSlash(rel), dir: dir, ring: ring})
		return nil
	})
	sort.Slice(found, func(i, j int) bool { return found[i].name < found[j].name })
	return found, err
}

// resume is the subcommand: the cases named (or the root's one paused
// case) run through runCases, each resuming from its break bundle.
func resume(ctx context.Context, args []string, stdout, stderr io.Writer) int {
	var names, flagArgs []string
	for i, a := range args {
		if strings.HasPrefix(a, "-") {
			flagArgs = args[i:]
			break
		}
		names = append(names, a)
	}
	root := flagValue(flagArgs, "root")
	if root == "" {
		fmt.Fprintln(stderr, "-root is required")
		return 2
	}
	paused, err := breakRings(root)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	if len(names) == 0 {
		switch len(paused) {
		case 0:
			fmt.Fprintf(stderr, "nothing is paused at a breakpoint under %s (a run with -break leaves one)\n", root)
			return 2
		case 1:
			names = []string{paused[0].name}
		default:
			var list []string
			for _, p := range paused {
				list = append(list, fmt.Sprintf("%s (%s)", p.name, p.ring.Break.Spec))
			}
			fmt.Fprintf(stderr, "several cases are paused under %s; name one: %s\n", root, strings.Join(list, ", "))
			return 2
		}
	}
	for _, name := range names {
		found := false
		for _, p := range paused {
			if p.name == name {
				found = true
			}
		}
		if !found {
			fmt.Fprintf(stderr, "%s is not paused at a breakpoint under %s\n", name, root)
			return 2
		}
	}
	selected, opts, err := parseRun(append(names, flagArgs...), stderr)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	if opts.Fresh || opts.Repeat > 1 || opts.PostmortemOnly || opts.Seed != "" {
		fmt.Fprintln(stderr, "resume continues from the break bundle: it takes none of -fresh, -repeat, -seed, -postmortem-only")
		return 2
	}
	for _, p := range paused {
		for _, c := range selected {
			if p.name == c.Name {
				fmt.Fprintf(stdout, "resume: %s paused at %s (%s, tick %d)\n", c.Name, p.ring.Break.Spec, p.ring.Break.Reason, p.ring.Break.Tick)
			}
		}
	}
	return runCases(ctx, selected, opts, stdout)
}

// flagValue reads a flag's value from args before the FlagSet does, for
// a decision that precedes parsing; "" when absent.
func flagValue(args []string, name string) string {
	for i, a := range args {
		for _, prefix := range []string{"-" + name, "--" + name} {
			if a == prefix && i+1 < len(args) {
				return args[i+1]
			}
			if strings.HasPrefix(a, prefix+"=") {
				return strings.TrimPrefix(a, prefix+"=")
			}
		}
	}
	return ""
}

// discardBreaks removes the rings under root that hold a breakpoint, so
// the next plain run of each case starts fresh; it names what it removed.
func discardBreaks(root string, stdout io.Writer) error {
	paused, err := breakRings(root)
	if err != nil {
		return err
	}
	var errs []error
	for _, p := range paused {
		if err := os.RemoveAll(p.dir); err != nil {
			errs = append(errs, err)
			continue
		}
		fmt.Fprintf(stdout, "discarded the breakpoint of %s (%s)\n", p.name, p.ring.Break.Spec)
	}
	return errors.Join(errs...)
}
