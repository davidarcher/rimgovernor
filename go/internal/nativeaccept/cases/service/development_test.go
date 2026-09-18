package service

import (
	"strings"
	"testing"
	"time"
)

func ptr[T any](v T) *T { return &v }

func ranking(rows ...Row) Sample {
	return Sample{At: time.Unix(0, 0), Phase: "before-restart", Development: &Development{Tick: 5000, Workers: ptr(3), Capacity: 2, Labor: []LaborRow{{Work: "Construction", Free: 0}, {Work: "Research", Free: 1}}, Rows: rows}}
}

func TestCheckSampleAcceptsBoundedExplainedRanking(t *testing.T) {
	s := ranking(
		Row{Goal: "EnsureResearch", Deficit: ptr(1.0), WaitingSince: 100, Selected: true},
		Row{Goal: "EnsureComfort", Deficit: ptr(0.5), WaitingSince: 100, Reason: "labor_unavailable", Bottleneck: "Construction"},
		Row{Goal: "MaintainWood", Deficit: ptr(0.3), Risk: ptr(1.0), WaitingSince: 200, Reason: "risk_deferred"},
	)
	if bad := checkSample(s, 2, "MicroelectronicsBasics"); len(bad) != 0 {
		t.Fatal(bad)
	}
}

func TestCheckSampleFlagsEveryContractBreach(t *testing.T) {
	cases := map[string]Sample{
		"deferred without a reason":           ranking(Row{Goal: "EnsureComfort", WaitingSince: 1}),
		"selected with reason":                ranking(Row{Goal: "EnsureComfort", Selected: true, Reason: "capacity_committed"}),
		"labor_unavailable without":           ranking(Row{Goal: "EnsureComfort", Reason: "labor_unavailable", Bottleneck: "Mining"}),
		"bottleneck \"Construction\" on":      ranking(Row{Goal: "EnsureComfort", Reason: "capacity_committed", Bottleneck: "Construction"}),
		"workers_unknown despite":             ranking(Row{Goal: "EnsureComfort", Reason: "workers_unknown"}),
		"review-time research read missing":   ranking(Row{Goal: "EnsureResearch", Reason: "deficit_unknown"}),
		"waitingSince 6000 after review tick": ranking(Row{Goal: "EnsureComfort", WaitingSince: 6000, Reason: "capacity_committed"}),
		"admitted 3 beyond capacity 2":        ranking(Row{Goal: "a", Selected: true}, Row{Goal: "b", Selected: true}, Row{Goal: "c", Selected: true}),
	}
	for want, s := range cases {
		bad := checkSample(s, 2, "MicroelectronicsBasics")
		if len(bad) == 0 || !strings.Contains(strings.Join(bad, "\n"), want) {
			t.Errorf("%s: got %v", want, bad)
		}
	}
	over := ranking()
	over.Development.Capacity = 3
	if bad := checkSample(over, 2, ""); len(bad) != 1 || !strings.Contains(bad[0], "beyond project limit") {
		t.Fatal(bad)
	}
	over.Development.Capacity = 2
	over.Development.Workers = ptr(1)
	if bad := checkSample(over, 2, ""); len(bad) != 1 || !strings.Contains(bad[0], "beyond 1 workers") {
		t.Fatal(bad)
	}
	over.Development.Workers = nil
	if bad := checkSample(over, 2, ""); len(bad) != 1 || !strings.Contains(bad[0], "unknown workers") {
		t.Fatal(bad)
	}
	if bad := checkSample(Sample{}, 2, "x"); bad != nil {
		t.Fatal("unranked sample is not a breach", bad)
	}
}

func TestCheckRestartRetainsWaitingAgesAndTick(t *testing.T) {
	before := Development{Tick: 9000, Rows: []Row{{Goal: "EnsureComfort", WaitingSince: 100, Reason: "capacity_committed"}, {Goal: "EnsureResearch", WaitingSince: 100, Selected: true}}}
	after := Development{Tick: 12000, Rows: []Row{{Goal: "EnsureComfort", WaitingSince: 100, Reason: "capacity_committed"}, {Goal: "EnsureResearch", WaitingSince: 12000, Reason: "capacity_committed"}}}
	if bad := checkRestart(before, after); len(bad) != 0 {
		t.Fatal("selected goals may restart their age; waiting goals kept theirs", bad)
	}
	after.Rows[0].WaitingSince = 12000
	after.Tick = 8000
	bad := checkRestart(before, after)
	if len(bad) != 2 || !strings.Contains(bad[0], "rewound") || !strings.Contains(bad[1], "EnsureComfort: waiting age rewritten") {
		t.Fatal(bad)
	}
}

func TestDeriveMetricsCountsAdmissionReasonsAndWaits(t *testing.T) {
	a := ranking(Row{Goal: "EnsureResearch", Selected: true}, Row{Goal: "EnsureComfort", WaitingSince: 2000, Reason: "capacity_committed"})
	b := ranking(Row{Goal: "EnsureResearch", Committed: true}, Row{Goal: "EnsureComfort", WaitingSince: 2000, Reason: "labor_unavailable", Bottleneck: "Construction"})
	b.Development.Tick = 7500
	m := deriveMetrics([]Sample{a, a, b, {}})
	if m.Samples != 4 || m.Ranked != 3 || m.Reviews != 2 || !m.ResearchRanked {
		t.Fatalf("%+v", m)
	}
	if m.Selected["EnsureResearch"] != 2 || m.Committed["EnsureResearch"] != 1 || m.Reasons["capacity_committed"] != 2 || m.Reasons["labor_unavailable"] != 1 || m.LongestWait["EnsureComfort"] != 5500 {
		t.Fatalf("%+v", m)
	}
	if strings.Join(m.Goals, ",") != "EnsureComfort,EnsureResearch" {
		t.Fatal(m.Goals)
	}
}
