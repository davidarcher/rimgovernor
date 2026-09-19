package main

// acceptance suite runs a set of cases across N private game copies at
// once (issue #140, absorbing the former suiteaccept): each worker is its
// own disposable root (na.IsolatedRoot off -root, so its own GABS state,
// config and profile) launching the same game binary, chains its cases on
// one kept process (na.KeepGameEnv forced on) and stops it when its queue
// is empty. Every case keeps its own output directory and result.json; the
// suite's result.json lists them with exit code, wall and boot time, and
// passes only when every case did.
//
// The set is the registry (-all), registry names (-cases a,b) or a JSON
// suite file (-suite) whose rows name registry cases; every row runs
// through `acceptance run`, and a case that hosts a service (Serve or
// Service) receives -rimgovernor. "acceptance" labels the criterion a row
// stands for and is echoed into its report row. Every row runs fresh
// (-fresh, no checkpoint ring): a suite is the landing gate's form, and a
// resumed pass is not a pass (#249); a row whose result.json carries
// resumed_from fails the suite.
//
// Scheduling: one shared queue in three tiers. Bridge-only cases that keep
// the process come first; cases that end or replace it (NoKeep: a
// shutdown, a fault, an owned lifecycle; Rendered: a windowed profile the
// headless worker cannot serve) follow, so the kept process is reused as
// long as possible; serve-driven cases (Serve or Service) are last, so a
// worker that has hosted a service never runs a bridge-only case on that
// process afterwards (#119). Within each tier the queue is longest-first
// by the -baseline suite's wall times (untimed cases first, as if long),
// so a slow case does not land last and idle the other workers.
//
// -baseline also drives regression flagging: a case whose run time (wall
// time net of the game boot, so queue placement does not count) is more
// than RegressionRatio times its baseline's and RegressionFloorMs over it
// is listed under "regressions" (the list never fails the suite on its
// own) and the report carries the sum of case wall times beside the
// baseline's.
//
// Every row's metrics block (result.json "metrics", #297) is appended by
// the run to the series at -series (default <output>/../metrics.jsonl,
// shared by successive suites beside each other), and the metrics past
// their rule against the series' trailing median (na.Drift) are listed on
// the row under "drift" and, over all rows, under the suite's "drift".

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
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/childproc"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// RegressionRatio is the run-time growth over the baseline that flags a
// case as a regression, and RegressionFloorMs the absolute growth it must
// also exceed: on a ~10s case the peer load of the other workers moves
// the time by more than 25% run to run (#176).
const (
	RegressionRatio   = 1.25
	RegressionFloorMs = 5000
)

// entry is one row of a suite: a registry case.
type entry struct {
	Name string `json:"name"`
	// Acceptance names the criterion this row stands for (documentation
	// echoed into the suite report), if the suite file says.
	Acceptance string `json:"acceptance,omitempty"`

	// registered is the registry case the row resolves to.
	registered *cases.Case
}

// serveDriven reports whether the row hosts a `rimgovernor serve` process.
func (e entry) serveDriven() bool {
	return e.registered != nil && (e.registered.Serve != nil || e.registered.Service)
}

// suiteOptions is the suite subcommand's resolved configuration.
type suiteOptions struct {
	Root, Output, Rimgovernor, Baseline, GameID string
	// Series is the metrics series the rows append to; NoSeries skips it.
	Series   string
	NoSeries bool
	Workers  int
	Timeout  time.Duration
	// CaseTimeout, Budget and Stall pass through to `acceptance run`;
	// zero leaves the runner's defaults.
	CaseTimeout, Budget, Stall time.Duration
	// Evidence passes through to `acceptance run -evidence`; empty leaves
	// the runner's default.
	Evidence string
}

const suiteUsage = `  acceptance suite (-all | -cases a,b,... | -suite file.json) -root <dir> -output <dir> [-workers N -baseline <result.json> -series <metrics.jsonl> -no-series -rimgovernor <bin> -game <id> -timeout <d> -case-timeout <d> -budget <d> -stall <d> -evidence capped|full]`

// parseSuite resolves the suite subcommand's flags into the list of rows
// (in file order, before scheduling) and the options.
func parseSuite(args []string, stderr io.Writer) ([]entry, suiteOptions, error) {
	fs := flag.NewFlagSet("suite", flag.ContinueOnError)
	fs.SetOutput(stderr)
	var opts suiteOptions
	var all bool
	var names, suite string
	fs.BoolVar(&all, "all", false, "run every registered case")
	fs.StringVar(&names, "cases", "", "comma-separated registry case names")
	fs.StringVar(&suite, "suite", "", "JSON suite file: array of {name, acceptance}; names are registry cases")
	fs.StringVar(&opts.Root, "root", "", "absolute worker root to clone for every worker (e.g. .rimgovernor/bridge)")
	fs.StringVar(&opts.Output, "output", "", "fresh output directory")
	fs.StringVar(&opts.Rimgovernor, "rimgovernor", "", "prebuilt rimgovernor binary (absolute path) passed to the cases that host a service")
	fs.StringVar(&opts.Baseline, "baseline", "", "earlier suite result.json: orders the queue longest-first and flags regressions")
	fs.StringVar(&opts.Series, "series", "", "append-only metrics series every row's block is appended to (default <output>/../metrics.jsonl)")
	fs.BoolVar(&opts.NoSeries, "no-series", false, "leave the metrics series alone")
	fs.StringVar(&opts.GameID, "game", "rimgovernor-trial", "configured game ID")
	fs.IntVar(&opts.Workers, "workers", 2, "private game copies to run at once")
	fs.DurationVar(&opts.Timeout, "timeout", 2*time.Hour, "overall suite timeout")
	fs.DurationVar(&opts.CaseTimeout, "case-timeout", 0, "per-case safety net (default: the runner's)")
	fs.DurationVar(&opts.Budget, "budget", 0, "per-case budget override")
	fs.DurationVar(&opts.Stall, "stall", 0, "stall budget override")
	fs.StringVar(&opts.Evidence, "evidence", "", "evidence mode passed to every case: capped (default) or full")
	if err := fs.Parse(args); err != nil {
		return nil, opts, err
	}
	if len(fs.Args()) > 0 {
		return nil, opts, fmt.Errorf("unexpected arguments: %v", fs.Args())
	}
	selectors := 0
	for _, set := range []bool{all, names != "", suite != ""} {
		if set {
			selectors++
		}
	}
	if selectors != 1 {
		return nil, opts, errors.New("exactly one of -all, -cases or -suite is required")
	}
	if opts.Root == "" || opts.Output == "" {
		return nil, opts, errors.New("-root and -output are required")
	}
	if !filepath.IsAbs(opts.Root) {
		return nil, opts, fmt.Errorf("-root must be absolute: %s", opts.Root)
	}
	if opts.Workers < 1 {
		return nil, opts, errors.New("-workers must be at least 1")
	}
	opts.Output = mustAbs(opts.Output)
	if opts.Series == "" {
		opts.Series = na.SeriesPath(opts.Output)
	}
	opts.Series = mustAbs(opts.Series)
	var list []entry
	switch {
	case all:
		for _, c := range cases.All() {
			list = append(list, entry{Name: c.Name})
		}
	case names != "":
		for _, name := range strings.Split(names, ",") {
			if name = strings.TrimSpace(name); name != "" {
				list = append(list, entry{Name: name})
			}
		}
	default:
		data, err := os.ReadFile(suite)
		if err != nil {
			return nil, opts, err
		}
		if err := json.Unmarshal(data, &list); err != nil {
			return nil, opts, fmt.Errorf("-suite must be a JSON array of {name, acceptance}: %w", err)
		}
	}
	if err := resolveEntries(list); err != nil {
		return nil, opts, err
	}
	return list, opts, nil
}

// resolveEntries binds each row to its registry case and rejects unknown,
// duplicate or empty names.
func resolveEntries(list []entry) error {
	if len(list) == 0 {
		return errors.New("no cases listed")
	}
	seen := map[string]bool{}
	for i := range list {
		e := &list[i]
		if e.Name == "" || seen[e.Name] {
			return fmt.Errorf("case names must be unique and non-empty, got %q", e.Name)
		}
		seen[e.Name] = true
		c, ok := cases.Lookup(e.Name)
		if !ok {
			return fmt.Errorf("unknown case %q (see `acceptance list`)", e.Name)
		}
		e.registered = &c
	}
	return nil
}

// baseline is what an earlier suite's result.json contributes: wall and
// boot times by case name.
type baseline struct {
	path string
	wall map[string]float64
	boot map[string]float64
}

func loadBaseline(path string) (*baseline, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("-baseline: %w", err)
	}
	type row struct {
		Name   string  `json:"name"`
		WallMs float64 `json:"wall_ms"`
		BootMs float64 `json:"boot_ms"`
	}
	var prior struct {
		Cases []row `json:"cases"`
	}
	if err := json.Unmarshal(data, &prior); err != nil {
		return nil, fmt.Errorf("-baseline: %s is not a suite result.json: %w", path, err)
	}
	b := &baseline{path: path, wall: map[string]float64{}, boot: map[string]float64{}}
	for _, r := range prior.Cases {
		b.wall[r.Name] = r.WallMs
		b.boot[r.Name] = r.BootMs
	}
	return b, nil
}

// rank is the queue key: untimed rows sort as if longest.
func (b *baseline) rank(e entry) float64 {
	if b != nil {
		if ms, ok := b.wall[e.Name]; ok {
			return ms
		}
	}
	return -1
}

// tier is the row's scheduling tier: 0 keeps the process, 1 ends or
// replaces it (NoKeep, Rendered), 2 hosts a service.
func (e entry) tier() int {
	switch {
	case e.serveDriven():
		return 2
	case e.registered != nil && (e.registered.NoKeep || e.registered.Rendered):
		return 1
	}
	return 0
}

// schedule orders the queue by tier (kept-process rows, process-ending
// rows, serve-driven rows), each tier longest-first by the baseline. The
// sort is stable, so equal or untimed rows keep their listed order.
func schedule(list []entry, b *baseline) {
	sort.SliceStable(list, func(i, j int) bool {
		if ti, tj := list[i].tier(), list[j].tier(); ti != tj {
			return ti < tj
		}
		ri, rj := b.rank(list[i]), b.rank(list[j])
		if ri < 0 || rj < 0 {
			return ri < 0 && rj >= 0
		}
		return ri > rj
	})
}

// regression is one case whose run time (wall time net of boot) grew over
// its baseline's by more than RegressionRatio and RegressionFloorMs.
type regression struct {
	Name          string  `json:"name"`
	WallMs        int64   `json:"wall_ms"`
	BaselineMs    int64   `json:"baseline_ms"`
	RunMs         int64   `json:"run_ms"`
	BaselineRunMs int64   `json:"baseline_run_ms"`
	Ratio         float64 `json:"ratio"`
}

// regressions lists the rows over their baseline, in queue order, and the
// baseline's total wall time over the rows it timed.
func regressions(rows []map[string]any, b *baseline) (list []regression, baselineTotal int64) {
	list = []regression{}
	for _, row := range rows {
		name := na.AsString(row["name"])
		base, ok := b.wall[name]
		if !ok || base <= 0 {
			continue
		}
		baselineTotal += int64(base)
		baseRun := base - b.boot[name]
		wall, _ := row["wall_ms"].(int64)
		boot, _ := row["boot_ms"].(int64)
		run := float64(wall - boot)
		if baseRun > 0 && run > baseRun*RegressionRatio && run-baseRun > RegressionFloorMs {
			list = append(list, regression{Name: name, WallMs: wall, BaselineMs: int64(base), RunMs: int64(run), BaselineRunMs: int64(baseRun), Ratio: run / baseRun})
		}
	}
	return list, baselineTotal
}

// driftFlags collects the rows' drift flags (each row's result.json
// "drift", as the run computed them) in queue order.
func driftFlags(rows []map[string]any) []na.DriftFlag {
	flags := []na.DriftFlag{}
	for _, row := range rows {
		list, _ := row["drift"].([]any)
		for _, item := range list {
			m, ok := item.(map[string]any)
			if !ok {
				continue
			}
			flags = append(flags, na.DriftFlag{
				Case: na.AsString(m["case"]), Metric: na.AsString(m["metric"]),
				Value: na.AsNumber(m["value"]), Median: na.AsNumber(m["median"]),
				Ratio: na.AsNumber(m["ratio"]), Samples: int(na.AsNumber(m["samples"])),
			})
		}
	}
	return flags
}

// runSuite executes the rows across the workers and writes the suite's
// result.json under opts.Output; it returns the process exit code.
func runSuite(ctx context.Context, list []entry, opts suiteOptions, stderr io.Writer) int {
	if err := os.MkdirAll(opts.Output, 0755); err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	if entries, _ := os.ReadDir(opts.Output); len(entries) > 0 {
		fmt.Fprintln(stderr, "-output must be a fresh, empty directory")
		return 2
	}
	if opts.Workers > len(list) {
		opts.Workers = len(list)
	}
	report := na.NewReport(fmt.Sprintf("%d acceptance cases across %d private game copies, one kept process per worker", len(list), opts.Workers), true)
	report["workers"] = opts.Workers
	var b *baseline
	if opts.Baseline != "" {
		var err error
		if b, err = loadBaseline(opts.Baseline); err != nil {
			report["error"] = err.Error()
			return report.Finalize(opts.Output)
		}
		report["baseline"] = opts.Baseline
	}
	schedule(list, b)
	self, err := os.Executable()
	if err != nil {
		report["error"] = fmt.Sprintf("own executable: %v", err)
		return report.Finalize(opts.Output)
	}
	ctx, cancel := context.WithTimeout(ctx, opts.Timeout)
	defer cancel()

	// Worker roots: clones of -root, each launching its own process of the
	// shared installation.
	roots := make([]string, opts.Workers)
	for i := range roots {
		dest := filepath.Join(opts.Output, "workers", fmt.Sprint(i+1))
		if _, err := na.IsolatedRoot(opts.Root, dest); err != nil {
			report["error"] = fmt.Sprintf("worker %d root: %v", i+1, err)
			return report.Finalize(opts.Output)
		}
		roots[i] = dest
	}
	report["worker_roots"] = roots

	queue := make(chan entry)
	rows := make([]map[string]any, len(list))
	index := map[string]int{}
	for i, e := range list {
		index[e.Name] = i
	}
	var mu sync.Mutex
	var wg sync.WaitGroup
	for w := range roots {
		wg.Add(1)
		go func(worker int, workerRoot string) {
			defer wg.Done()
			for e := range queue {
				row := runEntry(ctx, e, opts, self, workerRoot, worker+1, stderr)
				mu.Lock()
				rows[index[e.Name]] = row
				mu.Unlock()
			}
			stopCtx, stopCancel := context.WithTimeout(context.Background(), 2*time.Minute)
			defer stopCancel()
			if err := na.StopGame(stopCtx, workerRoot, opts.GameID); err != nil {
				mu.Lock()
				report[fmt.Sprintf("worker_%d_stop_error", worker+1)] = err.Error()
				mu.Unlock()
			}
		}(w, roots[w])
	}
	started := time.Now()
	for _, e := range list {
		queue <- e
	}
	close(queue)
	wg.Wait()
	report["wall_ms"] = time.Since(started).Milliseconds()
	report["cases"] = rows
	passed, failed := 0, []string{}
	var total int64
	for _, row := range rows {
		wall, _ := row["wall_ms"].(int64)
		total += wall
		if ok, _ := row["passed"].(bool); ok {
			passed++
		} else {
			failed = append(failed, na.AsString(row["name"]))
		}
	}
	report["cases_passed"] = passed
	report["total_case_ms"] = total
	if b != nil {
		list, baselineTotal := regressions(rows, b)
		report["regressions"] = list
		report["baseline_total_ms"] = baselineTotal
		if len(list) > 0 {
			names := make([]string, 0, len(list))
			for _, r := range list {
				names = append(names, fmt.Sprintf("%s %.2fx", r.Name, r.Ratio))
			}
			fmt.Fprintf(stderr, "regressions (>%.0f%% and >%ds over baseline, net of boot): %s\n", (RegressionRatio-1)*100, RegressionFloorMs/1000, strings.Join(names, ", "))
		}
	}
	if !opts.NoSeries {
		report["series"] = opts.Series
		flags := driftFlags(rows)
		report["drift"] = flags
		if len(flags) > 0 {
			fmt.Fprintf(stderr, "drift (past the metric's rule against the trailing median of %s):\n", opts.Series)
			for _, f := range flags {
				fmt.Fprintf(stderr, "  %s\n", f)
			}
		}
	}
	if len(failed) > 0 {
		report["failed"] = failed
		report["error"] = fmt.Sprintf("%d of %d cases failed: %s", len(failed), len(rows), strings.Join(failed, ", "))
	} else {
		report["passed"] = true
	}
	return report.Finalize(opts.Output)
}

// entryCommand is the argv a row runs as on a worker (`acceptance run`
// on this executable) and where the row's result.json lands.
func entryCommand(e entry, opts suiteOptions, self, workerRoot string) (argv []string, output string) {
	argv = []string{self, "run", e.Name, "-root", workerRoot, "-output", opts.Output, "-game", opts.GameID, "-fresh", "-checkpoint-every", "0"}
	if opts.NoSeries {
		argv = append(argv, "-no-series")
	} else if opts.Series != "" {
		argv = append(argv, "-series", opts.Series)
	}
	if opts.Rimgovernor != "" && e.serveDriven() {
		argv = append(argv, "-rimgovernor", opts.Rimgovernor)
	}
	for _, f := range []struct {
		name string
		d    time.Duration
	}{{"-timeout", opts.CaseTimeout}, {"-budget", opts.Budget}, {"-stall", opts.Stall}} {
		if f.d > 0 {
			argv = append(argv, f.name, f.d.String())
		}
	}
	if opts.Evidence != "" {
		argv = append(argv, "-evidence", opts.Evidence)
	}
	return argv, cases.Options{Output: opts.Output}.CaseOutput(*e.registered)
}

// runEntry executes one row on a worker's root with the game kept between
// rows, and returns its report row: exit code, wall and boot time, output
// directory and the row's own result.json verdict.
func runEntry(ctx context.Context, e entry, opts suiteOptions, self, workerRoot string, worker int, stderr io.Writer) map[string]any {
	argv, output := entryCommand(e, opts, self, workerRoot)
	row := map[string]any{"name": e.Name, "worker": worker, "output": output, "argv": argv, "serve": e.serveDriven(), "passed": false}
	if e.Acceptance != "" {
		row["acceptance"] = e.Acceptance
	}
	cmd := exec.CommandContext(ctx, argv[0], argv[1:]...)
	childproc.HideConsole(cmd)
	// Explicit even though it is the default: a caller's opt-out must not
	// leak into the workers, which stop their game once at the end.
	cmd.Env = append(os.Environ(), na.KeepGameEnv+"=1")
	logPath := output + ".log"
	if err := os.MkdirAll(filepath.Dir(logPath), 0755); err != nil {
		row["error"] = err.Error()
		return row
	}
	logFile, err := os.Create(logPath)
	if err != nil {
		row["error"] = err.Error()
		return row
	}
	defer logFile.Close()
	cmd.Stdout, cmd.Stderr = logFile, logFile
	fmt.Fprintf(stderr, "[worker %d] %s\n", worker, e.Name)
	started := time.Now()
	runErr := cmd.Run()
	row["wall_ms"] = time.Since(started).Milliseconds()
	row["log"] = logPath
	var exitErr *exec.ExitError
	switch {
	case runErr == nil:
		row["exit"] = 0
	case errors.As(runErr, &exitErr):
		row["exit"] = exitErr.ExitCode()
	default:
		row["exit"] = -1
		row["error"] = runErr.Error()
	}
	if data, err := os.ReadFile(filepath.Join(output, "result.json")); err == nil {
		var result map[string]any
		if json.Unmarshal(data, &result) == nil {
			row["passed"], _ = result["passed"].(bool)
			if e := na.AsString(result["error"]); e != "" {
				row["error"] = e
			}
			if resumed, ok := result["resumed_from"]; ok {
				row["passed"], row["resumed_from"] = false, resumed
				row["error"] = "resumed from a checkpoint: a landing run must pass from scratch"
			}
			row["game_reuse"] = result["game_reuse"]
			if ms, ok := result["boot_ms"].(float64); ok {
				row["boot_ms"] = int64(ms)
			}
			if m, ok := na.MetricsOf(result); ok {
				row["metrics"] = m
			}
			if flags, ok := result["drift"].([]any); ok && len(flags) > 0 {
				row["drift"] = flags
			}
		}
	} else if row["error"] == nil {
		row["error"] = "no result.json: " + err.Error()
	}
	fmt.Fprintf(stderr, "[worker %d] %s exit=%v passed=%v %s\n", worker, e.Name, row["exit"], row["passed"], time.Since(started).Round(time.Second))
	return row
}

func mustAbs(path string) string {
	if abs, err := filepath.Abs(path); err == nil {
		return abs
	}
	return path
}
