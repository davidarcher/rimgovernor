// Package cost prices acceptance cases from an earlier run's timings so an
// agent choosing between the cases a change owes can see that one costs 4
// minutes and another 18 (#283). `acceptance list -cost` and cmd/affected
// share it.
//
// A baseline is either a suite result.json (one wall_ms/boot_ms row per
// case, the file `acceptance suite -baseline` also takes) or a metrics
// series (metrics.jsonl, nativeaccept.SeriesRow lines), where a case's
// price is the median of its last DriftWindow passing rows. Cases the
// baseline does not time are untimed and priced as unknown, never zero.
package cost

import (
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// Row is one case's baseline price: the wall time of its run and the boot
// time inside it (zero when the run attached to a kept process).
type Row struct {
	Wall time.Duration
	Boot time.Duration
}

// Run is the wall time net of boot: what the case itself costs.
func (r Row) Run() time.Duration { return r.Wall - r.Boot }

// Table is a baseline's prices by case name.
type Table struct {
	Path string
	rows map[string]Row
}

// Load reads a baseline: a metrics series when path ends in .jsonl, a suite
// result.json otherwise.
func Load(path string) (*Table, error) {
	if strings.HasSuffix(strings.ToLower(path), ".jsonl") {
		return loadSeries(path)
	}
	return loadSuite(path)
}

func loadSuite(path string) (*Table, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("baseline: %w", err)
	}
	var prior struct {
		Cases []struct {
			Name   string  `json:"name"`
			WallMs float64 `json:"wall_ms"`
			BootMs float64 `json:"boot_ms"`
		} `json:"cases"`
	}
	if err := json.Unmarshal(data, &prior); err != nil {
		return nil, fmt.Errorf("baseline: %s is not a suite result.json: %w", path, err)
	}
	t := &Table{Path: path, rows: map[string]Row{}}
	for _, r := range prior.Cases {
		if r.Name == "" || r.WallMs <= 0 {
			continue
		}
		t.rows[r.Name] = Row{Wall: ms(r.WallMs), Boot: ms(r.BootMs)}
	}
	return t, nil
}

func loadSeries(path string) (*Table, error) {
	if _, err := os.Stat(path); err != nil {
		return nil, fmt.Errorf("baseline: %w", err)
	}
	rows, err := na.ReadSeries(path)
	if err != nil {
		return nil, fmt.Errorf("baseline: %w", err)
	}
	walls, boots := map[string][]float64{}, map[string][]float64{}
	for _, row := range rows {
		if !row.Passed {
			continue
		}
		wall, ok := row.Metrics["wall_ms"]
		if !ok || wall <= 0 {
			continue
		}
		walls[row.Case] = append(walls[row.Case], wall)
		boots[row.Case] = append(boots[row.Case], row.Metrics["boot_ms"])
	}
	t := &Table{Path: path, rows: map[string]Row{}}
	for name, w := range walls {
		b := boots[name]
		if len(w) > na.DriftWindow {
			w, b = w[len(w)-na.DriftWindow:], b[len(b)-na.DriftWindow:]
		}
		t.rows[name] = Row{Wall: ms(median(w)), Boot: ms(median(b))}
	}
	return t, nil
}

func ms(v float64) time.Duration { return time.Duration(v * float64(time.Millisecond)) }

func median(values []float64) float64 {
	sorted := append([]float64(nil), values...)
	sort.Float64s(sorted)
	n := len(sorted)
	if n == 0 {
		return 0
	}
	if n%2 == 1 {
		return sorted[n/2]
	}
	return (sorted[n/2-1] + sorted[n/2]) / 2
}

// Of is name's price and whether the baseline times it. A nil table times
// nothing.
func (t *Table) Of(name string) (Row, bool) {
	if t == nil {
		return Row{}, false
	}
	r, ok := t.rows[name]
	return r, ok
}

// Names lists the cases the baseline times, sorted.
func (t *Table) Names() []string {
	if t == nil {
		return nil
	}
	names := make([]string, 0, len(t.rows))
	for name := range t.rows {
		names = append(names, name)
	}
	sort.Strings(names)
	return names
}

// Summary totals a name set: the timed cases' wall and boot and how many
// names the baseline does not time.
type Summary struct {
	Timed, Untimed int
	Wall, Boot     time.Duration
}

// Summarize prices names against t.
func Summarize(t *Table, names []string) Summary {
	var s Summary
	for _, name := range names {
		r, ok := t.Of(name)
		if !ok {
			s.Untimed++
			continue
		}
		s.Timed++
		s.Wall += r.Wall
		s.Boot += r.Boot
	}
	return s
}

// String renders the total on one line: "12 cases, 41m10s wall (boot 2m3s), 3 untimed".
func (s Summary) String() string {
	parts := []string{fmt.Sprintf("%d case%s", s.Timed+s.Untimed, plural(s.Timed+s.Untimed))}
	if s.Timed > 0 {
		parts = append(parts, fmt.Sprintf("%s wall (boot %s)", Format(s.Wall), Format(s.Boot)))
	}
	if s.Untimed > 0 {
		parts = append(parts, fmt.Sprintf("%d untimed", s.Untimed))
	}
	return strings.Join(parts, ", ")
}

func plural(n int) string {
	if n == 1 {
		return ""
	}
	return "s"
}

// Format renders a duration to the second ("4m12s", "18s").
func Format(d time.Duration) string { return d.Round(time.Second).String() }
