package main

import "testing"

func TestWorkshopBenchCompletedSeesActiveAndRetiredPlans(t *testing.T) {
	t.Parallel()
	done := map[string]any{"plan": "routine-workshop-abc", "actions": 1, "stages": map[string]int{"completed": 1}}
	open := map[string]any{"plan": "routine-workshop-abc", "actions": 2, "stages": map[string]int{"completed": 1, "pending": 1}}
	shell := map[string]any{"plan": "routine-shell-abc", "actions": 1, "stages": map[string]int{"completed": 1}}
	for name, sample := range map[string]map[string]any{
		"active done":  {"plans": []map[string]any{done}},
		"retired done": {"plans": []map[string]any{}, "retired_plans": []map[string]any{done}},
	} {
		if !workshopBenchCompleted(sample) {
			t.Fatal(name)
		}
	}
	for name, sample := range map[string]map[string]any{
		"open":       {"plans": []map[string]any{open}},
		"shell only": {"retired_plans": []map[string]any{shell}},
		"empty":      {},
	} {
		if workshopBenchCompleted(sample) {
			t.Fatal(name)
		}
	}
}
