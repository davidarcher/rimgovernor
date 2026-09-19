package main

import (
	"context"
	"flag"
	"fmt"
	"io"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/doctor"
)

const doctorUsage = `
  acceptance doctor -root <dir> [-rimgovernor <binary> -output <dir> -game <id> -worktree <dir>]`

// runDoctor is the preflight on its own (#277): every check with its fix,
// exit 1 only when one would certainly fail a run.
func runDoctor(ctx context.Context, args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("doctor", flag.ContinueOnError)
	fs.SetOutput(stderr)
	var o doctor.Options
	fs.StringVar(&o.Root, "root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	fs.StringVar(&o.Rimgovernor, "rimgovernor", "", "the rimgovernor binary a serve-driven run would take")
	fs.StringVar(&o.Output, "output", "", "the output directory a run would write under (default <root>/acceptance)")
	fs.StringVar(&o.GameID, "game", "rimgovernor-trial", "configured game ID")
	fs.StringVar(&o.Repo, "worktree", "", "checkout to compare the installed mod and binaries against (default: the one enclosing the working directory)")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	if len(fs.Args()) > 0 {
		fmt.Fprintf(stderr, "doctor takes no positional arguments: %v\n", fs.Args())
		return 2
	}
	if o.Root == "" {
		fmt.Fprintln(stderr, "-root is required")
		return 2
	}
	if !filepath.IsAbs(o.Root) {
		fmt.Fprintf(stderr, "-root must be absolute: %s\n", o.Root)
		return 2
	}
	checks := doctor.Run(ctx, o)
	doctor.Write(stdout, checks, false)
	if doctor.Failed(checks) {
		return 1
	}
	return 0
}
