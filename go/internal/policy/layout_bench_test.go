package policy

import (
	"compress/gzip"
	"encoding/json"
	"fmt"
	"os"
	"testing"
	"time"
)

// baselineSurveyPath is one real MapSurvey read from the committed baseline
// save (250x250) through startersite's RIMGOVERNOR_SURVEY_DUMP (#1280).
const baselineSurveyPath = "testdata/baseline-survey.json.gz"

func loadSurvey(tb testing.TB, path string) MapSurvey {
	tb.Helper()
	f, err := os.Open(path)
	if err != nil {
		tb.Fatal(err)
	}
	defer f.Close()
	z, err := gzip.NewReader(f)
	if err != nil {
		tb.Fatal(err)
	}
	var s MapSurvey
	if err := json.NewDecoder(z).Decode(&s); err != nil {
		tb.Fatal(err)
	}
	return s
}

func TestBaselineSurveyFixture(t *testing.T) {
	s := loadSurvey(t, baselineSurveyPath)
	if s.Bounds != (Bounds{Width: 250, Height: 250}) {
		t.Fatalf("bounds %+v, want 250x250", s.Bounds)
	}
	rich := 0
	for _, c := range s.Cells {
		if c.Fertility > 1.0 {
			rich++
		}
	}
	if rich == 0 {
		t.Fatalf("no rich cells among %d", len(s.Cells))
	}
}

func BenchmarkLayoutZone(b *testing.B) {
	s := loadSurvey(b, baselineSurveyPath)
	b.ResetTimer()
	for b.Loop() {
		Zone(s)
	}
}

func BenchmarkLayoutPlanCore(b *testing.B) {
	s := loadSurvey(b, baselineSurveyPath)
	zones := Zone(s)
	for _, pawns := range []int{3, 8} {
		b.Run(fmt.Sprintf("pawns=%d", pawns), func(b *testing.B) {
			for b.Loop() {
				corePlan(zones, pawns, BuildTierCamp)
			}
		})
	}
}

// BenchmarkLayoutSiteCore is the whole fresh-siting pass (#1285); it fails
// when one pass runs over siteBudget.
func BenchmarkLayoutSiteCore(b *testing.B) {
	s := loadSurvey(b, baselineSurveyPath)
	zones := Zone(s)
	b.ResetTimer()
	start, n := time.Now(), 0
	for b.Loop() {
		SiteCore(LayoutPlan{Zones: zones}, s, 3, 1, BuildTierCamp)
		n++
	}
	if per := time.Since(start) / time.Duration(n); per > siteBudget {
		b.Fatalf("siting took %v, budget %v", per, siteBudget)
	}
}

func BenchmarkLayoutPlanPerimeter(b *testing.B) {
	s := loadSurvey(b, baselineSurveyPath)
	plan := corePlan(Zone(s), 3, BuildTierCamp)
	b.ResetTimer()
	for b.Loop() {
		PlanPerimeter(plan, s)
	}
}
