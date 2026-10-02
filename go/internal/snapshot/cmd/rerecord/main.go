// Command rerecord refreshes registered snapshot testdata
// (internal/snapshot/recordings.json, written by `trim -case`) from a
// newer recording of each file's acceptance case, so a stale snapshot is
// one command instead of a hand trim. Run it from go/:
//
//	go run ./internal/snapshot/cmd/rerecord -from <dir> [out ...]
//
// <dir> is a RIMGOVERNOR_SNAPSHOT_DIR from a local run or a downloaded CI
// shard artifact (`gh run download <run> -n acceptance-<run>-<attempt>-shard-<id>`):
// any routine-stream under a <...>/<area>/<case>/ directory counts. Naming
// testdata files limits the refresh to them; -list prints the registry.
// Each file is rewritten from the review (or the same planner and goal's
// step read) at its recorded tick, else the first after it, and the
// registry takes the new tick. Rerun the consuming package's tests: the
// assertions, not the recording, state the behaviour.
package main

import (
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}

func run(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("rerecord", flag.ContinueOnError)
	fs.SetOutput(stderr)
	from := fs.String("from", "", "directory holding the newer recordings")
	list := fs.Bool("list", false, "print the registry and exit")
	registry := fs.String("registry", snapshot.RecordingsFile, "registry path, relative to go/")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	recs, err := snapshot.LoadRecordings(*registry)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	if *list {
		for _, r := range recs {
			at := fmt.Sprintf("review %d-%d", r.Tick, r.Seq)
			if r.Step != "" {
				at = r.Step
			}
			fmt.Fprintf(stdout, "%s\t%s\t%s\n", r.Out, r.Case, at)
		}
		return 0
	}
	if *from == "" {
		fmt.Fprintln(stderr, "usage: rerecord -from <dir> [out ...] | rerecord -list")
		return 2
	}
	only := map[string]bool{}
	for _, a := range fs.Args() {
		only[filepath.ToSlash(filepath.Clean(a))] = true
	}
	failed := 0
	for i, rec := range recs {
		if len(only) > 0 && !only[rec.Out] {
			continue
		}
		delete(only, rec.Out)
		next, err := refresh(rec, *from)
		if err != nil {
			fmt.Fprintln(stderr, err)
			failed++
			continue
		}
		recs[i] = next
		at := fmt.Sprintf("review %d-%d", next.Tick, next.Seq)
		if next.Step != "" {
			at = next.Step
		}
		fmt.Fprintf(stdout, "%s: %s from %s\n", rec.Out, at, rec.Case)
	}
	for out := range only {
		fmt.Fprintf(stderr, "%s is not registered: cut it once with `trim -case <area/case>`\n", out)
		failed++
	}
	if err := snapshot.SaveRecordings(*registry, recs); err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	if failed > 0 {
		return 1
	}
	return 0
}

func refresh(rec snapshot.Recording, from string) (snapshot.Recording, error) {
	streams, err := snapshot.CaseStreams(from, rec.Case)
	if err != nil {
		return rec, err
	}
	if len(streams) == 0 {
		return rec, fmt.Errorf("%s: no routine stream for %s under %s", rec.Out, rec.Case, from)
	}
	stream, review, step, err := snapshot.Pick(rec, streams)
	if err != nil {
		return rec, err
	}
	if err := snapshot.TrimTo(stream, filepath.FromSlash(rec.Out), rec.KeepCells, int64(review.Tick), review.Seq, step); err != nil {
		return rec, fmt.Errorf("%s: %w", rec.Out, err)
	}
	if step != "" {
		rec.Step = step
	} else {
		rec.Tick, rec.Seq = int64(review.Tick), review.Seq
	}
	return rec, nil
}
