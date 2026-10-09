// Command trim copies one recorded rounds, or a planner step read,
// into testdata as gzipped compact JSON without its planning cells
// (-keep-cells keeps them for a site-search test). The input is a serve's
// recorded stream, with -tick naming the review (-seq one of
// several at that tick, default the last) or -step naming a step read
// (step-<planner>-<goal>-<tick>-<seq>, as -list prints it), or a
// single-frame recording or per-step file (step-*.json) recorded before
// the stream carried them:
//
//	go run ./internal/snapshot/cmd/trim -tick <t> <routine-stream-*.jsonl> <testdata/name.json.gz>
//	go run ./internal/snapshot/cmd/trim -step <step-...> <routine-stream-*.jsonl> <testdata/name.json.gz>
//	go run ./internal/snapshot/cmd/trim -list <routine-stream-*.jsonl>
//
// -case <area/case> (run from go/) also registers the cut in
// internal/snapshot/recordings.json, so cmd/rerecord can refresh it from a
// newer recording of that case.
//
// -combat <name> promotes one fight of a stream (the first, or -plan's)
// into the combat replay testdata, trimmed to its stops with the
// combat sections keyed at its first; run it from go/:
//
//	go run ./internal/snapshot/cmd/trim -combat <name> [-plan <id>] <routine-stream-*.jsonl>
package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

func main() {
	keep := flag.Bool("keep-cells", false, "keep the planning window's site cells")
	tick := flag.Int64("tick", -1, "the review's tick, in a stream")
	seq := flag.Int("seq", 0, "the review's seq at -tick (default the last)")
	step := flag.String("step", "", "a step read in a stream, step-<planner>-<standard>-<tick>-<seq>")
	list := flag.Bool("list", false, "list a stream's reviews as <tick>-<seq>, then its step reads")
	combat := flag.String("combat", "", "promote a stream's fight to "+combatDir+"/<name>.json.gz")
	plan := flag.String("plan", "", "with -combat, the fight's plan (default the first fight)")
	caseName := flag.String("case", "", "register the cut in "+snapshot.RecordingsFile+" as recorded from this <area>/<case>, so cmd/rerecord can refresh it")
	flag.Parse()
	var err error
	switch {
	case *combat != "" && flag.NArg() == 1:
		err = promoteCombat(flag.Arg(0), *combat, domain.PlanID(*plan))
	case *list && flag.NArg() == 1:
		err = listStream(flag.Arg(0))
	case !*list && flag.NArg() == 2:
		err = run(flag.Arg(0), flag.Arg(1), *keep, *tick, *seq, *step, *caseName)
	default:
		fmt.Fprintln(os.Stderr, "usage: trim [-keep-cells] [-case area/case] [-tick t [-seq s] | -step name] <recording> <out.json.gz> | trim -list <stream> | trim -combat name [-plan id] <stream>")
		os.Exit(2)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

// combatDir is the combat replay testdata, relative to go/.
const combatDir = "internal/buildingruntime/testdata/combat"

func promoteCombat(in, name string, plan domain.PlanID) error {
	data, err := snapshot.TrimCombat(in, plan)
	if err != nil {
		return err
	}
	if err = os.MkdirAll(combatDir, 0o755); err != nil {
		return err
	}
	out := filepath.Join(combatDir, name+".json.gz")
	fmt.Printf("%s: %d bytes\n", out, len(data))
	return os.WriteFile(out, data, 0o644)
}

func listStream(path string) error {
	reviews, err := snapshot.Reviews(path)
	if err != nil {
		return err
	}
	for _, r := range reviews {
		fmt.Println(r)
	}
	steps, err := snapshot.Steps(path)
	for _, s := range steps {
		fmt.Println(s)
	}
	return err
}

func run(in, out string, keep bool, tick int64, seq int, step, caseName string) error {
	if err := snapshot.TrimTo(in, out, keep, tick, seq, step); err != nil {
		return err
	}
	if caseName == "" {
		return nil
	}
	if !snapshot.IsStream(in) {
		return fmt.Errorf("-case registers a cut from a stream; %s is not one", in)
	}
	rel, err := filepath.Rel(".", out)
	if err != nil {
		return err
	}
	rec := snapshot.Recording{Out: rel, Case: caseName, Step: step, KeepCells: keep}
	if step == "" {
		rec.Tick, rec.Seq = tick, seq
	}
	return snapshot.Register(snapshot.RecordingsFile, rec)
}
