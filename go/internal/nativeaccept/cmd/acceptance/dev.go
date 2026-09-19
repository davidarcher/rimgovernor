package main

// acceptance dev (#274): the edit loop over one serve-driven case from a
// checkpoint bundle. The expensive part of iterating on planner or policy
// code is replaying the colony to the state under test, not the Go
// build, so each iteration builds rimgovernor, reloads the bundle's save
// on the kept process (its store and journal restored beside it, never a
// fresh store: #119) and runs the case's Run and Postmortem as a resumed
// run, then waits for Enter (or, with -watch, for a change under the Go
// module) and goes again. The ring is read and never written, no stage
// bundle is captured and no series row is appended; every iteration's
// result.json carries resumed_from and "dev", which cmd/land refuses,
// since everything before the bundle ran under the code that captured it.

import (
	"bufio"
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const devUsage = `
  acceptance dev <area>/<case> -root <dir> [-from <label|dir> -watch -output <dir> -game <id> -headless=false -timeout <d> -budget <d> -stall <d> -evidence capped|full -no-doctor -no-heal -module <go dir>]
    builds rimgovernor, reloads the bundle (-from; default the ring's next entry, then its failed bundle) on the kept
    process and runs the case's Run and Postmortem from there, then waits for Enter (q quits) or, with -watch, for a
    change under the Go module; each iteration writes <output>/dev/<n>/<case> and is refused by cmd/land`

// devWatchPoll is how often -watch scans the module for a change.
const devWatchPoll = time.Second

// devOptions is what parseDev resolved: the case, the run options and
// the loop's own knobs.
type devOptions struct {
	c      cases.Case
	opts   cases.Options
	watch  bool
	module string
}

// parseDev resolves the dev subcommand's case name and flags.
func parseDev(args []string, stderr io.Writer) (devOptions, error) {
	var d devOptions
	var names, flagArgs []string
	for i, a := range args {
		if strings.HasPrefix(a, "-") {
			flagArgs = args[i:]
			break
		}
		names = append(names, a)
	}
	fs := flag.NewFlagSet("dev", flag.ContinueOnError)
	fs.SetOutput(stderr)
	opts := &d.opts
	fs.StringVar(&opts.Root, "root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	fs.StringVar(&opts.Output, "output", "", "output directory; iteration n writes under <output>/dev/<n>/<case> (default <root>/acceptance)")
	fs.StringVar(&opts.GameID, "game", "rimgovernor-trial", "configured game ID")
	fs.BoolVar(&opts.Headless, "headless", true, "use the headless profile (false: windowed)")
	fs.DurationVar(&opts.Timeout, "timeout", cases.DefaultTimeout, "per-iteration safety net")
	fs.DurationVar(&opts.Budget, "budget", 0, "per-iteration wall-clock budget that fails the run (default: the case's own)")
	fs.DurationVar(&opts.Stall, "stall", 0, "stall budget for the shared waits (default: RIMGOVERNOR_ACCEPT_STALL or 3m)")
	var evidence string
	fs.StringVar(&evidence, "evidence", "", "evidence mode: capped (the default) or full")
	fs.StringVar(&opts.From, "from", "", "the bundle to iterate from: a ring label (t+7m, failed) or a bundle directory (default: the ring's next entry, then its failed bundle)")
	fs.BoolVar(&opts.NoDoctor, "no-doctor", false, "skip the doctor preflight")
	fs.BoolVar(&opts.NoHeal, "no-heal", false, "refuse a stale or fixture-less installed mod instead of rebuilding and reinstalling it")
	fs.BoolVar(&d.watch, "watch", false, "rerun when a .go file under the module changes instead of waiting for Enter")
	fs.StringVar(&d.module, "module", "", "the Go module directory rimgovernor is built from (default: the go/ directory of the enclosing checkout)")
	if err := fs.Parse(flagArgs); err != nil {
		return d, err
	}
	if len(fs.Args()) > 0 {
		return d, fmt.Errorf("the case name must precede the flags: %v", fs.Args())
	}
	if len(names) != 1 {
		return d, errors.New("dev takes exactly one case name (see `acceptance list`)")
	}
	if opts.Root == "" {
		return d, errors.New("-root is required")
	}
	if !filepath.IsAbs(opts.Root) {
		return d, fmt.Errorf("-root must be absolute: %s", opts.Root)
	}
	opts.Evidence = na.EvidenceMode(evidence)
	if err := na.SetEvidenceMode(opts.Evidence); err != nil {
		return d, err
	}
	if opts.Output == "" {
		opts.Output = filepath.Join(opts.Root, "acceptance")
	}
	opts.Output = mustAbs(opts.Output)
	if d.module == "" {
		cwd, err := os.Getwd()
		if err != nil {
			return d, err
		}
		repo, ok := na.FindRepo(cwd)
		if !ok {
			return d, errors.New("not inside a checkout: pass -module <dir holding go.mod>")
		}
		d.module = filepath.Join(repo, "go")
	}
	d.module = mustAbs(d.module)
	if _, err := os.Stat(filepath.Join(d.module, "go.mod")); err != nil {
		return d, fmt.Errorf("-module %s holds no go.mod", d.module)
	}
	c, ok := cases.Lookup(names[0])
	if !ok {
		return d, fmt.Errorf("unknown case %q (see `acceptance list`)", names[0])
	}
	if err := c.Lint(); err != nil {
		return d, err
	}
	d.c = c
	opts.Dev = true
	// The iteration's binary is this loop's own build (devBuild).
	opts.Rimgovernor = filepath.Join(opts.Root, "dev", "rimgovernor"+exeSuffix())
	return d, nil
}

// exeSuffix is the platform's executable suffix.
func exeSuffix() string {
	if runtime.GOOS == "windows" {
		return ".exe"
	}
	return ""
}

// dev is the subcommand: preflight once, then build, run and wait until
// the trigger says quit (or stdin ends). The exit code is the last
// iteration's.
func dev(ctx context.Context, args []string, stdin io.Reader, stdout, stderr io.Writer) int {
	d, err := parseDev(args, stderr)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	return devLoop(ctx, d, stdin, stdout)
}

// devLoop runs the iterations; trigger is Enter on stdin or, with -watch,
// a change under the module.
func devLoop(ctx context.Context, d devOptions, stdin io.Reader, stdout io.Writer) int {
	opts := d.opts
	// The preflight reads the binary, so the first build precedes it.
	if err := devBuild(ctx, d.module, opts.Rimgovernor, stdout); err != nil {
		fmt.Fprintln(stdout, err)
		return 1
	}
	if !opts.NoDoctor {
		healed, ok := preflight(ctx, []cases.Case{d.c}, opts, stdout)
		if !ok {
			return 2
		}
		opts.Healed = healed
	}
	opts.Log = stdout
	exit := 0
	lines := bufio.NewScanner(stdin)
	var watched time.Time
	if d.watch {
		watched = newestGoFile(d.module)
	}
	// Iterations continue the numbering an earlier loop left under the
	// output, since every run needs a fresh directory.
	first := 1
	for {
		opts.Attempt = first
		if _, err := os.Stat(opts.CaseOutput(d.c)); err != nil {
			break
		}
		first++
	}
	for n := first; ; n++ {
		opts.Attempt = n
		err := error(nil)
		if n > first {
			err = devBuild(ctx, d.module, opts.Rimgovernor, stdout)
		}
		if err != nil {
			fmt.Fprintln(stdout, err)
			exit = 1
		} else {
			started := time.Now()
			report, code := cases.Execute(ctx, d.c, opts)
			exit = code
			printCase(stdout, d.c, opts, report, code, time.Since(started))
		}
		if ctx.Err() != nil {
			return exit
		}
		if d.watch {
			fmt.Fprintf(stdout, "dev: iteration %d done; watching %s for a change (Ctrl-C quits)\n", n, d.module)
			var ok bool
			if watched, ok = waitForChange(ctx, d.module, watched); !ok {
				return exit
			}
			continue
		}
		fmt.Fprintf(stdout, "dev: iteration %d done; Enter reruns, q quits: ", n)
		if !lines.Scan() || strings.TrimSpace(lines.Text()) == "q" {
			fmt.Fprintln(stdout)
			return exit
		}
	}
}

// devBuild builds ./cmd/rimgovernor from module into target, printing the
// build's timing; a running image (the iteration before, still stopping)
// is moved aside rather than overwritten, as setup does.
func devBuild(ctx context.Context, module, target string, stdout io.Writer) error {
	if err := os.MkdirAll(filepath.Dir(target), 0755); err != nil {
		return err
	}
	started := time.Now()
	fresh := target + ".new"
	cmd := exec.CommandContext(ctx, "go", "build", "-o", fresh, "./cmd/rimgovernor")
	cmd.Dir = module
	if out, err := cmd.CombinedOutput(); err != nil {
		return fmt.Errorf("dev: go build ./cmd/rimgovernor failed (%v):\n%s", err, out)
	}
	if _, err := os.Stat(target); err == nil {
		old := target + ".old"
		os.Remove(old)
		if err := os.Rename(target, old); err != nil {
			return fmt.Errorf("dev: move %s aside (is a service still running from it?): %w", target, err)
		}
		os.Remove(old)
	}
	if err := os.Rename(fresh, target); err != nil {
		return err
	}
	fmt.Fprintf(stdout, "dev: built %s in %s\n", target, time.Since(started).Round(100*time.Millisecond))
	return nil
}

// newestGoFile is the latest modification time of any .go file under
// module (vendor and testdata included: a change there rebuilds too).
func newestGoFile(module string) time.Time {
	var newest time.Time
	_ = filepath.WalkDir(module, func(path string, d fs.DirEntry, err error) error {
		if err != nil {
			return nil
		}
		if d.IsDir() {
			if name := d.Name(); name == ".git" || name == "node_modules" {
				return filepath.SkipDir
			}
			return nil
		}
		if !strings.HasSuffix(d.Name(), ".go") {
			return nil
		}
		if info, err := d.Info(); err == nil && info.ModTime().After(newest) {
			newest = info.ModTime()
		}
		return nil
	})
	return newest
}

// waitForChange polls the module until a .go file is newer than since,
// returning the new mark; ok is false when ctx ended first.
func waitForChange(ctx context.Context, module string, since time.Time) (time.Time, bool) {
	ticker := time.NewTicker(devWatchPoll)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return since, false
		case <-ticker.C:
			if newest := newestGoFile(module); newest.After(since) {
				// A save mid-edit lands as a burst; let it settle before
				// the build reads it.
				time.Sleep(devWatchPoll)
				return newestGoFile(module), true
			}
		}
	}
}
