package buildingruntime

import (
	"context"
	"fmt"
	"os"
	"slices"
	"strings"
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
)

// Undeclared planner reads: the recording facts reader notes every
// family a planner draws on (bridge.NoteRead from the native read seam and
// facts.Read), the queue compares each against the entry's declared
// families, and this binary fails when any read in any test was undeclared.
// The stub planner below reads on purpose; its name carries stubUndeclared
// so the collector keeps it out of the run's verdict.

const stubUndeclared = "stub-undeclared-"

var (
	undeclaredMu    sync.Mutex
	undeclaredReads []string
)

func collectUndeclaredRead(planner string, family bridge.FactFamily, source string) {
	undeclaredMu.Lock()
	defer undeclaredMu.Unlock()
	undeclaredReads = append(undeclaredReads, fmt.Sprintf("%s read %s (%s)", planner, family, source))
}

func TestMain(m *testing.M) {
	plannerReadAudit = collectUndeclaredRead
	code := m.Run()
	undeclaredMu.Lock()
	var real []string
	for _, read := range undeclaredReads {
		if !strings.HasPrefix(read, stubUndeclared) {
			real = append(real, read)
		}
	}
	undeclaredMu.Unlock()
	if len(real) > 0 && code == 0 {
		slices.Sort(real)
		real = slices.Compact(real)
		fmt.Fprintf(os.Stderr, "FAIL: planners read fact families they did not declare (clock_planner_catalog.go sections/families):\n  %s\n", strings.Join(real, "\n  "))
		code = 1
	}
	os.Exit(code)
}

// Every catalog entry declares at least one family of its own, and only
// real ones.
func TestPlannerCatalogDeclaresFamilies(t *testing.T) {
	t.Parallel()
	known := map[bridge.FactFamily]bool{}
	for _, family := range bridge.FactFamilies() {
		known[family] = true
	}
	for _, entry := range plannerCatalog {
		own := 0
		for family := range entry.declared() {
			if !known[family] {
				t.Errorf("%s declares unknown family %q", entry.name, family)
			}
			if family != bridge.FactDefinitions && family != bridge.FactIdentity {
				own++
			}
		}
		if own == 0 {
			t.Errorf("%s declares no fact family: give it sections or families", entry.name)
		}
		for _, section := range entry.sections {
			if !known[section.Family()] {
				t.Errorf("%s section %s has no family", entry.name, section)
			}
		}
	}
}

// The read note flags exactly the families an entry did not declare.
func TestPlannerReadNoteFlagsUndeclaredFamilies(t *testing.T) {
	t.Parallel()
	for _, entry := range plannerCatalog {
		declared := entry.declared()
		for _, family := range bridge.FactFamilies() {
			var flagged []string
			ctx := bridge.WithReadNote(context.Background(), func(f bridge.FactFamily, source string) {
				if !declared[f] {
					flagged = append(flagged, string(f))
				}
			})
			bridge.NoteRead(ctx, family, "test")
			if got, want := len(flagged) == 1, !declared[family]; got != want {
				t.Errorf("%s read %s: flagged=%v, want %v", entry.name, family, got, want)
			}
		}
	}
	// The sections a planner consumes are each declared through their family.
	for _, entry := range plannerCatalog {
		for _, section := range entry.sections {
			if !entry.declared()[section.Family()] {
				t.Errorf("%s consumes %s but does not declare %s", entry.name, section, section.Family())
			}
		}
	}
}

// A planner run through the scheduler's wave reads a held section and a
// native family: the read of its declared family passes, the other is
// reported to the audit under the planner's name.
func TestSchedulerReportsUndeclaredPlannerRead(t *testing.T) {
	t.Parallel()
	s, _ := schedulerFixture(t)
	stub := plannerEntry{name: stubUndeclared + "planner", class: classCritical, priority: plannerCritical, sections: []facts.Section{facts.Pawns},
		configured: func(*ClockSchedulerConfig) bool { return true },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			facts.Read[int](ctx, s.facts.store, facts.Pawns) // declared: pawns
			facts.Read[int](ctx, s.facts.store, facts.Rooms) // undeclared: rooms
			bridge.NoteRead(ctx, bridge.FactWorld, "native") // undeclared: world
			bridge.NoteRead(ctx, bridge.FactDefinitions, "native")
			return BuildingReasonNoDeficit, nil
		}}
	s.catalog = []plannerEntry{stub}
	if _, err := s.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	undeclaredMu.Lock()
	defer undeclaredMu.Unlock()
	var got []string
	for _, read := range undeclaredReads {
		if strings.HasPrefix(read, stubUndeclared+"planner") {
			got = append(got, read)
		}
	}
	slices.Sort(got)
	got = slices.Compact(got)
	want := []string{stubUndeclared + "planner read rooms (held:rooms)", stubUndeclared + "planner read world (native)"}
	if !slices.Equal(got, want) {
		t.Fatalf("audit saw %v, want %v", got, want)
	}
}
