package main

// Stage scheduling (issue #527, #270 slice B): under `suite -stages` a row
// whose case declares Stages (#329) is not one work item but a chain of
// them, one per declared stage still missing from the bundles cached in
// -root plus the tail that runs the case to its verdict. Each stage item
// is `acceptance run <case> -through <stage>`: it opens on the newest
// cached bundle, captures the stage's own and ends. Its bundle is
// published back into -root when it finishes, so the next item of the
// chain, on whichever worker is free, opens on it, and the next suite (or
// `acceptance run`) in that root starts warm. A chain is serial by
// nature (each stage's bundle is the next one's input), so the fan-out is
// across rows: once bundles are warm only tails run, one per case, in
// parallel, and the suite's wall is max(tail) plus a reload rather than
// the sum of the chains. A row with nothing cached runs its whole chain
// as one item: splitting a cold chain would only add a reload per stage.
//
// The tail's row is the case's row and carries staged_from; the stage
// items' rows are listed under "stage_runs" and the whole graph under
// "stages". An item whose producer failed is blocked, not run. The land
// tier refuses -stages: a landing pass stages from scratch (cmd/land).

import (
	"context"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sync"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// item is one unit of suite work: a row run to its verdict (through ""),
// or one declared stage of it (through), opening on the stage from ("" for
// a fresh start) that item dep produces (-1 when it is cached or none).
type item struct {
	entry   entry
	through string
	from    string
	dep     int
}

// label names the item in logs and rows: the case, or case@stage.
func (it item) label() string {
	if it.through == "" {
		return it.entry.Name
	}
	return it.entry.Name + "@" + it.through
}

// expand plans list's work items: one per row, or the chain of stage
// items a staged row still needs past cached (its newest cached stage,
// "" when none) and its tail. Items keep list order, a chain's in stage
// order, so the queue's tiers and baseline ranking hold.
func expand(list []entry, cached func(entry) string) []item {
	var items []item
	for _, e := range list {
		stages := e.registered.Stages
		have := cached(e)
		if len(stages) == 0 || have == "" {
			items = append(items, item{entry: e, dep: -1})
			continue
		}
		next := 0
		for i, name := range stages {
			if name == have {
				next = i + 1
			}
		}
		from, dep := have, -1
		for _, name := range stages[next:] {
			items = append(items, item{entry: e, through: name, from: from, dep: dep})
			from, dep = name, len(items)-1
		}
		items = append(items, item{entry: e, from: from, dep: dep})
	}
	return items
}

// dispatch runs items across workers in list order, an item once the item
// it depends on has passed: run(worker, index) reports whether the item
// passed, and every worker calls stop once its queue is empty. Items whose
// producer failed (or was itself blocked) never run; the map names each
// with the index of the item that failed.
func dispatch(items []item, workers int, run func(worker, index int) bool, stop func(worker int)) map[int]int {
	const (
		pending = iota
		running
		passed
		failed
		blocked
	)
	state := make([]int, len(items))
	blockedBy := map[int]int{}
	var mu sync.Mutex
	cond := sync.NewCond(&mu)
	// next picks the first runnable item, marking the ones whose producer
	// failed as blocked on the way; -1 with wait true means an item may
	// still become runnable once a running one ends.
	next := func() (index int, wait bool) {
		for i, it := range items {
			if state[i] != pending {
				continue
			}
			if it.dep < 0 {
				return i, false
			}
			switch state[it.dep] {
			case passed:
				return i, false
			case failed:
				state[i], blockedBy[i] = blocked, it.dep
			case blocked:
				state[i], blockedBy[i] = blocked, blockedBy[it.dep]
			default:
				wait = true
			}
		}
		return -1, wait
	}
	var wg sync.WaitGroup
	for w := 1; w <= workers; w++ {
		wg.Add(1)
		go func(worker int) {
			defer wg.Done()
			defer stop(worker)
			for {
				mu.Lock()
				index, wait := next()
				for index < 0 && wait {
					cond.Wait()
					index, wait = next()
				}
				if index < 0 {
					mu.Unlock()
					return
				}
				state[index] = running
				mu.Unlock()
				ok := run(worker, index)
				mu.Lock()
				if ok {
					state[index] = passed
				} else {
					state[index] = failed
				}
				cond.Broadcast()
				mu.Unlock()
			}
		}(w)
	}
	wg.Wait()
	return blockedBy
}

// stageOutput is where a stage item's run writes: the suite output's
// stages/<stage> tree, beside the case's own row.
func stageOutput(opts suiteOptions, it item) string {
	if it.through == "" {
		return opts.Output
	}
	return filepath.Join(opts.Output, "stages", it.through)
}

// stagesRel is the case's stage bundle directory relative to a root.
func stagesRel(name string) string {
	return filepath.Join("stages", filepath.FromSlash(name))
}

// carryStages replaces the worker root's cached stage bundles of the case
// with the shared root's, so the item opens on what the suite has
// published so far.
func carryStages(root, workerRoot, name string) error {
	src, dst := filepath.Join(root, stagesRel(name)), filepath.Join(workerRoot, stagesRel(name))
	if err := os.RemoveAll(dst); err != nil {
		return err
	}
	if _, err := os.Stat(src); err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	return na.CopyTree(src, dst)
}

// publishStages copies every stage bundle the item left in the worker
// root over the shared root's, stage by stage, and returns the stages it
// published.
func publishStages(root, workerRoot, name string) ([]string, error) {
	src, dst := filepath.Join(workerRoot, stagesRel(name)), filepath.Join(root, stagesRel(name))
	entries, err := os.ReadDir(src)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	var published []string
	for _, entry := range entries {
		if !entry.IsDir() {
			continue
		}
		if _, err := os.Stat(filepath.Join(src, entry.Name(), na.CheckpointSidecar)); err != nil {
			continue
		}
		target := filepath.Join(dst, entry.Name())
		if err := os.RemoveAll(target); err != nil {
			return published, err
		}
		if err := na.CopyTree(filepath.Join(src, entry.Name()), target); err != nil {
			return published, err
		}
		published = append(published, entry.Name())
	}
	return published, nil
}

// runItem runs one work item on a worker: under -stages a staged case's
// bundles are carried in from the shared root first and published back
// after, and a stage item writes under the suite output's stages tree. The row is the
// run's, plus "stage" and "from" for the graph.
func runItem(ctx context.Context, it item, opts suiteOptions, self, workerRoot string, worker int, stderr io.Writer) map[string]any {
	current := opts
	current.Output = stageOutput(opts, it)
	staged := opts.Stages && len(it.entry.registered.Stages) > 0
	if staged {
		if err := carryStages(opts.Root, workerRoot, it.entry.Name); err != nil {
			return map[string]any{"name": it.entry.Name, "stage": it.through, "worker": worker, "passed": false, "error": "carry stage bundles: " + err.Error()}
		}
	}
	e := it.entry
	e.Through = it.through
	row := runEntry(ctx, e, current, self, workerRoot, worker, stderr)
	if it.through != "" {
		row["stage"] = it.through
	}
	if it.from != "" {
		row["from"] = it.from
	}
	if staged {
		published, err := publishStages(opts.Root, workerRoot, it.entry.Name)
		if err != nil {
			row["publish_error"] = err.Error()
			fmt.Fprintf(stderr, "[worker %d] %s: publish stage bundles: %v\n", worker, it.label(), err)
		}
		if len(published) > 0 {
			row["published"] = published
		}
	}
	return row
}

// blockedRow is the row of an item that never ran because the item at
// cause failed.
func blockedRow(it item, cause item) map[string]any {
	row := map[string]any{"name": it.entry.Name, "passed": false, "blocked_by": cause.label(), "error": "blocked: " + cause.label() + " failed, so its bundle is missing"}
	if it.through != "" {
		row["stage"] = it.through
	}
	if it.from != "" {
		row["from"] = it.from
	}
	return row
}

// stageGraph is the report's "stages" block: per staged row, the stage it
// was cached through and each item's outcome in chain order.
func stageGraph(items []item, rows []map[string]any) []map[string]any {
	var graph []map[string]any
	byCase := map[string]map[string]any{}
	for i, it := range items {
		if len(it.entry.registered.Stages) == 0 {
			continue
		}
		g := byCase[it.entry.Name]
		if g == nil {
			g = map[string]any{"case": it.entry.Name, "declared": it.entry.registered.Stages, "cached_through": it.from, "items": []map[string]any{}}
			byCase[it.entry.Name] = g
			graph = append(graph, g)
		}
		row := rows[i]
		node := map[string]any{"stage": it.through, "from": it.from, "passed": row["passed"]}
		if it.through == "" {
			node["stage"] = "(tail)"
		}
		for _, k := range []string{"worker", "wall_ms", "output", "blocked_by", "published", "error"} {
			if v, ok := row[k]; ok {
				node[k] = v
			}
		}
		g["items"] = append(g["items"].([]map[string]any), node)
	}
	return graph
}

// cachedStage is expand's cached for a suite: the newest stage bundle of
// the row's case under the shared root.
func cachedStage(opts suiteOptions) func(entry) string {
	return func(e entry) string {
		return cases.CachedStage(*e.registered, cases.Options{Root: opts.Root, Headless: true})
	}
}
