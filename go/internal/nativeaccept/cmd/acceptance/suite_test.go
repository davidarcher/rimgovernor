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
	if data, _ := json.Marshal(list); !strings.Contains(string(data), `"ratio":1.5001`) {
		t.Errorf("ratio json = %s", data)
	}
	// A run that attached is compared to a baseline that booted.
	rows = []map[string]any{{"name": "e", "wall_ms": int64(15200), "boot_ms": int64(100)}}
	b.boot["e"] = 5100
	if list, _ = regressions(rows, b); len(list) != 1 || list[0].RunMs != 15100 || list[0].BaselineRunMs != 5000 {
		t.Errorf("regressions = %+v", list)
	}
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
	if opts.Workers != 3 || opts.Budget != 4*time.Minute {
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
	if want := []string{self, "run", "smoke/identity", "-root", worker, "-output", opts.Output, "-game", "rimgovernor-trial", "-budget", "4m0s"}; strings.Join(argv, " ") != strings.Join(want, " ") {
		t.Errorf("bridge argv = %v", argv)
	}
	if output != filepath.Join(opts.Output, "smoke", "identity") {
		t.Errorf("bridge output = %q", output)
	}
	argv, output = entryCommand(list[1], opts, self, worker)
	if want := []string{self, "run", "light/dark", "-root", worker, "-output", opts.Output, "-game", "rimgovernor-trial", "-rimgovernor", "rg.exe", "-budget", "4m0s"}; strings.Join(argv, " ") != strings.Join(want, " ") {
		t.Errorf("service argv = %v", argv)
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
