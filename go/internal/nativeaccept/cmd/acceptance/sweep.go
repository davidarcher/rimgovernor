package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/childproc"
	"github.com/davidarcher/RimGovernor/go/internal/combatdispatch"
	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/combatlab"
)

const sweepUsage = `  acceptance sweep <fixture>... -root <dir> -output <dir> -rimgovernor <bin> [-n 20 | -seeds 1,2,5 -workers 1|2 -against <sweep.json|dir> -restage -record -game <id> -case-timeout <d>]
    runs combatlab/metrics-<fixture> once per seed from one staged checkpoint (seed s reseeds the RNG after the load, -run-seed),
    on 1 or 2 private clones of -root, and writes <output>/<fixture>/sweep.json (per-seed rows plus summary) with the report printed
    deaths and downs first; -against pairs the seeds with an earlier sweep.json (or <dir>/<fixture>/sweep.json) and exits 1 on a survival regression
`

type sweepOptions struct {
	Fixtures    []string
	Root        string
	Output      string
	Rimgovernor string
	GameID      string
	Seeds       []int
	Workers     int
	Against     string
	Restage     bool
	Record      bool
	CaseTimeout time.Duration
}

func parseSweep(args []string, stderr io.Writer) (sweepOptions, error) {
	var opts sweepOptions
	var flagArgs []string
	for i, a := range args {
		if strings.HasPrefix(a, "-") {
			flagArgs = args[i:]
			break
		}
		opts.Fixtures = append(opts.Fixtures, a)
	}
	fs := flag.NewFlagSet("sweep", flag.ContinueOnError)
	fs.SetOutput(stderr)
	n := fs.Int("n", combatlab.BaselineSeeds, "seeds 1..n")
	fs.BoolVar(&opts.Record, "record", false, "write each clean sweep of seeds 1..N over the fixture's committed baseline (run from go/)")
	seeds := fs.String("seeds", "", "comma-separated seeds instead of 1..n")
	fs.StringVar(&opts.Root, "root", "", "absolute worker root to clone for every worker")
	fs.StringVar(&opts.Output, "output", "", "fresh output directory")
	fs.StringVar(&opts.Rimgovernor, "rimgovernor", "", "prebuilt rimgovernor binary (absolute path)")
	fs.StringVar(&opts.GameID, "game", "rimgovernor-trial", "configured game ID")
	fs.IntVar(&opts.Workers, "workers", 1, "private game copies running seeds at once (1 or 2)")
	fs.StringVar(&opts.Against, "against", "", "earlier sweep.json (or a directory of <fixture>/sweep.json) to pair seeds with")
	fs.BoolVar(&opts.Restage, "restage", false, "discard the cached stage bundle and stage again")
	fs.DurationVar(&opts.CaseTimeout, "case-timeout", 0, "per-seed run safety net (default: the runner's)")
	if err := fs.Parse(flagArgs); err != nil {
		return opts, err
	}
	if len(fs.Args()) > 0 {
		return opts, fmt.Errorf("fixture names must precede the flags: %v", fs.Args())
	}
	if len(opts.Fixtures) == 0 {
		return opts, errors.New("sweep needs at least one fixture (lab-choke, ...)")
	}
	if opts.Root == "" || opts.Output == "" || opts.Rimgovernor == "" {
		return opts, errors.New("-root, -output and -rimgovernor are required")
	}
	if !filepath.IsAbs(opts.Root) || !filepath.IsAbs(opts.Rimgovernor) {
		return opts, errors.New("-root and -rimgovernor must be absolute")
	}
	opts.Output = mustAbs(opts.Output)
	if opts.Against != "" {
		opts.Against = mustAbs(opts.Against)
	}
	var err error
	if opts.Seeds, err = sweepSeeds(*n, *seeds); err != nil {
		return opts, err
	}
	if opts.Record && (*seeds != "" || *n != combatlab.BaselineSeeds) {
		return opts, fmt.Errorf("-record writes the %d-seed baseline: omit -n and -seeds", combatlab.BaselineSeeds)
	}
	if opts.Workers < 1 || opts.Workers > combatdispatch.MaxWorkers {
		return opts, fmt.Errorf("-workers must be 1 to %d", combatdispatch.MaxWorkers)
	}
	return opts, nil
}

// sweepSeeds is the explicit list or 1..n; seed 0 means no reseed and is refused.
func sweepSeeds(n int, list string) ([]int, error) {
	var seeds []int
	if list != "" {
		for _, f := range strings.Split(list, ",") {
			s, err := strconv.Atoi(strings.TrimSpace(f))
			if err != nil {
				return nil, fmt.Errorf("-seeds: %w", err)
			}
			seeds = append(seeds, s)
		}
	} else {
		for s := 1; s <= n; s++ {
			seeds = append(seeds, s)
		}
	}
	if len(seeds) == 0 {
		return nil, errors.New("no seeds: -n must be at least 1")
	}
	for _, s := range seeds {
		if s <= 0 {
			return nil, fmt.Errorf("seed %d: seeds are positive (0 is no reseed)", s)
		}
	}
	return seeds, nil
}

// sweep runs the sweeps; the exit code is 1 on a failed seed or a survival
// regression against -against, 2 on a refused invocation.
func sweep(ctx context.Context, args []string, stdout, stderr io.Writer) int {
	opts, err := parseSweep(args, stderr)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	// Taken before any game of the sweep boots, so the count is the box's peers.
	load := combatdispatch.LoadNote()
	var selected []cases.Case
	for _, f := range opts.Fixtures {
		c, ok := cases.Lookup("combatlab/metrics-" + f)
		if !ok {
			fmt.Fprintf(stderr, "unknown fixture %q (no case combatlab/metrics-%s)\n", f, f)
			return 2
		}
		selected = append(selected, c)
	}
	if _, ok := preflight(ctx, selected, cases.Options{Root: opts.Root, Rimgovernor: opts.Rimgovernor, Output: opts.Output, GameID: opts.GameID, NoHeal: true}, stderr); !ok {
		return 2
	}
	self, err := os.Executable()
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	roots := make([]string, opts.Workers)
	for i := range roots {
		dest := filepath.Join(opts.Output, "workers", fmt.Sprint(i+1))
		if _, err := na.IsolatedRoot(opts.Root, dest); err != nil {
			fmt.Fprintf(stderr, "worker %d root: %v\n", i+1, err)
			return 2
		}
		roots[i] = dest
	}
	defer func() {
		for _, r := range roots {
			stopCtx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
			if err := na.StopGame(stopCtx, r, opts.GameID); err != nil {
				fmt.Fprintf(stderr, "stop %s: %v\n", r, err)
			}
			cancel()
		}
	}()
	exit := 0
	for _, c := range selected {
		fixture := strings.TrimPrefix(c.Name, "combatlab/metrics-")
		if code := sweepFixture(ctx, c, fixture, opts, self, load, roots, stdout, stderr); code > exit {
			exit = code
		}
	}
	return exit
}

func sweepFixture(ctx context.Context, c cases.Case, fixture string, opts sweepOptions, self, load string, roots []string, stdout, stderr io.Writer) int {
	dir := filepath.Join(opts.Output, fixture)
	if err := stageForSweep(ctx, c, opts, self, roots, dir); err != nil {
		fmt.Fprintf(stderr, "%s: stage: %v\n", fixture, err)
		return 1
	}
	report, dispatchErr := combatdispatch.Dispatch(ctx, opts.Seeds, opts.Workers, load,
		func(ctx context.Context, worker, seed int) (combatmetrics.SeedRow, error) {
			fmt.Fprintf(stderr, "[worker %d] %s seed %d\n", worker+1, fixture, seed)
			row, err := runSeed(ctx, c, opts, self, roots[worker], dir, worker, seed)
			if err != nil {
				fmt.Fprintf(stderr, "[worker %d] %s seed %d: %v\n", worker+1, fixture, seed, err)
			}
			return row, err
		})
	rows := make([]combatmetrics.SeedRow, 0, len(report.Runs))
	for _, r := range report.Runs {
		row := r.Value
		row.Seed, row.Worker, row.Error = r.Seed, r.Worker+1, r.Error
		rows = append(rows, row)
	}
	result := combatmetrics.NewSweep(fixture, report.Workers, report.Load, report.WallMs, rows)
	path := filepath.Join(dir, "sweep.json")
	if err := writeJSON(path, result); err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	fmt.Fprint(stdout, result.Report())
	fmt.Fprintf(stdout, "  wall %d ms; written %s\n", result.WallMs, path)
	code := 0
	if dispatchErr != nil {
		fmt.Fprintf(stdout, "FAIL\t%s\t%v\n", fixture, dispatchErr)
		code = 1
	}
	if opts.Record {
		if err := recordBaseline(result); err != nil {
			fmt.Fprintf(stdout, "FAIL\t%s\tnot recorded: %v\n", fixture, err)
			return 1
		}
		fmt.Fprintf(stdout, "  recorded baseline %s\n", fixture)
	}
	if opts.Against != "" {
		cmp, err := compareAgainst(opts.Against, result)
		if err != nil {
			fmt.Fprintf(stdout, "FAIL\t%s\t%v\n", fixture, err)
			return 1
		}
		fmt.Fprint(stdout, cmp.Report())
		if err := writeJSON(filepath.Join(dir, "comparison.json"), cmp); err != nil {
			fmt.Fprintln(stderr, err)
		}
		if cmp.Regressed() {
			code = 1
		}
	}
	return code
}

// recordBaseline writes a clean sweep over the fixture's committed baseline in
// the module's source tree (the working directory must be inside go/).
func recordBaseline(s combatmetrics.Sweep) error {
	if d := s.Defects(); len(d) > 0 {
		return fmt.Errorf("sweep is not a baseline: %s", strings.Join(d, "; "))
	}
	dir, err := os.Getwd()
	if err != nil {
		return err
	}
	for {
		if _, err := os.Stat(filepath.Join(dir, "go.mod")); err == nil {
			break
		}
		if parent := filepath.Dir(dir); parent != dir {
			dir = parent
			continue
		}
		return errors.New("no go.mod above the working directory: run -record from go/")
	}
	return writeJSON(filepath.Join(dir, filepath.FromSlash(combatlab.BaselinesDir), s.Fixture+".json"), s)
}

// compareAgainst loads the baseline sweep (a file, or <dir>/<fixture>/sweep.json).
func compareAgainst(against string, cand combatmetrics.Sweep) (combatmetrics.Comparison, error) {
	path := against
	if info, err := os.Stat(against); err == nil && info.IsDir() {
		path = filepath.Join(against, cand.Fixture, "sweep.json")
		if _, err := os.Stat(path); err != nil {
			path = filepath.Join(against, cand.Fixture+".json") // the baselines directory
		}
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return combatmetrics.Comparison{}, err
	}
	base, err := combatmetrics.ParseSweep(data)
	if err != nil {
		return combatmetrics.Comparison{}, fmt.Errorf("%s: %w", path, err)
	}
	return combatmetrics.Compare(base, cand)
}

func writeJSON(path string, v any) error {
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	return os.WriteFile(path, append(data, '\n'), 0o644)
}

// stageForSweep stages the fixture once, on worker 1, and gives every worker
// root its own copy of the bundle (through the shared root, which keeps the
// bundle for the next sweep). Every seed then resumes that one checkpoint.
func stageForSweep(ctx context.Context, c cases.Case, opts sweepOptions, self string, roots []string, dir string) error {
	if !opts.Restage {
		if err := carryStages(opts.Root, roots[0], c.Name); err != nil {
			return err
		}
	}
	argv := []string{"-through", "staged", "-checkpoint-every", "0"}
	if opts.Restage {
		argv = append(argv, "-restage")
	}
	if err := runChild(ctx, c, opts, self, roots[0], filepath.Join(dir, "stage"), argv); err != nil {
		return err
	}
	if _, err := publishStages(opts.Root, roots[0], c.Name); err != nil {
		return err
	}
	for _, r := range roots[1:] {
		if err := carryStages(opts.Root, r, c.Name); err != nil {
			return err
		}
	}
	return nil
}

// runChild runs `acceptance run` for the case on a worker root into output.
func runChild(ctx context.Context, c cases.Case, opts sweepOptions, self, root, output string, extra []string) error {
	argv := []string{"run", c.Name, "-root", root, "-output", output, "-game", opts.GameID, "-no-doctor", "-no-series", "-rimgovernor", opts.Rimgovernor}
	if opts.CaseTimeout > 0 {
		argv = append(argv, "-timeout", opts.CaseTimeout.String())
	}
	argv = append(argv, extra...)
	if err := os.MkdirAll(output, 0o755); err != nil {
		return err
	}
	log, err := os.Create(output + ".log")
	if err != nil {
		return err
	}
	defer log.Close()
	cmd := exec.CommandContext(ctx, self, argv...)
	childproc.HideConsole(cmd)
	cmd.Env = append(os.Environ(), na.KeepGameEnv+"=1")
	cmd.Stdout, cmd.Stderr = log, log
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("%s: %w (see %s)", c.Name, err, log.Name())
	}
	return nil
}

// runSeed runs one seed on a worker root and reads the seed's row from its
// bundle: the metrics and the staging digest and reseed draws it reported.
func runSeed(ctx context.Context, c cases.Case, opts sweepOptions, self, root, dir string, worker, seed int) (combatmetrics.SeedRow, error) {
	row := combatmetrics.SeedRow{Seed: seed, Worker: worker + 1}
	output := filepath.Join(dir, fmt.Sprintf("seed-%d", seed))
	runErr := runChild(ctx, c, opts, self, root, output, []string{"-checkpoint-every", "0", "-run-seed", strconv.Itoa(seed)})
	caseDir := cases.Options{Output: output}.CaseOutput(c)
	if data, err := os.ReadFile(filepath.Join(caseDir, "result.json")); err == nil {
		var result struct {
			Digest string `json:"staged_digest"`
			Draws  []int  `json:"run_seed_draws"`
		}
		if json.Unmarshal(data, &result) == nil {
			row.StagedDigest, row.RunSeedDraws = result.Digest, result.Draws
		}
	}
	if runErr != nil {
		return row, runErr
	}
	data, err := os.ReadFile(filepath.Join(caseDir, combatlab.MetricsFile))
	if err != nil {
		return row, err
	}
	var m combatmetrics.Metrics
	if err := json.Unmarshal(data, &m); err != nil {
		return row, fmt.Errorf("%s: %w", combatlab.MetricsFile, err)
	}
	row.Metrics = &m
	return row, nil
}
