package main

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

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

// The queue is bridge-only first, then serve-driven, each half
// longest-first by the baseline with untimed rows ahead as if long; the
// baseline may be the former suiteaccept's "harnesses" rows.
func TestScheduleServeLastLongestFirst(t *testing.T) {
	b, err := loadBaseline(writeBaseline(t, `{"harnesses":[{"name":"a","wall_ms":10},{"name":"b","wall_ms":30},{"name":"c","wall_ms":20},{"name":"s1","wall_ms":50},{"name":"s2","wall_ms":90}]}`))
	if err != nil {
		t.Fatal(err)
	}
	serveCase := cases.Case{Name: "x/serve", Serve: &cases.ServeSpec{}}
	list := []entry{
		{Name: "s1", Args: []string{"-rimgovernor", "x"}},
		{Name: "a"}, {Name: "b"},
		{Name: "x/serve", registered: &serveCase},
		{Name: "c"}, {Name: "new"},
		{Name: "s2", Serve: true},
	}
	schedule(list, b)
	if got := names(list); got != "new b c a x/serve s2 s1" {
		t.Errorf("order = %q", got)
	}
	// Without a baseline the listed order holds within each half.
	list = []entry{{Name: "s1", Serve: true}, {Name: "a"}, {Name: "b"}}
	schedule(list, nil)
	if got := names(list); got != "a b s1" {
		t.Errorf("unordered = %q", got)
	}
	if _, err := loadBaseline(filepath.Join(t.TempDir(), "missing.json")); err == nil {
		t.Error("expected an error for a missing baseline")
	}
}

func TestRegressionsFlagOverRatio(t *testing.T) {
	b, err := loadBaseline(writeBaseline(t, `{"cases":[{"name":"a","wall_ms":1000},{"name":"b","wall_ms":1000},{"name":"c","wall_ms":1000}]}`))
	if err != nil {
		t.Fatal(err)
	}
	rows := []map[string]any{
		{"name": "a", "wall_ms": int64(1250)},
		{"name": "b", "wall_ms": int64(1251)},
		{"name": "c", "wall_ms": int64(400)},
		{"name": "new", "wall_ms": int64(99999)},
	}
	list, total := regressions(rows, b)
	if total != 3000 {
		t.Errorf("baseline total = %d", total)
	}
	if len(list) != 1 || list[0].Name != "b" || list[0].BaselineMs != 1000 || list[0].WallMs != 1251 {
		t.Errorf("regressions = %+v", list)
	}
	if data, _ := json.Marshal(list); !strings.Contains(string(data), `"ratio":1.251`) {
		t.Errorf("ratio json = %s", data)
	}
}

func TestParseSuiteResolvesRegistryAndBinaries(t *testing.T) {
	root, bin := absRoot(), filepath.Join(absRoot(), "bin")
	suite := filepath.Join(t.TempDir(), "suite.json")
	if err := os.WriteFile(suite, []byte(`[
		{"name": "smoke/identity", "acceptance": "runner smoke"},
		{"name": "needsaccept"},
		{"name": "light-dark", "binary": "lightaccept.exe", "args": ["-rimgovernor", "{rimgovernor}", "-scenario", "dark"]}
	]`), 0644); err != nil {
		t.Fatal(err)
	}
	var stderr bytes.Buffer
	list, opts, err := parseSuite([]string{"-suite", suite, "-root", root, "-output", filepath.Join(root, "out"), "-bin", bin, "-rimgovernor", "rg.exe", "-workers", "3", "-budget", "4m"}, &stderr)
	if err != nil {
		t.Fatalf("parseSuite: %v (%s)", err, stderr.String())
	}
	if opts.Workers != 3 || opts.Budget != 4*time.Minute || opts.Bin != bin {
		t.Fatalf("opts = %+v", opts)
	}
	if list[0].registered == nil || list[0].registered.Name != "smoke/identity" || list[0].serveDriven() {
		t.Errorf("registry row = %+v", list[0])
	}
	if list[1].registered != nil || list[1].Binary != filepath.Join(bin, "needsaccept.exe") || list[1].serveDriven() {
		t.Errorf("default binary row = %+v", list[1])
	}
	if list[2].Binary != filepath.Join(bin, "lightaccept.exe") || list[2].Args[1] != "rg.exe" || !list[2].serveDriven() {
		t.Errorf("serve binary row = %+v", list[2])
	}
	self, worker := filepath.Join(root, "acceptance.exe"), filepath.Join(root, "out", "workers", "1")
	argv, output := entryCommand(list[0], opts, self, worker)
	if want := []string{self, "run", "smoke/identity", "-root", worker, "-output", opts.Output, "-game", "rimgovernor-trial", "-budget", "4m0s"}; strings.Join(argv, " ") != strings.Join(want, " ") {
		t.Errorf("registry argv = %v", argv)
	}
	if output != filepath.Join(opts.Output, "smoke", "identity") {
		t.Errorf("registry output = %q", output)
	}
	argv, output = entryCommand(list[2], opts, self, worker)
	if want := []string{list[2].Binary, "-root", worker, "-output", filepath.Join(opts.Output, "light-dark"), "-rimgovernor", "rg.exe", "-scenario", "dark"}; strings.Join(argv, " ") != strings.Join(want, " ") {
		t.Errorf("binary argv = %v", argv)
	}
	if output != filepath.Join(opts.Output, "light-dark") {
		t.Errorf("binary output = %q", output)
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
		"no selector":          {"-root", root, "-output", out},
		"two selectors":        {"-all", "-cases", "smoke/identity", "-root", root, "-output", out},
		"unknown case":         {"-cases", "smoke/nope", "-root", root, "-output", out},
		"missing output":       {"-all", "-root", root},
		"relative root":        {"-all", "-root", "bridge", "-output", out},
		"zero workers":         {"-all", "-root", root, "-output", out, "-workers", "0"},
		"positional":           {"-all", "-root", root, "-output", out, "extra"},
		"binary without -bin":  {"-suite", writeBaseline(t, `[{"name":"needsaccept"}]`), "-root", root, "-output", out},
		"duplicate names":      {"-suite", writeBaseline(t, `[{"name":"smoke/identity"},{"name":"smoke/identity"}]`), "-root", root, "-output", out},
		"registry row w/ args": {"-suite", writeBaseline(t, `[{"name":"smoke/identity","args":["-x"]}]`), "-root", root, "-output", out},
	} {
		var stderr bytes.Buffer
		if _, _, err := parseSuite(args, &stderr); err == nil {
			t.Errorf("%s: parseSuite(%v) = nil", name, args)
		}
	}
}

func TestBootMs(t *testing.T) {
	if got := bootMs(map[string]any{"boot_ms": 5723.0}); got != 5723 {
		t.Errorf("runner boot_ms = %d", got)
	}
	if got := bootMs(map[string]any{"game_reuse": map[string]any{"openMs": 210.0}}); got != 210 {
		t.Errorf("game_reuse openMs = %d", got)
	}
	if got := bootMs(map[string]any{}); got != 0 {
		t.Errorf("no boot = %d", got)
	}
}

// The issue #6 acceptance matrix must keep one row per criterion in the
// issue text, each naming a harness that exists, so a typo or a dropped row
// fails go test rather than a spent native session.
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
	resolved, _, err := parseSuite([]string{"-suite", path, "-root", absRoot(), "-output", filepath.Join(absRoot(), "out"), "-bin", filepath.Join(absRoot(), "bin")}, &stderr)
	if err != nil {
		t.Fatal(err)
	}
	criteria := map[string]bool{
		"dark benches": false, "partially lit benches": false, "protected fungus rooms": false,
		"filthy vs inherently dirty rooms": false, "kitchen/butcher separation": false, "unreachable stores": false,
		"disconnected consumers": false, "exhausted fuel": false, "exhausted batteries": false, "hot-weather freezer failure": false,
	}
	for i, h := range list {
		if h.Binary == "" || filepath.IsAbs(h.Binary) || !strings.HasSuffix(h.Binary, ".exe") {
			t.Errorf("%s: binary %q must be a harness name relative to -bin", h.Name, h.Binary)
		}
		if _, err := os.Stat(filepath.Join("..", strings.TrimSuffix(h.Binary, ".exe"), "main.go")); err != nil {
			t.Errorf("%s: no harness command for %s: %v", h.Name, h.Binary, err)
		}
		if len(h.Args) < 2 || h.Args[0] != "-rimgovernor" || h.Args[1] != "{rimgovernor}" {
			t.Errorf("%s: args must start with -rimgovernor {rimgovernor}, got %v", h.Name, h.Args)
		}
		if h.Acceptance == "" {
			t.Errorf("%s: acceptance criterion missing", h.Name)
		}
		if resolved[i].registered != nil || !resolved[i].serveDriven() {
			t.Errorf("%s: must resolve to a serve-driven binary, got %+v", h.Name, resolved[i])
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
	if got := resolved[0].Binary; got != filepath.Join(absRoot(), "bin", list[0].Binary) {
		t.Errorf("relative binary resolved to %q", got)
	}
}
