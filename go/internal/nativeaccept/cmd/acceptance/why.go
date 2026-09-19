package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/postmortem"
)

const whyUsage = `
  acceptance why <output>/<area>/<case> [-json]`

// why prints the postmortem digest of a case output directory (#278): the
// same digest a failed run writes to result.json ("diagnosis") and
// diagnosis.txt, recomputed from the evidence on disk so it can be re-read
// after a fix lands or against a peer's run.
func why(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("why", flag.ContinueOnError)
	fs.SetOutput(stderr)
	asJSON := fs.Bool("json", false, "print the digest as JSON instead of text")
	var dirs, flagArgs []string
	for i, a := range args {
		if len(a) > 0 && a[0] == '-' {
			flagArgs = args[i:]
			break
		}
		dirs = append(dirs, a)
	}
	if err := fs.Parse(flagArgs); err != nil {
		return 2
	}
	dirs = append(dirs, fs.Args()...)
	if len(dirs) == 0 {
		fmt.Fprintln(stderr, "why needs a case output directory (the one holding result.json)")
		return 2
	}
	exit := 0
	for _, dir := range dirs {
		if _, err := os.Stat(filepath.Join(dir, "result.json")); err != nil {
			fmt.Fprintf(stderr, "%s: no result.json: %v\n", dir, err)
			exit = 1
			continue
		}
		digest := postmortem.Collect(context.Background(), dir, nil)
		if *asJSON {
			data, _ := json.MarshalIndent(digest, "", "  ")
			fmt.Fprintln(stdout, string(data))
			continue
		}
		if len(dirs) > 1 {
			fmt.Fprintf(stdout, "# %s\n", dir)
		}
		_ = digest.Write(stdout)
	}
	return exit
}
