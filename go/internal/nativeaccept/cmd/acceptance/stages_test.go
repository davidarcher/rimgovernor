package main

import (
	"bytes"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func labels(items []item) string {
	var out []string
	for _, it := range items {
		out = append(out, it.label())
	}
	return strings.Join(out, " ")
}

// A staged row expands into the stage items past its newest cached
// bundle plus its tail, each depending on the one before; a row with
// nothing cached, or without stages, is one whole item.
func TestExpandStageChainFromCache(t *testing.T) {
	chain := cases.Case{Name: "x/chain", Stages: []string{"feed", "kitchen", "cold"}}
	plain := cases.Case{Name: "x/plain"}
	list := []entry{{Name: "x/plain", registered: &plain}, {Name: "x/chain", registered: &chain}}

	items := expand(list, func(e entry) string { return "feed" })
	if got := labels(items); got != "x/plain x/chain@kitchen x/chain@cold x/chain" {
		t.Fatalf("items = %q", got)
	}
	if items[0].dep != -1 || items[1].dep != -1 || items[1].from != "feed" || items[2].dep != 1 || items[2].from != "kitchen" || items[3].dep != 2 || items[3].from != "cold" || items[3].through != "" {
		t.Errorf("deps = %+v", items)
	}

	// Warm through the last stage: only the tail runs.
	items = expand(list, func(e entry) string { return "cold" })
	if got := labels(items); got != "x/plain x/chain" || items[1].from != "cold" || items[1].dep != -1 {
		t.Errorf("warm items = %q %+v", got, items)
	}

	// Nothing cached: the whole chain is one fresh item.
	items = expand(list, func(e entry) string { return "" })
	if got := labels(items); got != "x/plain x/chain" || items[1].from != "" || items[1].dep != -1 {
		t.Errorf("cold items = %q %+v", got, items)
	}
}

// dispatch hands ready items to free workers in list order, holds an item
// until its producer passed, and blocks the rest of a chain behind a
// failed item without running it.
func TestDispatchFansOutIndependentTailsAndBlocksFailedChains(t *testing.T) {
	a := cases.Case{Name: "x/a", Stages: []string{"s1", "s2"}}
	b := cases.Case{Name: "x/b", Stages: []string{"s1"}}
	c := cases.Case{Name: "x/c"}
	list := []entry{{Name: "x/a", registered: &a}, {Name: "x/b", registered: &b}, {Name: "x/c", registered: &c}}
	items := expand(list, func(e entry) string {
		if e.Name == "x/a" {
			return "s1"
		}
		return ""
	})
	if got := labels(items); got != "x/a@s2 x/a x/b x/c" {
		t.Fatalf("items = %q", got)
	}
	var mu sync.Mutex
	var order []string
	started := make(chan int, len(items))
	release := map[int]chan struct{}{}
	for i := range items {
		release[i] = make(chan struct{})
	}
	stopped := map[int]bool{}
	go func() {
		// Every item but the tail of x/a starts before any ends: the
		// tail waits on its stage item.
		seen := map[int]bool{}
		for len(seen) < 3 {
			seen[<-started] = true
		}
		if seen[1] {
			t.Error("x/a tail started before x/a@s2 ended")
		}
		for i := range items {
			close(release[i])
		}
	}()
	blocked := dispatch(items, 3, func(worker, i int) bool {
		started <- i
		<-release[i]
		mu.Lock()
		order = append(order, items[i].label())
		mu.Unlock()
		return items[i].label() != "x/a@s2"
	}, func(worker int) {
		mu.Lock()
		stopped[worker] = true
		mu.Unlock()
	})
	if len(stopped) != 3 {
		t.Errorf("stopped workers = %v", stopped)
	}
	if slices.Contains(order, "x/a") {
		t.Errorf("blocked tail ran: %v", order)
	}
	if cause, ok := blocked[1]; !ok || cause != 0 || len(blocked) != 1 {
		t.Errorf("blocked = %v", blocked)
	}
	row := blockedRow(items[1], items[0])
	if row["blocked_by"] != "x/a@s2" || row["passed"] != false || row["from"] != "s2" {
		t.Errorf("blocked row = %v", row)
	}
}

// A chain with every producer passing runs in stage order on whichever
// worker is free; a single worker runs everything serially.
func TestDispatchSerialChainOnOneWorker(t *testing.T) {
	a := cases.Case{Name: "x/a", Stages: []string{"s1", "s2", "s3"}}
	items := expand([]entry{{Name: "x/a", registered: &a}}, func(entry) string { return "s1" })
	var order []string
	blocked := dispatch(items, 1, func(worker, i int) bool {
		order = append(order, items[i].label())
		return true
	}, func(int) {})
	if got := strings.Join(order, " "); got != "x/a@s2 x/a@s3 x/a" || len(blocked) != 0 {
		t.Errorf("order = %q blocked = %v", got, blocked)
	}
}

// -stages drops -restage, adds -through for a stage item, refuses the
// land tier and -resume, and keeps a row that opened on a stage bundle
// passing.
func TestSuiteStagesFlags(t *testing.T) {
	root := t.TempDir()
	var stderr bytes.Buffer
	list, opts, err := parseSuite([]string{"-cases", "smoke/identity", "-root", root, "-output", filepath.Join(root, "out"), "-no-series", "-stages"}, &stderr)
	if err != nil {
		t.Fatalf("parseSuite: %v (%s)", err, stderr.String())
	}
	self, worker := filepath.Join(root, "acceptance.exe"), filepath.Join(root, "out", "workers", "1")
	argv, _ := entryCommand(list[0], opts, self, worker)
	if slices.Contains(argv, "-restage") || !slices.Contains(argv, "-fresh") || slices.Contains(argv, "-through") {
		t.Errorf("-stages argv = %v", argv)
	}
	stage := list[0]
	stage.Through = "shell"
	argv, _ = entryCommand(stage, opts, self, worker)
	if i := slices.Index(argv, "-through"); i < 0 || argv[i+1] != "shell" {
		t.Errorf("stage argv = %v", argv)
	}
	if got := stageOutput(opts, item{entry: stage, through: "shell"}); got != filepath.Join(opts.Output, "stages", "shell") {
		t.Errorf("stage output = %q", got)
	}
	if got := stageOutput(opts, item{entry: list[0]}); got != opts.Output {
		t.Errorf("tail output = %q", got)
	}
	for name, args := range map[string][]string{
		"land tier": {"-tier", "land", "-root", root, "-output", filepath.Join(root, "out"), "-stages"},
		"resume":    {"-cases", "smoke/identity", "-root", root, "-output", filepath.Join(root, "out"), "-stages", "-resume"},
	} {
		if _, _, err := parseSuite(args, &stderr); err == nil {
			t.Errorf("%s: parseSuite(%v) = nil", name, args)
		}
	}
	rows := []map[string]any{{"name": "a"}, {"name": "b", "staged_from": map[string]any{"stage": "shell"}}}
	if got := stagedRows(rows); len(got) != 1 || got[0] != "b" {
		t.Errorf("stagedRows = %v", got)
	}
}

// Stage bundles travel by directory: carry replaces the worker's copy
// with the shared root's, publish copies each bundle the worker holds
// back, and a directory without a sidecar is not a bundle.
func TestCarryAndPublishStages(t *testing.T) {
	root, worker := t.TempDir(), t.TempDir()
	write := func(base, stage, file string) {
		t.Helper()
		path := filepath.Join(base, "stages", "x", "chain", stage, file)
		if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte("x"), 0644); err != nil {
			t.Fatal(err)
		}
	}
	write(root, "feed", na.CheckpointSidecar)
	write(worker, "stale", na.CheckpointSidecar)
	if err := carryStages(root, worker, "x/chain"); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(worker, "stages", "x", "chain", "feed", na.CheckpointSidecar)); err != nil {
		t.Errorf("feed not carried: %v", err)
	}
	if _, err := os.Stat(filepath.Join(worker, "stages", "x", "chain", "stale")); !os.IsNotExist(err) {
		t.Errorf("stale worker bundle kept: %v", err)
	}
	if err := carryStages(root, worker, "x/none"); err != nil {
		t.Errorf("nothing to carry: %v", err)
	}
	write(worker, "kitchen", na.CheckpointSidecar)
	write(worker, "partial", "save.rws")
	published, err := publishStages(root, worker, "x/chain")
	if err != nil {
		t.Fatal(err)
	}
	if strings.Join(published, " ") != "feed kitchen" {
		t.Errorf("published = %v", published)
	}
	if _, err := os.Stat(filepath.Join(root, "stages", "x", "chain", "kitchen", na.CheckpointSidecar)); err != nil {
		t.Errorf("kitchen not published: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "stages", "x", "chain", "partial")); !os.IsNotExist(err) {
		t.Errorf("sidecar-less directory published: %v", err)
	}
	if published, err := publishStages(root, worker, "x/none"); err != nil || len(published) != 0 {
		t.Errorf("nothing to publish: %v %v", published, err)
	}
}

// The report's stage graph lists each staged row's items in chain order
// with what they opened on and how they ended.
func TestStageGraph(t *testing.T) {
	a := cases.Case{Name: "x/a", Stages: []string{"s1", "s2"}}
	c := cases.Case{Name: "x/c"}
	items := expand([]entry{{Name: "x/a", registered: &a}, {Name: "x/c", registered: &c}}, func(e entry) string {
		if e.Name == "x/a" {
			return "s1"
		}
		return ""
	})
	rows := []map[string]any{
		{"name": "x/a", "stage": "s2", "passed": false, "worker": 1, "error": "boom"},
		blockedRow(items[1], items[0]),
		{"name": "x/c", "passed": true},
	}
	graph := stageGraph(items, rows)
	if len(graph) != 1 || graph[0]["case"] != "x/a" || graph[0]["cached_through"] != "s1" {
		t.Fatalf("graph = %v", graph)
	}
	nodes := graph[0]["items"].([]map[string]any)
	if len(nodes) != 2 || nodes[0]["stage"] != "s2" || nodes[0]["error"] != "boom" || nodes[1]["stage"] != "(tail)" || nodes[1]["blocked_by"] != "x/a@s2" {
		t.Errorf("nodes = %v", nodes)
	}
}
