package telemetry

import (
	"encoding/json"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// TestGrafanaDashboardsAreWellFormed keeps the provisioned dashboards parseable,
// limited to the documented instruments, and listed in their README.
func TestGrafanaDashboardsAreWellFormed(t *testing.T) {
	dir := filepath.Join("..", "..", "..", "tools", "observability", "dashboards")
	readme, err := os.ReadFile(filepath.Join(dir, "README.md"))
	if err != nil {
		t.Fatal(err)
	}
	sources, err := os.ReadFile(filepath.Join(dir, "..", "datasources.yaml"))
	if err != nil {
		t.Fatal(err)
	}
	files, _ := filepath.Glob(filepath.Join(dir, "*.json"))
	if len(files) == 0 {
		t.Fatal("no dashboards found")
	}
	name := regexp.MustCompile(`rimgovernor_[a-z_]+`)
	known := regexp.MustCompile(`^rimgovernor_(native_phase_duration_milliseconds(_bucket)?|native_calls_total|scheduler_(step_duration|stop_latency|controller_pause|stop_pause|critical_wave_duration)_milliseconds(_bucket|_sum|_count)?|game_ticks_per_second|action_outcomes_total|admission_decisions_total|telemetry_dropped_total)$`)
	uids := map[string]bool{}
	for _, f := range files {
		raw, err := os.ReadFile(f)
		if err != nil {
			t.Fatal(err)
		}
		var d struct {
			UID    string `json:"uid"`
			Panels []struct {
				ID      int    `json:"id"`
				Title   string `json:"title"`
				Type    string `json:"type"`
				Targets []struct {
					Expr      string `json:"expr"`
					QueryText string `json:"queryText"`
					Source    struct {
						Type string `json:"type"`
						UID  string `json:"uid"`
					} `json:"datasource"`
				} `json:"targets"`
			} `json:"panels"`
		}
		if err := json.Unmarshal(raw, &d); err != nil {
			t.Fatalf("%s: %v", f, err)
		}
		if d.UID == "" || uids[d.UID] {
			t.Errorf("%s: missing or duplicate uid %q", f, d.UID)
		}
		uids[d.UID] = true
		ids := map[int]bool{}
		for _, p := range d.Panels {
			if ids[p.ID] {
				t.Errorf("%s: duplicate panel id %d", f, p.ID)
			}
			ids[p.ID] = true
			if p.Type != "text" && !strings.Contains(string(readme), "| "+p.Title+" |") {
				t.Errorf("%s: panel %q is not listed in README.md", f, p.Title)
			}
			for _, tg := range p.Targets {
				if tg.Source.Type == "frser-sqlite-datasource" {
					if !strings.Contains(string(sources), "uid: "+tg.Source.UID) || !strings.Contains(tg.QueryText, "concern_transitions") {
						t.Errorf("%s: panel %q: SQLite target needs a provisioned uid and a concern_transitions query", f, p.Title)
					}
					continue
				}
				if tg.Source.Type == "loki" {
					if !strings.Contains(tg.Expr, `service_name="rimgovernor-serve"`) {
						t.Errorf("%s: panel %q: Loki target must select service_name=\"rimgovernor-serve\"", f, p.Title)
					}
					continue
				}
				for _, m := range name.FindAllString(tg.Expr, -1) {
					if !known.MatchString(m) {
						t.Errorf("%s: panel %q queries unknown metric %q", f, p.Title, m)
					}
				}
			}
		}
	}
}
