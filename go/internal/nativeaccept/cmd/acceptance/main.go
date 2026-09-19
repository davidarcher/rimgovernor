// Command acceptance is the shared runner over the case registry (#135):
//
//	acceptance list [-cost [-baseline <result.json|metrics.jsonl>]] [<case>|<area>/...]...
//	acceptance run <case>... [-root -output -game -headless -timeout -budget -stall -rimgovernor -series -no-series -evidence -fresh -rewind N -checkpoint-every d]
//	acceptance suite (-all | -cases a,b | -suite file.json) -root -output -workers N [-baseline result.json -series metrics.jsonl]
//	acceptance stop -root <dir> [-config -game -takeover]
//	acceptance setup [-worktree -rimworld -harmony -gabs -fixture -production -rebuild -skip-mod -skip-binaries]
//	acceptance why <output>/<area>/<case> [-json]
//	acceptance warm -root <dir> [-game -headless=false -background]
//	acceptance fixture <op> [key=value ...] -root <dir> [-save <name> | -loaded]
//
// It replaces the per-harness binaries' preamble with one loop: resolve the
// shared configuration, open the game, bring it to the case's Start, quiet
// the storyteller, freeze needs, run the case, write result.json with the
// run's timing and metrics block, append the block to the metrics series
// and flag drift (nativeaccept/metrics.go). `suite` (suite.go) runs a set
// across N private game copies with regression and drift flagging. A run
// checkpoints its case into the root's ring and resumes a case whose last
// run there failed (#249; -fresh starts over, -rewind steps back,
// -checkpoint-every 0 turns it off); suite always runs fresh. Cases
// register from the area packages imported below. stop ends the game a
// root keeps between runs (na.KeepGameEnv) through GABS games_stop: the
// PID-owned launch recorded by that root's own GABS configuration, never
// a process matched by image name; warm (warm.go) boots that kept process
// ahead of the first run (#285). fixture (fixture.go) runs one
// fixture op against the root's kept game and leaves the world loaded
// for the next call (#284).
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

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/postmortem"

	// Registered case areas.
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/animals"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/apply"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/authority"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/bed"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/bills"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/caravan"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/clean"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/combat"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/construction"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/custody"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/defense"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/dialog"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/draft"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/facility"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/farm"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/floor"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/husbandry"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/letter"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/lifecycle"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/light"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/mapscope"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/medical"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/mood"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/movement"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/naming"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/needs"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/pawn"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/power"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/presentation"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/production"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/quest"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/reactivewatch"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/recovery"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/refrigeration"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/research"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/rooms"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/route"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/routinehaul"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/service"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/settlement"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/shelter"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/smoke"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/speedmatrix"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/storage"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/supplies"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/supply"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/surgery"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/temperature"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/tickbudget"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/tools"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/trade"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/upkeep"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/video"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/wall"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/waste"
	_ "github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/zone"
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
		return list(args[1:], stdout, stderr)
	case "run":
		selected, opts, err := parseRun(args[1:], stderr)
		if err != nil {
			fmt.Fprintln(stderr, err)
			return 2
		}
		return runCases(context.Background(), selected, opts, stdout)
	case "suite":
		list, opts, err := parseSuite(args[1:], stderr)
		if err != nil {
			fmt.Fprintln(stderr, err)
			return 2
		}
		return runSuite(context.Background(), list, opts, stderr)
	case "stop":
		return stop(args[1:], stdout, stderr)
	case "setup":
		return runSetup(context.Background(), args[1:], stdout, stderr)
	case "why":
		return why(args[1:], stdout, stderr)
	case "warm":
		return warm(context.Background(), args[1:], stdout, stderr)
	case "fixture":
		return runFixture(context.Background(), args[1:], stdout, stderr)
	default:
		fmt.Fprintf(stderr, "unknown command %q\n%s\n", args[0], usage)
		return 2
	}
}

const usage = `usage:
` + listUsage + `
  acceptance run <case>... -root <dir> [-output <dir> -game <id> -headless=false -timeout <d> -budget <d> -stall <d> -rimgovernor <binary> -series <metrics.jsonl> -no-series -evidence capped|full -fresh -rewind <n> -checkpoint-every <d>]
    a case whose last run in this root failed resumes from its checkpoint ring (printed on the first line);
    -fresh starts over, -rewind <n> resumes n entries earlier, -checkpoint-every 0 turns the ring off
  acceptance stop -root <dir> [-config <dir> -game <id> -takeover]
` + setupUsage + suiteUsage + whyUsage + warmUsage + fixtureUsage

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
	var evidence string
	fs.StringVar(&evidence, "evidence", "", "evidence mode: capped (payloads over 256 KiB truncated, the default) or full (also written under <case>/full/)")
	fs.StringVar(&opts.Rimgovernor, "rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor) for cases that launch a service")
	fs.BoolVar(&opts.Fresh, "fresh", false, "discard the case's checkpoint ring in this root and start from scratch (landing runs always do)")
	fs.IntVar(&opts.Rewind, "rewind", 0, "resume this many checkpoint entries earlier than the ring's next")
	fs.DurationVar(&opts.CheckpointEvery, "checkpoint-every", na.DefaultCheckpointEvery, "checkpoint the run phase this often at a natural pause into <root>/checkpoints/<case>/ (0 turns the ring and resuming off)")
	fs.StringVar(&opts.Series, "series", "", "append-only metrics series each case's block is appended to (default <output>/../metrics.jsonl)")
	fs.BoolVar(&opts.NoSeries, "no-series", false, "leave the metrics series alone")
	if err := fs.Parse(flagArgs); err != nil {
		return nil, opts, err
	}
	opts.Evidence = na.EvidenceMode(evidence)
	if err := na.SetEvidenceMode(opts.Evidence); err != nil {
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
	if opts.Rimgovernor != "" && !filepath.IsAbs(opts.Rimgovernor) {
		return nil, opts, fmt.Errorf("-rimgovernor must be absolute: %s", opts.Rimgovernor)
	}
	if opts.Output == "" {
		opts.Output = filepath.Join(opts.Root, "acceptance")
	}
	if opts.Rewind < 0 {
		return nil, opts, fmt.Errorf("-rewind must not be negative: %d", opts.Rewind)
	}
	opts.Output = mustAbs(opts.Output)
	if opts.Series != "" {
		opts.Series = mustAbs(opts.Series)
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
	opts.Log = stdout
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
		if digest, ok := report["diagnosis"].(postmortem.Digest); ok {
			// The digest is the first thing anyone reads after a failure;
			// under suite it lands in the case log.
			_ = digest.Write(stdout)
		}
		if flags, _ := report["drift"].([]na.DriftFlag); len(flags) > 0 {
			for _, f := range flags {
				fmt.Fprintf(stdout, "\tdrift: %s\n", f)
			}
		}
	}
	return exit
}

// stop is the former gamesstop tool: games_stop through the root's own
// GABS configuration (config-headless first), with -takeover taking the
// attachment from a stalled controller of that same root first.
func stop(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("stop", flag.ContinueOnError)
	fs.SetOutput(stderr)
	root := fs.String("root", "", "absolute disposable worker root")
	configDir := fs.String("config", "", "GABS config directory (default <root>/config-headless, then <root>/config)")
	game := fs.String("game", "rimgovernor-trial", "configured game ID")
	takeover := fs.Bool("takeover", false, "take the GABS attachment from a stalled controller of this same root before stopping")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	if *root == "" {
		fmt.Fprintln(stderr, "-root is required")
		return 2
	}
	if *configDir == "" {
		*configDir = filepath.Join(*root, "config-headless")
		if _, err := os.Stat(*configDir); err != nil {
			*configDir = filepath.Join(*root, "config")
		}
	}
	gabs, err := na.GABSExecutable(*root, *configDir)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	client, err := na.OpenBridgeSession(ctx, gabs, *configDir, *game, 60*time.Second)
	if err != nil && *takeover {
		// The root's own controller still holds the attachment; the caller
		// owns both sessions, so the handoff is explicit.
		client, err = na.OpenBridgeSessionWithTakeover(ctx, gabs, *configDir, *game, 60*time.Second)
	}
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	defer client.Close()
	stopped, err := client.GamesStop(ctx)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	fmt.Fprintln(stdout, string(stopped.Envelope))
	return 0
}
