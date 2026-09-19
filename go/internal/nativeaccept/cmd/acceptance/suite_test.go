package main

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func writeBaseline(t *testing.T, body string) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "result.json")
	if err := os.WriteFile(path, []byte(body), 0644); err != nil {
		t.Fatal(err)
	}
	return path
}

func names(list []entry) string {
	var out []string
	for _, e := range list {
		out = append(out, e.Name)
	}
	return strings.Join(out, " ")
}

// The queue is kept-process cases first, then the ones that end the
// process, then serve-driven, each tier longest-first by the baseline with
// untimed rows ahead as if long.
func TestScheduleServeLastLongestFirst(t *testing.T) {
	b, err := loadBaseline(writeBaseline(t, `{"cases":[{"name":"a","wall_ms":10},{"name":"b","wall_ms":30},{"name":"c","wall_ms":20},{"name":"s1","wall_ms":50},{"name":"s2","wall_ms":90}]}`))
	if err != nil {
		t.Fatal(err)
	}
	serveCase := cases.Case{Name: "x/serve", Serve: &cases.ServeSpec{}}
	serviceCase := cases.Case{Name: "x/service", Service: true}
	noKeep := cases.Case{Name: "x/shutdown", NoKeep: true}
	rendered := cases.Case{Name: "x/video", Rendered: true}
	bridge := cases.Case{Name: "x/bridge"}
	list := []entry{
		{Name: "s1", registered: &serviceCase},
		{Name: "x/shutdown", registered: &noKeep},
		{Name: "a", registered: &bridge}, {Name: "b", registered: &bridge},
		{Name: "x/serve", registered: &serveCase},
		{Name: "c", registered: &bridge}, {Name: "new", registered: &bridge},
		{Name: "s2", registered: &serveCase},
		{Name: "x/video", registered: &rendered},
	}
	schedule(list, b)
	if got := names(list); got != "new b c a x/shutdown x/video x/serve s2 s1" {
		t.Errorf("order = %q", got)
	}
	// Without a baseline the listed order holds within each tier.
	list = []entry{{Name: "s1", registered: &serveCase}, {Name: "a", registered: &bridge}, {Name: "b", registered: &bridge}}
	schedule(list, nil)
	if got := names(list); got != "a b s1" {
		t.Errorf("unordered = %q", got)
	}
	if _, err := loadBaseline(filepath.Join(t.TempDir(), "missing.json")); err == nil {
		t.Error("expected an error for a missing baseline")
	}
}

func TestRegressionsFlagOverRatioAndFloorNetOfBoot(t *testing.T) {
	b, err := loadBaseline(writeBaseline(t, `{"cases":[
		{"name":"a","wall_ms":10000},
		{"name":"b","wall_ms":10000},
		{"name":"c","wall_ms":10000},
		{"name":"d","wall_ms":100000},
		{"name":"e","wall_ms":10100,"boot_ms":100},
		{"name":"f","wall_ms":1000}]}`))
	if err != nil {
		t.Fatal(err)
	}
	rows := []map[string]any{
		// Over the ratio and the floor.
		{"name": "a", "wall_ms": int64(15001)},
		// Over the ratio but not the floor (#176: letteraccept 1.28x).
		{"name": "b", "wall_ms": int64(12800)},
		// Faster.
		{"name": "c", "wall_ms": int64(4000)},
		// Over the floor but not the ratio.
		{"name": "d", "wall_ms": int64(110000)},
		// Over both on wall time only because this run booted the game
		// (#176: smoke/identity 1.53x with boot_ms 5083 against a 200ms attach).
		{"name": "e", "wall_ms": int64(15400), "boot_ms": int64(5083)},
		// A tiny baseline never trips the floor.
		{"name": "f", "wall_ms": int64(4000)},
		{"name": "new", "wall_ms": int64(99999)},
	}
	list, total := regressions(rows, b)
	if total != 141100 {
		t.Errorf("baseline total = %d", total)
	}
	if len(list) != 1 || list[0].Name != "a" || list[0].BaselineMs != 10000 || list[0].WallMs != 15001 || list[0].RunMs != 15001 || list[0].BaselineRunMs != 10000 {
		t.Errorf("regressions = %+v", list)
	}
	if data, _ := json.Marshal(list); !strings.Contains(string(data), `"ratio":1.5001`) || strings.Contains(string(data), "baseline_flake") {
		t.Errorf("ratio json = %s", data)
	}
	if list[0].String() != "a 1.50x" {
		t.Errorf("regression line = %q", list[0])
	}
	// A run that attached is compared to a baseline that booted.
	rows = []map[string]any{{"name": "e", "wall_ms": int64(15200), "boot_ms": int64(100)}}
	b.boot["e"] = 5100
	if list, _ = regressions(rows, b); len(list) != 1 || list[0].RunMs != 15100 || list[0].BaselineRunMs != 5000 {
		t.Errorf("regressions = %+v", list)
	}
}

// The suite's drift list is every row's result.json drift (as the run
// decoded it from JSON), in queue order, typed back for the report.
func TestDriftFlagsCollectRowsInOrder(t *testing.T) {
	rows := []map[string]any{
		{"name": "a"},
		{"name": "b", "drift": []any{
			map[string]any{"case": "b", "metric": "wall_ms", "value": 16000.0, "median": 10000.0, "ratio": 1.6, "samples": 5.0},
			map[string]any{"case": "b", "metric": "native_errors", "value": 3.0, "median": 1.0, "ratio": 3.0, "samples": 5.0},
		}},
		{"name": "c", "drift": []any{map[string]any{"case": "c", "metric": "waits_stalled", "value": 1.0, "median": 0.0, "ratio": 0.0, "samples": 2.0}, "junk"}},
	}
	flags := driftFlags(rows)
	if len(flags) != 3 || flags[0].Case != "b" || flags[0].Metric != "wall_ms" || flags[0].Samples != 5 || flags[1].Metric != "native_errors" || flags[2].Case != "c" {
		t.Errorf("flags = %+v", flags)
	}
	if got := flags[0].String(); !strings.Contains(got, "b wall_ms 16000 vs median 10000 (1.60x over 5)") {
		t.Errorf("String = %q", got)
	}
	if flags = driftFlags([]map[string]any{{"name": "a"}}); flags == nil || len(flags) != 0 {
		t.Errorf("empty = %#v", flags)
	}
}

// The drift a suite reports is what na.Drift computes for each row's block
// against the series the earlier suites appended to: the flagging the
// runner does per case, end to end from a series file.
func TestSuiteDriftFromSeries(t *testing.T) {
	series := filepath.Join(t.TempDir(), "metrics.jsonl")
	for _, wall := range []float64{10000, 10000, 10000} {
		if err := na.AppendSeries(series, na.SeriesRow{Case: "smoke/identity", RunID: "earlier", Passed: true, Metrics: na.Metrics{"wall_ms": wall, "native_calls": 100}}); err != nil {
			t.Fatal(err)
		}
	}
	report := na.Report{"passed": true, na.MetricsKey: na.Metrics{"wall_ms": 20000, "native_calls": 100}}
	flags := na.RecordSeries(series, "smoke/identity", "this-run", report)
	if len(flags) != 1 || flags[0].Metric != "wall_ms" || flags[0].Ratio != 2 {
		t.Fatalf("flags = %+v", flags)
	}
	// A second case of the same run reads the series with its own row
	// there and still only judges the earlier runs.
	if flags = na.Drift("smoke/identity", "this-run", na.Metrics{"wall_ms": 10000}, mustReadSeries(t, series)); len(flags) != 0 {
		t.Errorf("own run counted: %+v", flags)
	}
}

func mustReadSeries(t *testing.T, path string) []na.SeriesRow {
	t.Helper()
	rows, err := na.ReadSeries(path)
	if err != nil {
		t.Fatal(err)
	}
	return rows
}

func TestParseSuiteResolvesRegistry(t *testing.T) {
	root := absRoot()
	suite := filepath.Join(t.TempDir(), "suite.json")
	if err := os.WriteFile(suite, []byte(`[
		{"name": "smoke/identity", "acceptance": "runner smoke"},
		{"name": "light/dark"}
	]`), 0644); err != nil {
		t.Fatal(err)
	}
	var stderr bytes.Buffer
	list, opts, err := parseSuite([]string{"-suite", suite, "-root", root, "-output", filepath.Join(root, "out"), "-rimgovernor", "rg.exe", "-workers", "3", "-budget", "4m"}, &stderr)
	if err != nil {
		t.Fatalf("parseSuite: %v (%s)", err, stderr.String())
	}
	if opts.Workers != 3 || opts.Budget != 4*time.Minute || opts.Series != filepath.Join(root, "metrics.jsonl") {
		t.Fatalf("opts = %+v", opts)
	}
	if list[0].registered == nil || list[0].registered.Name != "smoke/identity" || list[0].serveDriven() || list[0].Acceptance != "runner smoke" {
		t.Errorf("bridge row = %+v", list[0])
	}
	if list[1].registered == nil || !list[1].serveDriven() {
		t.Errorf("service row = %+v", list[1])
	}
	self, worker := filepath.Join(root, "acceptance.exe"), filepath.Join(root, "out", "workers", "1")
	argv, output := entryCommand(list[0], opts, self, worker)
	if want := []string{self, "run", "smoke/identity", "-root", worker, "-output", opts.Output, "-game", "rimgovernor-trial", "-fresh", "-checkpoint-every", "0", "-no-doctor", "-series", opts.Series, "-budget", "4m0s"}; strings.Join(argv, " ") != strings.Join(want, " ") {
		t.Errorf("bridge argv = %v", argv)
	}
	if output != filepath.Join(opts.Output, "smoke", "identity") {
		t.Errorf("bridge output = %q", output)
	}
	argv, output = entryCommand(list[1], opts, self, worker)
	if want := []string{self, "run", "light/dark", "-root", worker, "-output", opts.Output, "-game", "rimgovernor-trial", "-fresh", "-checkpoint-every", "0", "-no-doctor", "-series", opts.Series, "-rimgovernor", "rg.exe", "-budget", "4m0s"}; strings.Join(argv, " ") != strings.Join(want, " ") {
		t.Errorf("service argv = %v", argv)
	}
	// -no-series passes through instead of a path; an explicit -series is
	// made absolute.
	_, noSeries, err := parseSuite([]string{"-cases", "smoke/identity", "-root", root, "-output", filepath.Join(root, "out"), "-no-series"}, &stderr)
	if err != nil {
		t.Fatal(err)
	}
	if argv, _ = entryCommand(list[0], noSeries, self, worker); !slices.Contains(argv, "-no-series") || slices.Contains(argv, "-series") {
		t.Errorf("-no-series argv = %v", argv)
	}
	_, explicit, err := parseSuite([]string{"-cases", "smoke/identity", "-root", root, "-output", filepath.Join(root, "out"), "-series", "series.jsonl"}, &stderr)
	if err != nil || !filepath.IsAbs(explicit.Series) || filepath.Base(explicit.Series) != "series.jsonl" {
		t.Errorf("-series = %q, %v", explicit.Series, err)
	}
	if output != filepath.Join(opts.Output, "light", "dark") {
		t.Errorf("service output = %q", output)
	}

	list, _, err = parseSuite([]string{"-all", "-root", root, "-output", filepath.Join(root, "out")}, &stderr)
	if err != nil || len(list) == 0 || list[0].registered == nil {
		t.Fatalf("-all: %v %+v", err, list)
	}
	list, _, err = parseSuite([]string{"-cases", "smoke/identity", "-root", root, "-output", filepath.Join(root, "out")}, &stderr)
	if err != nil || len(list) != 1 || list[0].registered == nil {
		t.Fatalf("-cases: %v %+v", err, list)
	}
}

func TestParseSuiteRejects(t *testing.T) {
	root, out := absRoot(), filepath.Join(absRoot(), "out")
	for name, args := range map[string][]string{
		"no selector":         {"-root", root, "-output", out},
		"two selectors":       {"-all", "-cases", "smoke/identity", "-root", root, "-output", out},
		"unknown case":        {"-cases", "smoke/nope", "-root", root, "-output", out},
		"missing output":      {"-all", "-root", root},
		"relative root":       {"-all", "-root", "bridge", "-output", out},
		"zero workers":        {"-all", "-root", root, "-output", out, "-workers", "0"},
		"positional":          {"-all", "-root", root, "-output", out, "extra"},
		"retired binary name": {"-suite", writeBaseline(t, `[{"name":"needsaccept"}]`), "-root", root, "-output", out},
		"duplicate names":     {"-suite", writeBaseline(t, `[{"name":"smoke/identity"},{"name":"smoke/identity"}]`), "-root", root, "-output", out},
		"empty suite":         {"-suite", writeBaseline(t, `[]`), "-root", root, "-output", out},
	} {
		var stderr bytes.Buffer
		if _, _, err := parseSuite(args, &stderr); err == nil {
			t.Errorf("%s: parseSuite(%v) = nil", name, args)
		}
	}
}

// The issue #6 acceptance matrix must keep one row per criterion in the
// issue text, each naming a registered service case (#142), so a typo or a
// dropped row fails go test rather than a spent native session.
func TestIssue6MatrixCoversEveryCriterion(t *testing.T) {
	path := filepath.Join("suites", "issue-6-matrix.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	decoder := json.NewDecoder(strings.NewReader(string(data)))
	decoder.DisallowUnknownFields()
	var list []entry
	if err := decoder.Decode(&list); err != nil {
		t.Fatal(err)
	}
	var stderr bytes.Buffer
	resolved, _, err := parseSuite([]string{"-suite", path, "-root", absRoot(), "-output", filepath.Join(absRoot(), "out")}, &stderr)
	if err != nil {
		t.Fatal(err)
	}
	criteria := map[string]bool{
		"dark benches": false, "partially lit benches": false, "protected fungus rooms": false, "layout changes": false,
		"filthy vs inherently dirty rooms": false, "kitchen/butcher separation": false, "unreachable stores": false,
		"disconnected consumers": false, "exhausted fuel": false, "exhausted batteries": false, "hot-weather freezer failure": false,
	}
	for i, h := range list {
		if h.Acceptance == "" {
			t.Errorf("%s: acceptance criterion missing", h.Name)
		}
		if resolved[i].registered == nil || !resolved[i].serveDriven() {
			t.Errorf("%s: must resolve to a registered service case, got %+v", h.Name, resolved[i])
		}
		for criterion := range criteria {
			if strings.HasPrefix(h.Acceptance, criterion+":") {
				criteria[criterion] = true
			}
		}
	}
	for criterion, covered := range criteria {
		if !covered {
			t.Errorf("criterion %q has no row", criterion)
		}
	}
	argv, _ := entryCommand(resolved[0], suiteOptions{Output: absRoot(), Rimgovernor: "rg.exe", GameID: "rimgovernor-trial"}, "acceptance.exe", "w1")
	if !slices.Contains(argv, "-rimgovernor") {
		t.Errorf("service case argv lacks -rimgovernor: %v", argv)
	}
}

// A baseline row's flake record (#281) rides on the regression it flags.
func TestRegressionsCarryTheBaselineFlake(t *testing.T) {
	b, err := loadBaseline(writeBaseline(t, `{"cases":[
		{"name":"a","wall_ms":10000,"flake":{"rate":0.3,"failures":3,"samples":10}},
		{"name":"b","wall_ms":10000,"flake":{"rate":0,"failures":0,"samples":4}}]}`))
	if err != nil {
		t.Fatal(err)
	}
	list, _ := regressions([]map[string]any{{"name": "a", "wall_ms": int64(20000)}, {"name": "b", "wall_ms": int64(20000)}}, b)
	if len(list) != 2 || list[0].BaselineFlake == nil || list[0].BaselineFlake.Failures != 3 || list[1].BaselineFlake == nil || list[1].BaselineFlake.Samples != 4 {
		t.Fatalf("regressions = %+v", list)
	}
	if list[0].String() != "a 2.00x (baseline flake 30%)" || list[1].String() != "b 2.00x" {
		t.Errorf("lines = %q, %q", list[0], list[1])
	}
	data, _ := json.Marshal(list[0])
	if !strings.Contains(string(data), `"baseline_flake":{"rate":0.3,"failures":3,"samples":10}`) {
		t.Errorf("json = %s", data)
	}
}

func TestSuiteResumeCarriesTheRingAndKeepsThePass(t *testing.T) {
	root := t.TempDir()
	var stderr bytes.Buffer
	list, opts, err := parseSuite([]string{"-cases", "smoke/identity", "-root", root, "-output", filepath.Join(root, "out"), "-no-series", "-resume"}, &stderr)
	if err != nil {
		t.Fatalf("parseSuite: %v (%s)", err, stderr.String())
	}
	self, worker := filepath.Join(root, "acceptance.exe"), filepath.Join(root, "out", "workers", "1")
	argv, _ := entryCommand(list[0], opts, self, worker)
	if slices.Contains(argv, "-fresh") || slices.Contains(argv, "-checkpoint-every") || !slices.Contains(argv, "-no-doctor") {
		t.Errorf("-resume argv = %v", argv)
	}

	ring := filepath.Join(root, "checkpoints", "smoke", "identity")
	if err := os.MkdirAll(filepath.Join(ring, "t+7m"), 0755); err != nil {
		t.Fatal(err)
	}
	for _, f := range []string{"ring.json", filepath.Join("t+7m", "save.rws")} {
		if err := os.WriteFile(filepath.Join(ring, f), []byte("x"), 0644); err != nil {
			t.Fatal(err)
		}
	}
	stale := filepath.Join(worker, "checkpoints", "smoke", "identity", "stale")
	if err := os.MkdirAll(stale, 0755); err != nil {
		t.Fatal(err)
	}
	if err := carryRing(root, worker, "smoke/identity"); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(worker, "checkpoints", "smoke", "identity", "t+7m", "save.rws")); err != nil {
		t.Errorf("ring not carried: %v", err)
	}
	if _, err := os.Stat(stale); !os.IsNotExist(err) {
		t.Errorf("stale worker ring kept: %v", err)
	}
	if err := carryRing(root, worker, "light/dark"); err != nil {
		t.Errorf("no ring to carry: %v", err)
	}

	rows := []map[string]any{{"name": "a"}, {"name": "b", "resumed_from": map[string]any{"label": "t+7m"}}}
	if got := resumedRows(rows); len(got) != 1 || got[0] != "b" {
		t.Errorf("resumedRows = %v", got)
	}
}
