// Command acceptance is the shared runner over the case registry (#135):
//
//	acceptance list
//	acceptance run <case>... [-root -output -game -headless -timeout -budget -stall]
//
// It replaces the per-harness binaries' preamble with one loop: resolve the
// shared configuration, open the game, bring it to the case's Start, quiet
// the storyteller, freeze needs, run the case, write result.json with the
// run's timing. Cases register from the area packages imported below.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"

	// Registered case areas.
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/smoke"
)

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}

func run(args []string, stdout, stderr io.Writer) int {
	if len(args) == 0 {
		fmt.Fprintln(stderr, usage)
		return 2
	}
	switch args[0] {
	case "list":
		for _, c := range cases.All() {
			fmt.Fprintf(stdout, "%s\t%s\n", c.Name, c.Scope)
		}
		return 0
	case "run":
		selected, opts, err := parseRun(args[1:], stderr)
		if err != nil {
			fmt.Fprintln(stderr, err)
			return 2
		}
		return runCases(context.Background(), selected, opts, stdout)
	default:
		fmt.Fprintf(stderr, "unknown command %q\n%s\n", args[0], usage)
		return 2
	}
}

const usage = `usage:
  acceptance list
  acceptance run <case>... -root <dir> [-output <dir> -game <id> -headless=false -timeout <d> -budget <d> -stall <d>]`

// parseRun resolves the run subcommand's flags and case names. Flags may
// follow the case names (flag.FlagSet stops at the first non-flag, so the
// names are split off first).
func parseRun(args []string, stderr io.Writer) ([]cases.Case, cases.Options, error) {
	var names, flagArgs []string
	for i, a := range args {
		if strings.HasPrefix(a, "-") {
			flagArgs = args[i:]
			break
		}
		names = append(names, a)
	}
	fs := flag.NewFlagSet("run", flag.ContinueOnError)
	fs.SetOutput(stderr)
	var opts cases.Options
	fs.StringVar(&opts.Root, "root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	fs.StringVar(&opts.Output, "output", "", "output directory; each case writes under <output>/<case> (default <root>/acceptance)")
	fs.StringVar(&opts.GameID, "game", "rimgovernor-trial", "configured game ID")
	fs.BoolVar(&opts.Headless, "headless", true, "use the headless profile (false: windowed)")
	fs.DurationVar(&opts.Timeout, "timeout", cases.DefaultTimeout, "per-case safety net")
	fs.DurationVar(&opts.Budget, "budget", 0, "per-case wall-clock budget that fails the run (default: the case's own)")
	fs.DurationVar(&opts.Stall, "stall", 0, "stall budget for the shared waits (default: RIMGOVERNOR_ACCEPT_STALL or 3m)")
	if err := fs.Parse(flagArgs); err != nil {
		return nil, opts, err
	}
	if len(fs.Args()) > 0 {
		return nil, opts, fmt.Errorf("case names must precede the flags: %v", fs.Args())
	}
	if len(names) == 0 {
		return nil, opts, errors.New("run needs at least one case name (see `acceptance list`)")
	}
	if opts.Root == "" {
		return nil, opts, errors.New("-root is required")
	}
	if !filepath.IsAbs(opts.Root) {
		return nil, opts, fmt.Errorf("-root must be absolute: %s", opts.Root)
	}
	if opts.Output == "" {
		opts.Output = filepath.Join(opts.Root, "acceptance")
	}
	selected := make([]cases.Case, 0, len(names))
	for _, name := range names {
		c, ok := cases.Lookup(name)
		if !ok {
			return nil, opts, fmt.Errorf("unknown case %q (see `acceptance list`)", name)
		}
		if err := c.Lint(); err != nil {
			return nil, opts, err
		}
		selected = append(selected, c)
	}
	return selected, opts, nil
}

// runCases executes the cases in order on one game; the exit code is
// non-zero when any case failed.
func runCases(ctx context.Context, selected []cases.Case, opts cases.Options, stdout io.Writer) int {
	exit := 0
	for _, c := range selected {
		started := time.Now()
		report, code := cases.Execute(ctx, c, opts)
		status := "PASS"
		if code != 0 {
			exit = 1
			status = "FAIL"
		}
		fmt.Fprintf(stdout, "%s\t%s\t%s\t%s\n", status, c.Name, time.Since(started).Round(time.Millisecond), filepath.Join(opts.CaseOutput(c), "result.json"))
		if err, _ := report["error"].(string); err != "" {
			fmt.Fprintf(stdout, "\t%s\n", err)
		}
	}
	return exit
}
