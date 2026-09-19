package nativeaccept

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// Every result.json carries a flat "metrics" block of numbers under stable
// names (issue #297), so a run's cost is comparable across runs without
// reading the nested report: the timing fields, the wait statistics, the
// native round trips of every flight recording under the output directory
// (flight.jsonl and each restart's service-N/flight.jsonl), the service
// launches and the evidence size. Finalize computes it from the report
// and the output directory; cases add nothing. `acceptance run` and
// `acceptance suite` append one row per case to an append-only series
// (metrics.jsonl beside the output directory) and flag the metrics that
// drifted past their rule against the series' trailing median.

// MetricsKey is the report field holding the block.
const MetricsKey = "metrics"

// MetricNames lists the block's keys in report order; every block carries
// all of them (0 when the source is absent, e.g. no service ran).
var MetricNames = []string{
	"wall_ms", "boot_ms", "ticks_advanced", "wall_tps",
	"waits", "waits_stalled", "max_quiet_ms",
	"native_calls", "native_errors", "native_bytes",
	"reads_per_step_mean", "cache_hit_ratio",
	"native_queue_ms_mean", "native_exec_ms_mean",
	"service_launches", "evidence_bytes",
}

// Metrics is the block: metric name to value.
type Metrics map[string]float64

// ComputeMetrics derives the block from a finalized report (its timing
// fields and wait_stats) and the output directory's recordings and
// evidence. It is total: a missing recording or unreadable directory
// leaves that part at zero.
func ComputeMetrics(r Report, output string) Metrics {
	m := Metrics{}
	for _, name := range MetricNames {
		m[name] = 0
	}
	m["wall_ms"] = metricValue(r[WallMsKey])
	m["boot_ms"] = metricValue(r[BootMsKey])
	m["ticks_advanced"] = metricValue(r[TicksAdvancedKey])
	m["wall_tps"] = metricValue(r[WallTPSKey])
	if stats, ok := AsMap(r["wait_stats"]); ok {
		m["waits"] = metricValue(stats["waits"])
		m["waits_stalled"] = metricValue(stats["stalled"])
		m["max_quiet_ms"] = metricValue(stats["max_quiet_ms"])
	}
	launches := 0
	for key := range r {
		if key == "service" || strings.HasPrefix(key, "service_") {
			if _, ok := AsMap(r[key]); ok {
				launches++
			}
		}
	}
	m["service_launches"] = float64(launches)
	var calls, errs, hits, bytes, timed uint64
	var queue, execute float64
	var steps, reads uint64
	for _, path := range flightRecordings(output) {
		rows, err := bridge.ReadTimeline(path)
		if err != nil {
			continue
		}
		summary := bridge.SummarizePhases(rows)
		for _, tool := range summary.Tools {
			calls += tool.Calls
			errs += tool.Errors
			hits += tool.CacheHits
			bytes += tool.ResponseBytes
			timed += tool.NativeTimed
			queue += tool.NativeQueueMs
			execute += tool.NativeExecuteMs
		}
		steps += summary.Steps.Steps
		reads += summary.Steps.Reads
	}
	m["native_calls"] = float64(calls)
	m["native_errors"] = float64(errs)
	m["native_bytes"] = float64(bytes)
	if calls+hits > 0 {
		m["cache_hit_ratio"] = float64(hits) / float64(calls+hits)
	}
	if steps > 0 {
		m["reads_per_step_mean"] = float64(reads) / float64(steps)
	}
	if timed > 0 {
		m["native_queue_ms_mean"] = queue / float64(timed)
		m["native_exec_ms_mean"] = execute / float64(timed)
	}
	m["evidence_bytes"] = float64(evidenceBytes(output))
	return m
}

// metricValue reads a report number as Go or decoded JSON holds it; an
// absent or non-numeric field is 0.
func metricValue(value any) float64 {
	switch v := value.(type) {
	case float64:
		return v
	case float32:
		return float64(v)
	case int:
		return float64(v)
	case int64:
		return float64(v)
	case uint64:
		return float64(v)
	case string:
		var f float64
		if _, err := fmt.Sscan(v, &f); err == nil {
			return f
		}
	}
	return 0
}

// flightRecordings lists the run's flight recordings: the first launch's
// under output and each restart's under its service-N directory.
func flightRecordings(output string) []string {
	paths := []string{FlightRecorderPath(output)}
	matches, _ := filepath.Glob(filepath.Join(output, "service-*", "flight.jsonl"))
	sort.Strings(matches)
	return append(paths, matches...)
}

// evidenceBytes sums the size of every file under output; result.json is
// written after the block, so it never counts.
func evidenceBytes(output string) int64 {
	var total int64
	_ = filepath.WalkDir(output, func(path string, d fs.DirEntry, err error) error {
		if err != nil || d.IsDir() {
			return nil
		}
		if info, err := d.Info(); err == nil {
			total += info.Size()
		}
		return nil
	})
	return total
}

// MetricsOf reads a report's block as Metrics (a report decoded from JSON
// holds it as map[string]any).
func MetricsOf(r map[string]any) (Metrics, bool) {
	switch block := r[MetricsKey].(type) {
	case Metrics:
		return block, true
	case map[string]float64:
		return Metrics(block), true
	case map[string]any:
		m := Metrics{}
		for name, value := range block {
			m[name] = metricValue(value)
		}
		return m, true
	}
	return nil, false
}

// SeriesRow is one line of the append-only series: which case, which run
// (RunID names the run's output directory), what source it ran from, on
// which world seed (the report's world block, #281), when, whether it
// passed and its block.
type SeriesRow struct {
	Case           string    `json:"case"`
	RunID          string    `json:"run_id"`
	SourceRevision string    `json:"source_revision,omitempty"`
	Seed           string    `json:"seed,omitempty"`
	Timestamp      time.Time `json:"timestamp"`
	Passed         bool      `json:"passed"`
	Metrics        Metrics   `json:"metrics"`
}

// FlakeWindow is how many prior rows of a case its flake rate spans.
const FlakeWindow = 10

// Flake is a case's recent pass record over the series (#281): the share
// of its last FlakeWindow runs that failed, whatever the reason. A rate
// strictly between 0 and 1 is a case that passes and fails on the same
// code; the suite prints it beside a failed or flagged row so a known
// flake is not read as a regression.
type Flake struct {
	Rate     float64 `json:"rate"`
	Failures int     `json:"failures"`
	Samples  int     `json:"samples"`
}

func (f Flake) String() string {
	return fmt.Sprintf("%d of %d recent runs failed (%.0f%%)", f.Failures, f.Samples, f.Rate*100)
}

// FlakeOf is the flake record of case name over the last FlakeWindow rows
// of the series that are not the run's own (runID); Samples is zero when
// the series has none.
func FlakeOf(name, runID string, series []SeriesRow) Flake {
	var prior []SeriesRow
	for _, row := range series {
		if row.Case == name && row.RunID != runID {
			prior = append(prior, row)
		}
	}
	if len(prior) > FlakeWindow {
		prior = prior[len(prior)-FlakeWindow:]
	}
	f := Flake{Samples: len(prior)}
	for _, row := range prior {
		if !row.Passed {
			f.Failures++
		}
	}
	if f.Samples > 0 {
		f.Rate = float64(f.Failures) / float64(f.Samples)
	}
	return f
}

// FlakeOfReport reads a report's "flake" block (a report decoded from
// JSON holds it as map[string]any).
func FlakeOfReport(r map[string]any) (Flake, bool) {
	switch block := r["flake"].(type) {
	case Flake:
		return block, true
	case map[string]any:
		return Flake{Rate: AsNumber(block["rate"]), Failures: int(AsNumber(block["failures"])), Samples: int(AsNumber(block["samples"]))}, true
	}
	return Flake{}, false
}

// SeriesPath is where a run rooted at output keeps its series: metrics.jsonl
// in the output directory's parent, so successive runs (each a fresh
// output directory beside the last) share one.
func SeriesPath(output string) string {
	return filepath.Join(filepath.Dir(output), "metrics.jsonl")
}

// AppendSeries appends one row to path as a single line (O_APPEND, so the
// suite's workers appending at once interleave whole lines).
func AppendSeries(path string, row SeriesRow) error {
	data, err := json.Marshal(row)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
		return err
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
	if err != nil {
		return err
	}
	defer f.Close()
	_, err = f.Write(append(data, '\n'))
	return err
}

// ReadSeries reads every row of path in file order; a missing file is an
// empty series and a corrupt line is skipped.
func ReadSeries(path string) ([]SeriesRow, error) {
	f, err := os.Open(path)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return nil, nil
		}
		return nil, err
	}
	defer f.Close()
	var rows []SeriesRow
	scanner := bufio.NewScanner(f)
	scanner.Buffer(make([]byte, 0, 64*1024), 4*1024*1024)
	for scanner.Scan() {
		var row SeriesRow
		if json.Unmarshal(scanner.Bytes(), &row) == nil && row.Case != "" {
			rows = append(rows, row)
		}
	}
	return rows, scanner.Err()
}

// DriftRule says when a metric's value has drifted from its trailing
// median: past Ratio times the median and Floor over it (Lower: below
// Ratio times the median and Floor under it, for a metric where less is
// worse). The floor keeps a tiny median from flagging noise (#176).
type DriftRule struct {
	Ratio float64
	Floor float64
	Lower bool
}

// DriftRules is the per-metric rule set; wall_ms keeps the suite's
// RegressionRatio and RegressionFloorMs. A metric without a rule never
// flags.
var DriftRules = map[string]DriftRule{
	"wall_ms":              {Ratio: 1.25, Floor: 5000},
	"boot_ms":              {Ratio: 1.5, Floor: 10000},
	"ticks_advanced":       {Ratio: 0.8, Floor: 1000, Lower: true},
	"wall_tps":             {Ratio: 0.8, Floor: 50, Lower: true},
	"waits":                {Ratio: 1.5, Floor: 5},
	"waits_stalled":        {Ratio: 1, Floor: 0.5},
	"max_quiet_ms":         {Ratio: 1.5, Floor: 10000},
	"native_calls":         {Ratio: 1.5, Floor: 200},
	"native_errors":        {Ratio: 1, Floor: 0.5},
	"native_bytes":         {Ratio: 1.5, Floor: 1 << 20},
	"reads_per_step_mean":  {Ratio: 1.5, Floor: 2},
	"cache_hit_ratio":      {Ratio: 0.8, Floor: 0.05, Lower: true},
	"native_queue_ms_mean": {Ratio: 1.5, Floor: 20},
	"native_exec_ms_mean":  {Ratio: 1.5, Floor: 20},
	"service_launches":     {Ratio: 1, Floor: 0.5},
	"evidence_bytes":       {Ratio: 1.5, Floor: 1 << 20},
}

// DriftWindow is how many prior rows of a case the trailing median spans.
const DriftWindow = 10

// DriftFlag is one metric of one case past its rule.
type DriftFlag struct {
	Case   string  `json:"case"`
	Metric string  `json:"metric"`
	Value  float64 `json:"value"`
	Median float64 `json:"median"`
	Ratio  float64 `json:"ratio"`
	// Samples is how many prior rows the median spans.
	Samples int `json:"samples"`
}

func (f DriftFlag) String() string {
	return fmt.Sprintf("%s %s %.6g vs median %.6g (%.2fx over %d)", f.Case, f.Metric, f.Value, f.Median, f.Ratio, f.Samples)
}

// Drift compares one case's block with its trailing median over the prior
// passing rows of the series (the last DriftWindow passes of the case, in
// series order, excluding rows of runID: the run's own appends; a failed
// or refused run's cost is not the case's) and lists every metric past
// its rule, in MetricNames order. A metric whose median is zero or absent
// never flags: there is nothing to drift from.
func Drift(name, runID string, m Metrics, series []SeriesRow) []DriftFlag {
	var prior []SeriesRow
	for _, row := range series {
		if row.Case == name && row.RunID != runID && row.Passed {
			prior = append(prior, row)
		}
	}
	if len(prior) > DriftWindow {
		prior = prior[len(prior)-DriftWindow:]
	}
	var flags []DriftFlag
	for _, metric := range MetricNames {
		rule, ok := DriftRules[metric]
		if !ok {
			continue
		}
		var samples []float64
		for _, row := range prior {
			if v, ok := row.Metrics[metric]; ok {
				samples = append(samples, v)
			}
		}
		median := medianOf(samples)
		if median <= 0 {
			continue
		}
		value := m[metric]
		drifted := value > median*rule.Ratio && value-median > rule.Floor
		if rule.Lower {
			drifted = value < median*rule.Ratio && median-value > rule.Floor
		}
		if drifted {
			flags = append(flags, DriftFlag{Case: name, Metric: metric, Value: value, Median: median, Ratio: value / median, Samples: len(samples)})
		}
	}
	return flags
}

func medianOf(values []float64) float64 {
	if len(values) == 0 {
		return 0
	}
	sorted := append([]float64(nil), values...)
	sort.Float64s(sorted)
	n := len(sorted)
	if n%2 == 1 {
		return sorted[n/2]
	}
	return (sorted[n/2-1] + sorted[n/2]) / 2
}

// RecordSeries appends the finalized report's block for case name to the
// series at path under runID, stamped with SourceRevision and the run's
// world seed, and returns the drift flags against the rows already there.
// The report gains "series" (the path), "flake" (FlakeOf over the prior
// rows, when there are any) and, when any, "drift". Errors are reported,
// not fatal: the run's verdict does not depend on its bookkeeping.
func RecordSeries(path, name, runID string, r Report) []DriftFlag {
	m, ok := MetricsOf(r)
	if !ok {
		return nil
	}
	r["series"] = path
	prior, err := ReadSeries(path)
	if err != nil {
		r["series_error"] = err.Error()
	}
	flags := Drift(name, runID, m, prior)
	if len(flags) > 0 {
		r["drift"] = flags
	}
	if flake := FlakeOf(name, runID, prior); flake.Samples > 0 {
		r["flake"] = flake
	}
	passed, _ := r["passed"].(bool)
	world, _ := WorldOf(r)
	row := SeriesRow{Case: name, RunID: runID, SourceRevision: SourceRevision(), Seed: world.Seed, Timestamp: time.Now().UTC(), Passed: passed, Metrics: m}
	if err := AppendSeries(path, row); err != nil {
		r["series_error"] = err.Error()
	}
	return flags
}

var sourceRevision struct {
	once sync.Once
	rev  string
}

// SourceRevision is the HEAD commit of the checkout enclosing the working
// directory, or "" without one; the series stamps every row with it so a
// drift can be read against the change that ran.
func SourceRevision() string {
	sourceRevision.once.Do(func() {
		cwd, err := os.Getwd()
		if err != nil {
			return
		}
		repo, ok := FindRepo(cwd)
		if !ok {
			return
		}
		out, err := exec.Command("git", "-C", repo, "rev-parse", "HEAD").Output()
		if err == nil {
			sourceRevision.rev = strings.TrimSpace(string(out))
		}
	})
	return sourceRevision.rev
}
