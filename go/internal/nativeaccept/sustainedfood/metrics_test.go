package sustainedfood

import "testing"

func sample(at, need, status string, methodCount int, plans []map[string]any) map[string]any {
	return map[string]any{"at": at, "need": need, "status": status, "method_count": methodCount, "plans": plans}
}

func pendingPlan(id string, count int) map[string]any {
	return map[string]any{"plan": id, "stages": map[string]int{"pending": count}}
}

func progressingPlan(id string, pending, dispatched int) map[string]any {
	return map[string]any{"plan": id, "stages": map[string]int{"pending": pending, "awaiting_observation": dispatched}}
}

func TestDeriveMetricsDetectsStallAndRecovery(t *testing.T) {
	timeline := []map[string]any{
		sample("2026-01-01T00:00:00Z", "Critical", "Active", 1, []map[string]any{pendingPlan("p1", 6)}),
		sample("2026-01-01T00:00:05Z", "Critical", "Active", 1, []map[string]any{pendingPlan("p1", 6)}),
		sample("2026-01-01T00:00:10Z", "Critical", "Active", 1, []map[string]any{pendingPlan("p1", 6)}),
		sample("2026-01-01T00:00:15Z", "Critical", "Active", 1, []map[string]any{progressingPlan("p1", 2, 4)}),
		sample("2026-01-01T00:00:20Z", "Stable", "Active", 1, []map[string]any{progressingPlan("p2", 0, 2)}),
	}
	m := DeriveMetrics(timeline)
	if m.Samples != 5 {
		t.Fatalf("Samples = %d, want 5", m.Samples)
	}
	if m.NeedCounts["Critical"] != 4 || m.NeedCounts["Stable"] != 1 {
		t.Fatalf("NeedCounts = %#v", m.NeedCounts)
	}
	if m.DistinctPlans != 2 {
		t.Fatalf("DistinctPlans = %d, want 2", m.DistinctPlans)
	}
	if len(m.Stalls) != 1 {
		t.Fatalf("Stalls = %#v, want exactly one stall window", m.Stalls)
	}
	if m.Stalls[0].Samples != 3 {
		t.Fatalf("stall samples = %d, want 3", m.Stalls[0].Samples)
	}
	if m.StalledSamples != 3 {
		t.Fatalf("StalledSamples = %d, want 3", m.StalledSamples)
	}
	if !m.RecoveredAllStalls {
		t.Fatalf("RecoveredAllStalls = false, want true (timeline ends past the stall)")
	}
}

func TestDeriveMetricsOpenEndedStallNotRecovered(t *testing.T) {
	timeline := []map[string]any{
		sample("2026-01-01T00:00:00Z", "Critical", "Active", 1, []map[string]any{pendingPlan("p1", 6)}),
		sample("2026-01-01T00:00:05Z", "Critical", "Active", 1, []map[string]any{pendingPlan("p1", 6)}),
	}
	m := DeriveMetrics(timeline)
	if len(m.Stalls) != 1 {
		t.Fatalf("Stalls = %#v, want exactly one stall window", m.Stalls)
	}
	if m.RecoveredAllStalls {
		t.Fatalf("RecoveredAllStalls = true, want false (timeline ends mid-stall)")
	}
}

func TestDeriveMetricsEmptyTimeline(t *testing.T) {
	m := DeriveMetrics(nil)
	if m.Samples != 0 || m.DistinctPlans != 0 || len(m.Stalls) != 0 || m.StalledFraction != 0 {
		t.Fatalf("DeriveMetrics(nil) = %#v, want all-zero", m)
	}
}

func TestDeriveMetricsSkipsErrorSamples(t *testing.T) {
	timeline := []map[string]any{
		{"error": "boom", "at": "2026-01-01T00:00:00Z"},
		sample("2026-01-01T00:00:05Z", "Stable", "Active", 1, []map[string]any{progressingPlan("p1", 0, 1)}),
	}
	m := DeriveMetrics(timeline)
	if m.Samples != 1 {
		t.Fatalf("Samples = %d, want 1 (error sample skipped)", m.Samples)
	}
}
