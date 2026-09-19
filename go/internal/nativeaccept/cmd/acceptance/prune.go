package main

import (
	"flag"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

const pruneUsage = `
  acceptance prune -output <dir> [-keep <n> -dry-run]
    deletes the oldest run outputs under <dir> (the parent of the -output directories runs and suites wrote),
    keeping the newest n (default 5) and their <name>.log/.err; loose files and directories holding no result.json stay`

// pruneDepth is how deep a run output nests its result.json: <suite>/<area>/<case>/result.json.
const pruneDepth = 3

// prune deletes old run outputs under one directory (#303): each child
// directory holding a result.json within pruneDepth is a run or suite
// output, ordered by modification time, and every one past the newest
// -keep is removed with the <name>.log and <name>.err a background launch
// wrote beside it. Anything else under the directory is left alone and
// named, so a root or a profile passed by mistake loses nothing.
func prune(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("prune", flag.ContinueOnError)
	fs.SetOutput(stderr)
	output := fs.String("output", "", "absolute directory holding run outputs")
	keep := fs.Int("keep", 5, "newest run outputs to keep")
	dryRun := fs.Bool("dry-run", false, "list what would be deleted without deleting")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	if *output == "" || !filepath.IsAbs(*output) {
		fmt.Fprintln(stderr, "-output must be an absolute directory")
		return 2
	}
	if *keep < 0 {
		fmt.Fprintln(stderr, "-keep must be 0 or more")
		return 2
	}
	plan, err := planPrune(*output, *keep)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	for _, skipped := range plan.skipped {
		fmt.Fprintf(stdout, "skip %s (no result.json)\n", skipped)
	}
	for _, kept := range plan.kept {
		fmt.Fprintf(stdout, "keep %s\n", kept.name)
	}
	var freed int64
	for _, run := range plan.remove {
		verb := "delete"
		if *dryRun {
			verb = "would delete"
		}
		fmt.Fprintf(stdout, "%s %s (%s)\n", verb, run.name, humanBytes(run.bytes))
		if *dryRun {
			freed += run.bytes
			continue
		}
		for _, path := range run.paths {
			if err := os.RemoveAll(path); err != nil {
				fmt.Fprintf(stderr, "delete %s: %v\n", path, err)
				return 1
			}
		}
		freed += run.bytes
	}
	fmt.Fprintf(stdout, "%d run outputs kept, %d removed, %s freed\n", len(plan.kept), len(plan.remove), humanBytes(freed))
	return 0
}

type pruneRun struct {
	name  string
	paths []string
	bytes int64
	// modified is the run's last write: the newest file anywhere under it,
	// so a suite still being written sorts newest whatever its directory's
	// own stamp says.
	modified int64
}

type prunePlan struct {
	kept, remove []pruneRun
	skipped      []string
}

// planPrune lists the run outputs under dir, newest first, and splits them
// at keep.
func planPrune(dir string, keep int) (prunePlan, error) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return prunePlan{}, err
	}
	var plan prunePlan
	var runs []pruneRun
	for _, entry := range entries {
		if !entry.IsDir() {
			continue
		}
		path := filepath.Join(dir, entry.Name())
		run, ok, err := scanRun(path)
		if err != nil {
			return prunePlan{}, err
		}
		if !ok {
			plan.skipped = append(plan.skipped, entry.Name())
			continue
		}
		run.name = entry.Name()
		run.paths = []string{path}
		for _, suffix := range []string{".log", ".err"} {
			side := path + suffix
			if info, err := os.Stat(side); err == nil && info.Mode().IsRegular() {
				run.paths = append(run.paths, side)
				run.bytes += info.Size()
			}
		}
		runs = append(runs, run)
	}
	sort.SliceStable(runs, func(i, j int) bool { return runs[i].modified > runs[j].modified })
	if keep > len(runs) {
		keep = len(runs)
	}
	plan.kept, plan.remove = runs[:keep], runs[keep:]
	return plan, nil
}

// scanRun sizes a directory and reports whether it is a run output: a
// result.json lies within pruneDepth of it.
func scanRun(dir string) (pruneRun, bool, error) {
	var run pruneRun
	found := false
	err := filepath.WalkDir(dir, func(path string, d fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			return nil
		}
		info, err := d.Info()
		if err != nil {
			return err
		}
		run.bytes += info.Size()
		if stamp := info.ModTime().UnixNano(); stamp > run.modified {
			run.modified = stamp
		}
		if d.Name() == "result.json" {
			rel, _ := filepath.Rel(dir, path)
			if strings.Count(filepath.ToSlash(rel), "/") < pruneDepth {
				found = true
			}
		}
		return nil
	})
	return run, found, err
}

func humanBytes(n int64) string {
	const unit = 1024
	if n < unit {
		return fmt.Sprintf("%d B", n)
	}
	value, exp := float64(n)/unit, 0
	for ; value >= unit && exp < 3; exp++ {
		value /= unit
	}
	return fmt.Sprintf("%.1f %ciB", value, "KMGT"[exp])
}
