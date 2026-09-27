// Command trim copies one recorded routine review, or a planner step read
// (#794), into testdata as gzipped compact JSON without its planning cells
// (-keep-cells keeps them for a site-search test). The input is a serve's
// recorded stream (#756), with -tick naming the review (-seq one of
// several at that tick, default the last) or -step naming a step read
// (step-<planner>-<goal>-<tick>-<seq>, as -list prints it), or a
// single-frame recording or per-step file (step-*.json) recorded before
// the stream carried them:
//
//	go run ./internal/snapshot/cmd/trim -tick <t> <routine-stream-*.jsonl> <testdata/name.json.gz>
//	go run ./internal/snapshot/cmd/trim -step <step-...> <routine-stream-*.jsonl> <testdata/name.json.gz>
//	go run ./internal/snapshot/cmd/trim -list <routine-stream-*.jsonl>
//
// -combat <name> promotes one fight of a stream (the first, or -plan's)
// into the combat replay testdata (#853), trimmed to its stops with the
// combat sections keyed at its first; run it from go/:
//
//	go run ./internal/snapshot/cmd/trim -combat <name> [-plan <id>] <routine-stream-*.jsonl>
package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

func main() {
	keep := flag.Bool("keep-cells", false, "keep the planning window's site cells")
	tick := flag.Int64("tick", -1, "the review's tick, in a stream")
	seq := flag.Int("seq", 0, "the review's seq at -tick (default the last)")
	step := flag.String("step", "", "a step read in a stream, step-<planner>-<goal>-<tick>-<seq>")
	list := flag.Bool("list", false, "list a stream's reviews as <tick>-<seq>, then its step reads")
	combat := flag.String("combat", "", "promote a stream's fight to "+combatDir+"/<name>.json.gz")
	plan := flag.String("plan", "", "with -combat, the fight's plan (default the first fight)")
	flag.Parse()
	var err error
	switch {
	case *combat != "" && flag.NArg() == 1:
		err = promoteCombat(flag.Arg(0), *combat, domain.PlanID(*plan))
	case *list && flag.NArg() == 1:
		err = listStream(flag.Arg(0))
	case !*list && flag.NArg() == 2:
		err = run(flag.Arg(0), flag.Arg(1), *keep, *tick, *seq, *step)
	default:
		fmt.Fprintln(os.Stderr, "usage: trim [-keep-cells] [-tick t [-seq s] | -step name] <recording> <out.json.gz> | trim -list <stream> | trim -combat name [-plan id] <stream>")
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

func run(in, out string, keep bool, tick int64, seq int, step string) error {
	if step != "" || strings.HasPrefix(filepath.Base(in), "step-") {
		var s snapshot.Step
		var err error
		if step != "" {
			s, err = snapshot.LoadStreamStep(in, step)
		} else {
			s, err = snapshot.LoadStep(in)
		}
		if err != nil {
			return err
		}
		data, err := snapshot.CompressStep(s, keep)
		if err != nil {
			return err
		}
		return os.WriteFile(out, data, 0o644)
	}
	var r snapshot.Routine
	var err error
	if snapshot.IsStream(in) {
		if tick < 0 {
			return fmt.Errorf("%s is a stream: name the review with -tick or a step read with -step (trim -list shows them)", in)
		}
		r, err = snapshot.LoadReview(in, domain.Tick(tick), seq)
	} else {
		r, err = snapshot.Load(in)
	}
	if err != nil {
		return err
	}
	if !keep {
		r.TrimCells()
	}
	data, err := snapshot.Compress(r)
	if err != nil {
		return err
	}
	return os.WriteFile(out, data, 0o644)
}
