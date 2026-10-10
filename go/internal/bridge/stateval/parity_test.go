package stateval

import (
	"errors"
	"flag"
	"fmt"
	"math"
	"os"
	"slices"
	"sort"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

var update = flag.Bool("update", false, "rewrite testdata/not_mirrored_stats.txt")

// recordedEnv is the environment the recording was made in: core plus the
// DLCs the sidecar lists, no scenario stat factors (the debug start's
// scenario has none), classic mode off.
func recordedEnv(t *testing.T) Env {
	t.Helper()
	raw, err := os.ReadFile("../../observation/testdata/full_catalog.version")
	if err != nil {
		t.Fatal(err)
	}
	mods := map[string]bool{"ludeon.rimworld": true, "davidarcher.rimgovernor.native": true}
	for _, line := range strings.Split(string(raw), "\n") {
		if dlcs, ok := strings.CutPrefix(line, "dlcs="); ok {
			for _, dlc := range strings.Split(strings.TrimSpace(dlcs), ",") {
				mods[dlc] = true
			}
		}
	}
	return Env{ActiveMods: mods, ScenarioFactors: map[string]float32{}}
}

// TestModsOfMatchesTheRecordingSidecar: the mods the rows name are core and the
// DLCs the recording was made with, and the RimGovernor native mod.
func TestModsOfMatchesTheRecordingSidecar(t *testing.T) {
	want := recordedEnv(t).ActiveMods
	got := ModsOf(recordedcatalog.Catalog(t))
	for mod := range want {
		if !got[mod] {
			t.Errorf("mod %s has no def row", mod)
		}
	}
	for mod := range got {
		if !want[mod] {
			t.Errorf("rows name mod %s, the sidecar does not", mod)
		}
	}
}

// blocker is the class a failed evaluation names, or "" for no failure; any
// error that is not a NotMirrored fails the test.
func blocker(t *testing.T, what string, err error) string {
	t.Helper()
	if err == nil {
		return ""
	}
	var nm *bridge.NotMirrored
	if !errors.As(err, &nm) {
		t.Errorf("%s: %v", what, err)
		return "ERROR"
	}
	return nm.Class
}

// TestParityWithRecordedStatTable compares the evaluator with the game's own
// DefStatTable on every recorded row: every stat whose classes are all owned
// must reproduce the game's value, and ShouldShowFor must reproduce whether
// the stat is in the row at all. The stats that fail with NotMirrored are the
// golden set in testdata/not_mirrored_stats.txt (go test -update rewrites it):
// a stat leaves it only by porting the classes it names.
func TestParityWithRecordedStatTable(t *testing.T) {
	catalog := recordedcatalog.Catalog(t)
	wire := recordedcatalog.Wire(t)
	eval := New(catalog, recordedEnv(t))
	table := wire.StatValues

	type rowKind struct {
		terrain bool
		rows    []*o.DefStatRow
		subject func(*o.DefStatRow) Subject
	}
	kinds := []rowKind{
		{false, table.Rows, func(r *o.DefStatRow) Subject { return ThingSubject(r.DefName, r.StuffName) }},
		{true, table.TerrainRows, func(r *o.DefStatRow) Subject { return TerrainSubject(r.DefName) }},
	}
	valueBlocked := map[string]string{} // stat -> class
	showBlocked := map[string]string{}
	checked, matched, shownChecked := 0, 0, 0
	for _, kind := range kinds {
		for _, row := range kind.rows {
			in := map[int32]float32{}
			for i, s := range row.Stat {
				in[s] = row.Value[i]
			}
			subject := kind.subject(row)
			for index, stat := range table.Stats {
				want, shown := in[int32(index)]
				what := fmt.Sprintf("%s/%s/%s", row.DefName, row.StuffName, stat)
				gotShown, err := eval.ShouldShowFor(stat, subject)
				if class := blocker(t, what+" shown", err); class != "" {
					showBlocked[stat] = class
				} else if plannerStat(kind.terrain, stat) {
					// The table keeps this stat on every ThingDef row whether shown or not.
					if !shown {
						t.Fatalf("%s: planner stat missing from the row", what)
					}
				} else {
					shownChecked++
					if gotShown != shown {
						t.Fatalf("%s: shown %v, game %v", what, gotShown, shown)
					}
				}
				if !shown {
					continue
				}
				value, err := eval.Value(stat, subject)
				if class := blocker(t, what, err); class != "" {
					valueBlocked[stat] = class
					continue
				}
				checked++
				if math.Float32bits(value) != math.Float32bits(want) {
					t.Fatalf("%s: value %v, game %v", what, value, want)
				}
				matched++
			}
		}
	}
	t.Logf("%d values and %d shown flags reproduced; %d stats blocked on a value, %d on ShouldShowFor", matched, shownChecked, len(valueBlocked), len(showBlocked))
	if matched == 0 {
		t.Fatal("no recorded value was reproduced")
	}

	var lines []string
	for _, stat := range table.Stats {
		if valueBlocked[stat] != "" || showBlocked[stat] != "" {
			lines = append(lines, fmt.Sprintf("%s\tvalue=%s\tshown=%s", stat, orNone(valueBlocked[stat]), orNone(showBlocked[stat])))
		}
	}
	sort.Strings(lines)
	got := "# Stats the evaluator cannot answer yet for some recorded row: the first class\n# it names (stat, value blocker, ShouldShowFor blocker; - is answered, or a value never\n# reached because the stat is never shown). Regenerate with go test -update.\n" + strings.Join(lines, "\n") + "\n"
	const golden = "testdata/not_mirrored_stats.txt"
	if *update {
		if err := os.WriteFile(golden, []byte(got), 0o644); err != nil {
			t.Fatal(err)
		}
		return
	}
	want, err := os.ReadFile(golden)
	if err != nil {
		t.Fatal(err)
	}
	if string(want) != got {
		t.Errorf("the stats the evaluator cannot answer changed; run go test -update ./internal/bridge/stateval and review the diff\nwant:\n%s\ngot:\n%s", want, got)
	}
}

// plannerStat is a stat the native table keeps on every ThingDef row whether
// the game shows it or not (NativeDefinitionCatalogTool PlannerStats).
func plannerStat(terrain bool, stat string) bool { return !terrain && stat == "DeteriorationRate" }

func orNone(s string) string {
	if s == "" {
		return "-"
	}
	return s
}

// TestOwnerColumnMatchesEvaluator: stat_classes.tsv names an owner for exactly
// the classes the evaluator ports, as stateval.<Class>.
func TestOwnerColumnMatchesEvaluator(t *testing.T) {
	raw, err := os.ReadFile("../../../cmd/stataudit/stat_classes.tsv")
	if err != nil {
		t.Fatal(err)
	}
	var owned []string
	for _, line := range strings.Split(strings.TrimSpace(string(raw)), "\n")[1:] {
		f := strings.Split(line, "\t")
		class, kind, owner := f[0], f[1], f[len(f)-1]
		if owner == "unowned" {
			continue
		}
		if kind != "part" || owner != "stateval."+class {
			t.Errorf("%s (%s) is owned by %q: only StatParts are ported, as stateval.<Class>", class, kind, owner)
		}
		owned = append(owned, class)
	}
	slices.Sort(owned)
	if got := OwnedClasses(); !slices.Equal(owned, got) {
		t.Errorf("stat_classes.tsv owns %v, the evaluator ports %v", owned, got)
	}
}
