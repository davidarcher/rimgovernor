// Command trim copies a recorded routine snapshot, or a planner step read
// (step-<goal>-<tick>-<seq>.json, #794), into testdata as gzipped compact
// JSON without its planning cells (-keep-cells keeps them for a
// site-search test):
//
//	go run ./internal/snapshot/cmd/trim <routine-<tick>-<seq>.json|step-...json> <testdata/name.json.gz>
package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

func main() {
	keep := flag.Bool("keep-cells", false, "keep the planning window's site cells")
	flag.Parse()
	if flag.NArg() != 2 {
		fmt.Fprintln(os.Stderr, "usage: trim [-keep-cells] <recorded.json> <out.json.gz>")
		os.Exit(2)
	}
	if err := run(flag.Arg(0), flag.Arg(1), *keep); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func run(in, out string, keep bool) error {
	var data []byte
	if strings.HasPrefix(filepath.Base(in), "step-") {
		s, err := snapshot.LoadStep(in)
		if err != nil {
			return err
		}
		if data, err = snapshot.CompressStep(s, keep); err != nil {
			return err
		}
	} else {
		r, err := snapshot.Load(in)
		if err != nil {
			return err
		}
		if !keep {
			r.TrimCells()
		}
		if data, err = snapshot.Compress(r); err != nil {
			return err
		}
	}
	return os.WriteFile(out, data, 0o644)
}
