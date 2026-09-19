package sustainedfood

import "testing"

func colonySample(tick uint64, colony map[string]any) map[string]any {
	return map[string]any{"at": "2026-01-01T00:00:00Z", "tick": tick, "colony": colony}
}

func TestDeriveColonyOutcomeTracksFoodAndColonists(t *testing.T) {
	timeline := []map[string]any{
		colonySample(100, map[string]any{"colonists": 4.0, "foodRunwayDays": 3.0, "foodNutrition": 20.0, "downed": 0.0, "moodMean": 0.6}),
		colonySample(200, map[string]any{"error": "read timed out"}),
		colonySample(300, map[string]any{"colonists": 4.0, "foodRunwayDays": 0.5, "foodNutrition": 2.0, "downed": 2.0, "moodMean": 0.3}),
		colonySample(400, map[string]any{"colonists": 2.0, "foodRunwayDays": 1.0, "foodNutrition": 4.0, "downed": 1.0, "moodMean": 0.4}),
		{"at": "2026-01-01T00:00:20Z"},
	}
	out := DeriveColonyOutcome(timeline)
	if out.Samples != 4 || out.Unreadable != 1 {
		t.Fatalf("samples %d unreadable %d", out.Samples, out.Unreadable)
	}
	if out.MinFoodRunwayDays == nil || *out.MinFoodRunwayDays != 0.5 || out.MinFoodRunwayTick == nil || *out.MinFoodRunwayTick != 300 {
		t.Fatalf("min runway %+v", out)
	}
	if out.MinFoodNutrition == nil || *out.MinFoodNutrition != 2.0 || out.FinalFoodRunwayDays == nil || *out.FinalFoodRunwayDays != 1.0 {
		t.Fatalf("food %+v", out)
	}
	if out.FirstColonists == nil || *out.FirstColonists != 4 || out.FinalColonists == nil || *out.FinalColonists != 2 || out.ColonistsLost != 2 {
		t.Fatalf("colonists %+v", out)
	}
	if out.MaxDowned != 2 || out.FinalDowned != 1 {
		t.Fatalf("downed %+v", out)
	}
	if out.MinMoodMean == nil || *out.MinMoodMean != 0.3 || out.FinalMoodMean == nil || *out.FinalMoodMean != 0.4 {
		t.Fatalf("mood %+v", out)
	}
}

func TestDeriveColonyOutcomeWithoutColonyBlocks(t *testing.T) {
	out := DeriveColonyOutcome([]map[string]any{{"at": "2026-01-01T00:00:00Z"}})
	if out.Samples != 0 || out.MinFoodRunwayDays != nil || out.FirstColonists != nil {
		t.Fatalf("%+v", out)
	}
}

func TestDeriveColonyOutcomeUnknownFactsStayNull(t *testing.T) {
	out := DeriveColonyOutcome([]map[string]any{colonySample(1, map[string]any{"colonists": nil, "foodRunwayDays": nil, "downed": 0.0, "moodMean": nil})})
	if out.Samples != 1 || out.MinFoodRunwayDays != nil || out.FirstColonists != nil || out.MinMoodMean != nil {
		t.Fatalf("%+v", out)
	}
}
