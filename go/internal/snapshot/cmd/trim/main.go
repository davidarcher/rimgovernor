// Command trim copies one recorded routine review, or a planner step read
// (step-<goal>-<tick>-<seq>.json, #794), into testdata as gzipped
// compact JSON without its planning cells (-keep-cells keeps them for a
// site-search test). The input is a serve's recorded stream (#756), with
// -tick naming the review (-seq one of several at that tick, default the
// last), or a single-frame recording or step read:
//
//	go run ./internal/snapshot/cmd/trim -tick <t> <routine-stream-*.jsonl> <testdata/name.json.gz>
//	go run ./internal/snapshot/cmd/trim -list <routine-stream-*.jsonl>
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
	list := flag.Bool("list", false, "list a stream's reviews as <tick>-<seq>")
	flag.Parse()
	var err error
	switch {
	case *list && flag.NArg() == 1:
		var reviews []snapshot.Review
		if reviews, err = snapshot.Reviews(flag.Arg(0)); err == nil {
			for _, r := range reviews {
				fmt.Println(r)
			}
		}
	case !*list && flag.NArg() == 2:
		err = run(flag.Arg(0), flag.Arg(1), *keep, *tick, *seq)
	default:
		fmt.Fprintln(os.Stderr, "usage: trim [-keep-cells] [-tick t [-seq s]] <recording> <out.json.gz> | trim -list <stream>")
		os.Exit(2)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func run(in, out string, keep bool, tick int64, seq int) error {
	if strings.HasPrefix(filepath.Base(in), "step-") {
		s, err := snapshot.LoadStep(in)
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
			return fmt.Errorf("%s is a stream: name the review with -tick (trim -list shows them)", in)
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
