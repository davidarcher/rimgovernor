// Command suiteaccept runs a list of native acceptance harnesses across N
// private game copies at once (issue #91): each worker is its own disposable
// root (na.IsolatedRoot off -root, so its own GABS state, config and
// profile) launching the same game binary, and each worker chains its
// harnesses on one kept process (RIMGOVERNOR_ACCEPT_KEEP_GAME) and stops it
// when its queue is empty. Every harness keeps its own output directory and
// result.json; the suite's result.json lists them with exit code, wall
// time and the harness's own error, and passes only when every harness
// did.
//
// The workers share the RimWorld installation -root points at, so the
// installed mod build must carry every fixture the listed harnesses need,
// and every harness must still fit the clock's step budget with N-1 peer
// games on the machine (#73 measured three).
//
// Harnesses are named on the command line (-harnesses a,b,c: <bin>/a.exe
// -root <worker> -output <output>/a) or listed in a JSON suite file
// (-suite: [{"name": "...", "binary": "...", "args": ["..."],
// "acceptance": "..."}], binary defaulting to <bin>/<name>.exe and a
// relative binary resolving under <bin>, args appended after
// -root/-output, acceptance an optional label echoed into the row so a
// suite can name the acceptance criterion each run stands for).
// -rimgovernor is passed to any harness whose args mention it as
// "{rimgovernor}".
//
// suites/issue-6-matrix.json is issue #6's cross-slice acceptance matrix:
// one row per criterion in the issue text, each mapped to the harness and
// scenario that exercises it, so the whole epic is re-accepted with one
// suite run. suite_test.go guards the file against typos and against a
// criterion losing its row.
//
// -order names an earlier suite's result.json: harnesses then start
// longest-first by that run's wall times (ones it did not time go first,
// as if long), so a slow harness does not land last and leave the other
// workers idle for its whole run.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

type harness struct {
	Name   string   `json:"name"`
	Binary string   `json:"binary,omitempty"`
	Args   []string `json:"args,omitempty"`
	// Acceptance names the criterion this run stands for (documentation
	// echoed into the suite report), if the suite file says.
	Acceptance string `json:"acceptance,omitempty"`
}

func main() {
	root := flag.String("root", "", "absolute worker root to clone for every worker (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory")
	bin := flag.String("bin", "", "directory holding the harness binaries (<bin>/<name>.exe)")
	harnesses := flag.String("harnesses", "", "comma-separated harness names to run with the default arguments")
	suite := flag.String("suite", "", "JSON suite file (array of {name, binary, args}); mutually exclusive with -harnesses")
	workers := flag.Int("workers", 2, "private game copies to run at once")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	order := flag.String("order", "", "earlier suite result.json whose wall times order the queue longest-first")
	rimgovernor := flag.String("rimgovernor", "", "prebuilt rimgovernor binary substituted for {rimgovernor} in suite args")
	timeout := flag.Duration("timeout", 2*time.Hour, "overall suite timeout")
	flag.Parse()
	if *root == "" || *output == "" || *bin == "" {
		fmt.Fprintln(os.Stderr, "-root, -output and -bin are required")
		os.Exit(2)
	}
	if (*harnesses == "") == (*suite == "") {
		fmt.Fprintln(os.Stderr, "exactly one of -harnesses or -suite is required")
		os.Exit(2)
	}
	if *workers < 1 {
		fmt.Fprintln(os.Stderr, "-workers must be at least 1")
		os.Exit(2)
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	list, err := loadSuite(*harnesses, *suite)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if *workers > len(list) {
		*workers = len(list)
	}
	binDir := mustAbs(*bin)
	for i := range list {
		list[i].Binary = resolveBinary(binDir, list[i])
		for j, a := range list[i].Args {
			list[i].Args[j] = strings.ReplaceAll(a, "{rimgovernor}", *rimgovernor)
		}
	}

	report := na.NewReport(fmt.Sprintf("%d native acceptance harnesses across %d private game copies, one kept process per worker", len(list), *workers), true)
	report["workers"] = *workers
	if *order != "" {
		if err := orderLongestFirst(list, *order); err != nil {
			report["error"] = err.Error()
			os.Exit(report.Finalize(*output))
		}
		report["order"] = *order
	}
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()

	// Worker roots: clones of -root, each launching its own process of the
	// shared installation.
	roots := make([]string, *workers)
	for i := range roots {
		dest := filepath.Join(mustAbs(*output), "workers", fmt.Sprint(i+1))
		if _, err := na.IsolatedRoot(*root, dest); err != nil {
			report["error"] = fmt.Sprintf("worker %d root: %v", i+1, err)
			os.Exit(report.Finalize(*output))
		}
		roots[i] = dest
	}
	report["worker_roots"] = roots

	queue := make(chan harness)
	rows := make([]map[string]any, len(list))
	index := map[string]int{}
	for i, h := range list {
		index[h.Name] = i
	}
	var mu sync.Mutex
	var wg sync.WaitGroup
	for w := range roots {
		wg.Add(1)
		go func(worker int, workerRoot string) {
			defer wg.Done()
			for h := range queue {
				row := run(ctx, h, workerRoot, filepath.Join(mustAbs(*output), h.Name), worker+1)
				mu.Lock()
				rows[index[h.Name]] = row
				mu.Unlock()
			}
			stopCtx, stopCancel := context.WithTimeout(context.Background(), 2*time.Minute)
			defer stopCancel()
			if err := na.StopGame(stopCtx, workerRoot, *game); err != nil {
				mu.Lock()
				report[fmt.Sprintf("worker_%d_stop_error", worker+1)] = err.Error()
				mu.Unlock()
			}
		}(w, roots[w])
	}
	started := time.Now()
	for _, h := range list {
		queue <- h
	}
	close(queue)
	wg.Wait()
	report["wall_ms"] = time.Since(started).Milliseconds()
	report["harnesses"] = rows
	passed, failed := 0, []string{}
	for _, row := range rows {
		if ok, _ := row["passed"].(bool); ok {
			passed++
		} else {
			failed = append(failed, na.AsString(row["name"]))
		}
	}
	report["harnesses_passed"] = passed
	if len(failed) > 0 {
		report["failed"] = failed
		report["error"] = fmt.Sprintf("%d of %d harnesses failed: %s", len(failed), len(rows), strings.Join(failed, ", "))
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

// run executes one harness on a worker's root with the game kept between
// harnesses, and returns its row: exit code, wall time, output directory and
// the harness's own result.json verdict.
func run(ctx context.Context, h harness, workerRoot, output string, worker int) map[string]any {
	row := map[string]any{"name": h.Name, "worker": worker, "output": output, "passed": false}
	if h.Acceptance != "" {
		row["acceptance"] = h.Acceptance
	}
	args := append([]string{"-root", workerRoot, "-output", output}, h.Args...)
	row["argv"] = append([]string{h.Binary}, args...)
	cmd := exec.CommandContext(ctx, h.Binary, args...)
	// Explicit even though it is the default: a caller's opt-out must not
	// leak into the workers, which stop their game once at the end.
	cmd.Env = append(os.Environ(), na.KeepGameEnv+"=1")
	logPath := output + ".log"
	logFile, err := os.Create(logPath)
	if err != nil {
		row["error"] = err.Error()
		return row
	}
	defer logFile.Close()
	cmd.Stdout, cmd.Stderr = logFile, logFile
	fmt.Fprintf(os.Stderr, "[worker %d] %s\n", worker, h.Name)
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
			row["game_reuse"] = result["game_reuse"]
		}
	} else if row["error"] == nil {
		row["error"] = "no result.json: " + err.Error()
	}
	fmt.Fprintf(os.Stderr, "[worker %d] %s exit=%v passed=%v %s\n", worker, h.Name, row["exit"], row["passed"], time.Since(started).Round(time.Second))
	return row
}

// resolveBinary names the harness executable: <bin>/<name>.exe by default,
// a relative binary under <bin>, an absolute one as given.
func resolveBinary(binDir string, h harness) string {
	switch {
	case h.Binary == "":
		return filepath.Join(binDir, h.Name+".exe")
	case filepath.IsAbs(h.Binary):
		return h.Binary
	default:
		return filepath.Join(binDir, h.Binary)
	}
}

func loadSuite(names, path string) ([]harness, error) {
	var list []harness
	if path != "" {
		data, err := os.ReadFile(path)
		if err != nil {
			return nil, err
		}
		if err := json.Unmarshal(data, &list); err != nil {
			return nil, fmt.Errorf("-suite must be a JSON array of {name, binary, args}: %w", err)
		}
	} else {
		for _, name := range strings.Split(names, ",") {
			if name = strings.TrimSpace(name); name != "" {
				list = append(list, harness{Name: name})
			}
		}
	}
	seen := map[string]bool{}
	for _, h := range list {
		if h.Name == "" || seen[h.Name] {
			return nil, fmt.Errorf("harness names must be unique and non-empty, got %q", h.Name)
		}
		seen[h.Name] = true
	}
	if len(list) == 0 {
		return nil, errors.New("no harnesses listed")
	}
	return list, nil
}

// orderLongestFirst sorts list by the wall times an earlier suite's
// result.json recorded, longest first; harnesses that run did not time sort
// before every timed one. The sort is stable, so equal or untimed ones keep
// their listed order.
func orderLongestFirst(list []harness, path string) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return fmt.Errorf("-order: %w", err)
	}
	var prior struct {
		Harnesses []struct {
			Name   string  `json:"name"`
			WallMs float64 `json:"wall_ms"`
		} `json:"harnesses"`
	}
	if err := json.Unmarshal(data, &prior); err != nil {
		return fmt.Errorf("-order: %s is not a suite result.json: %w", path, err)
	}
	wall := map[string]float64{}
	for _, h := range prior.Harnesses {
		wall[h.Name] = h.WallMs
	}
	rank := func(h harness) float64 {
		if ms, ok := wall[h.Name]; ok {
			return ms
		}
		return -1 // untimed: treat as longest
	}
	sort.SliceStable(list, func(i, j int) bool {
		a, b := rank(list[i]), rank(list[j])
		if a < 0 || b < 0 {
			return a < 0 && b >= 0
		}
		return a > b
	})
	return nil
}

func mustAbs(path string) string {
	if abs, err := filepath.Abs(path); err == nil {
		return abs
	}
	return path
}
