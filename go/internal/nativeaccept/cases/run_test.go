package cases

import (
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
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
