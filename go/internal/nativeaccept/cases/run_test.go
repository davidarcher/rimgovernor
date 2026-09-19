package cases

import (
	"path/filepath"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/store/core"
)

// An Owned case's session has no game: Config, Report and GABSPID answer
// without one and Release refuses rather than dereferencing it.
func TestOwnedSessionWithoutGame(t *testing.T) {
	report := na.Report{}
	cfg := &na.Config{Root: "r"}
	s := &session{config: cfg, report: report}
	if s.Config() != cfg || s.GABSPID() != 0 {
		t.Fatalf("Config/GABSPID = %v/%d", s.Config(), s.GABSPID())
	}
	s.Report()["x"] = 1
	if report["x"] != 1 {
		t.Fatal("Report is not the run's report")
	}
	if err := s.Release(); err == nil {
		t.Fatal("Release without a game should fail")
	}
}

// RequestID is the base on a fresh run; a resumed run suffixes the run id
// so a replayed submission is a new request to the restored store (#307),
// and the suffix holds still for the run's relaunches.
func TestRequestIDResumeSuffix(t *testing.T) {
	fresh := &session{}
	if got := fresh.RequestID("case-plan-1"); got != "case-plan-1" {
		t.Fatalf("fresh RequestID = %q", got)
	}
	resumed := &session{resumeSuffix: Options{Output: filepath.Join("out", "run-2")}.RunID()}
	got := resumed.RequestID("case-plan-1")
	if got != "case-plan-1-run-2" {
		t.Fatalf("resumed RequestID = %q", got)
	}
	if again := resumed.RequestID("case-plan-1"); again != got {
		t.Fatalf("RequestID drifted within a run: %q then %q", got, again)
	}
	if err := core.SubmissionID(got); err != nil {
		t.Fatalf("suffixed id %q is not a valid submission id: %v", got, err)
	}
}

func TestSeededStartPinsDebugAndScenarioStartsOnly(t *testing.T) {
	if s, err := seededStart(DebugStart{}, ""); err != nil || s.(na.DebugStart).Seed != "" {
		t.Errorf("unseeded = %+v, %v", s, err)
	}
	if s, err := seededStart(DebugStart{Size: na.DebugStart{Biomes: "Tundra"}}, "abc"); err != nil || s != (na.DebugStart{Biomes: "Tundra", Seed: "abc"}) {
		t.Errorf("debug = %+v, %v", s, err)
	}
	if s, err := seededStart(Scenario{Spec: na.ScenarioStart{Scenario: "LostTribe"}}, "abc"); err != nil || s.(na.ScenarioStart).Seed != "abc" {
		t.Errorf("scenario = %+v, %v", s, err)
	}
	s, err := seededStart(Fixture{Op: "test/x"}, "abc")
	if f, ok := s.(na.Fixture); err != nil || !ok || f.Op != "test/x" || f.On != (na.DebugStart{Seed: "abc"}) {
		t.Errorf("fixture = %+v, %v", s, err)
	}
	if _, err := seededStart(Save{Name: "baseline"}, "abc"); err == nil {
		t.Error("a save start took a seed")
	}
	if _, err := seededStart(Fixture{Op: "test/x", On: Save{Name: "baseline"}}, "abc"); err == nil {
		t.Error("a fixture on a save took a seed")
	}
}

func TestCaseOutputByAttempt(t *testing.T) {
	c := Case{Name: "a/b"}
	out := Options{Output: filepath.Join("out")}
	if got := out.CaseOutput(c); got != filepath.Join("out", "a", "b") {
		t.Errorf("attempt 0 = %q", got)
	}
	out.Attempt = 1
	if got := out.CaseOutput(c); got != filepath.Join("out", "a", "b") {
		t.Errorf("attempt 1 = %q", got)
	}
	out.Attempt = 3
	if got := out.CaseOutput(c); got != filepath.Join("out", "repeat", "3", "a", "b") {
		t.Errorf("attempt 3 = %q", got)
	}
}
