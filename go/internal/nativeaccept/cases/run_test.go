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
