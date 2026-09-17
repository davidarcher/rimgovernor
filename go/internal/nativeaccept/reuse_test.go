package nativeaccept

import (
	"strings"
	"testing"
)

func cleanState() ResetState {
	return ResetState{
		ColonyID: "colony-1", LoadToken: "token-2", Tick: 10, Paused: true,
		Stock: map[string]StockCounts{"WoodLog": {Units: 300, Forbidden: 0}, "Steel": {Units: 50, Forbidden: -1}},
	}
}

func TestCheckResetAcceptsCleanReload(t *testing.T) {
	baseline := cleanState()
	baseline.LoadToken = "token-1"
	now := cleanState()
	if err := CheckReset(now, &baseline, map[string]string{"token-1": "case-1"}); err != nil {
		t.Fatalf("clean reload rejected: %v", err)
	}
}

func TestCheckResetSkipsUnsampledForbidden(t *testing.T) {
	baseline := cleanState()
	baseline.LoadToken = "token-1"
	now := cleanState()
	now.Stock["Steel"] = StockCounts{Units: 50, Forbidden: 7}
	if err := CheckReset(now, &baseline, map[string]string{"token-1": "case-1"}); err != nil {
		t.Fatalf("forbidden count compared against an unsampled baseline: %v", err)
	}
}

func TestCheckResetFirstLoadNeedsNoBaseline(t *testing.T) {
	if err := CheckReset(cleanState(), nil, map[string]string{}); err != nil {
		t.Fatalf("first load rejected: %v", err)
	}
}

func TestCheckResetRejectsEveryViolation(t *testing.T) {
	baseline := cleanState()
	baseline.LoadToken = "token-1"
	cases := map[string]func(*ResetState){
		"load token %q was already issued": func(s *ResetState) { s.LoadToken = "token-1" },
		"missing colony id or load token":  func(s *ResetState) { s.LoadToken = "" },
		"not paused":                       func(s *ResetState) { s.Paused = false },
		"authority is still active":        func(s *ResetState) { s.AuthorityActive = true },
		"owned draft claim(s) survive":     func(s *ResetState) { s.OwnedDrafts = 1 },
		"differs from the baseline tick":   func(s *ResetState) { s.Tick = 11 },
		"stock differs":                    func(s *ResetState) { s.Stock["WoodLog"] = StockCounts{Units: 300, Forbidden: 5} },
		"baseline: Steel":                  func(s *ResetState) { delete(s.Stock, "Steel") },
		"baseline: Silver":                 func(s *ResetState) { s.Stock["Silver"] = StockCounts{Units: 1} },
	}
	for want, mutate := range cases {
		now := cleanState()
		mutate(&now)
		err := CheckReset(now, &baseline, map[string]string{"token-1": "case-1"})
		if err == nil {
			t.Errorf("%s: violation accepted", want)
			continue
		}
		key := strings.SplitN(want, " %q", 2)[0]
		if !strings.Contains(err.Error(), key) {
			t.Errorf("%s: error %q does not name the violation", want, err)
		}
	}
}

func TestCheckResetReportsEveryProblemAtOnce(t *testing.T) {
	baseline := cleanState()
	now := cleanState()
	now.Paused = false
	now.AuthorityActive = true
	err := CheckReset(now, &baseline, map[string]string{"token-2": "case-1"})
	if err == nil {
		t.Fatal("expected an error")
	}
	for _, part := range []string{"already issued", "not paused", "authority is still active"} {
		if !strings.Contains(err.Error(), part) {
			t.Errorf("error %q missing %q", err, part)
		}
	}
}

func TestRetiredLifecycleRefusesCases(t *testing.T) {
	g := &GameReuse{retired: true, reason: "test"}
	if _, err := g.BeginCase(nil, "x", "save", t.TempDir()); err != ErrReuseRetired {
		t.Fatalf("BeginCase after retirement: %v", err)
	}
	if _, err := g.Session(nil); err != ErrReuseRetired {
		t.Fatalf("Session after retirement: %v", err)
	}
	if err := g.Retire(nil, "again"); err != nil {
		t.Fatalf("Retire must be idempotent: %v", err)
	}
	if retired, reason := g.Retired(); !retired || reason != "test" {
		t.Fatalf("retirement reason overwritten: %v %q", retired, reason)
	}
}
