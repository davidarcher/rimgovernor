package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Each dashboard link names the uid of a provisioned dashboard file, and
// the allowlist takes exactly those URLs and nothing under them.
func TestDashboardLinksMatchProvisionedDashboards(t *testing.T) {
	files, _ := filepath.Glob(filepath.Join("..", "..", "..", "tools", "observability", "dashboards", "*.json"))
	var all strings.Builder
	for _, f := range files {
		raw, err := os.ReadFile(f)
		if err != nil {
			t.Fatal(err)
		}
		all.Write(raw)
	}
	if len(dashboardLinks) == 0 || all.Len() == 0 {
		t.Fatal("no dashboard links or dashboards")
	}
	for _, d := range dashboardLinks {
		uid, ok := strings.CutPrefix(d.URL, grafanaURL+"/d/")
		if !ok || strings.Contains(uid, "/") || strings.Contains(uid, "?") {
			t.Errorf("%s: %q is not a bare dashboard URL", d.Name, d.URL)
		}
		if !strings.Contains(all.String(), `"uid": "`+uid+`"`) {
			t.Errorf("%s: no provisioned dashboard has uid %q", d.Name, uid)
		}
		if err := allowedLink(d.URL); err != nil {
			t.Errorf("%s refused: %v", d.Name, err)
		}
		for _, bad := range []string{d.URL + "/", d.URL + "?x=1", d.URL + "?kiosk", d.URL + "x"} {
			if allowedLink(bad) == nil {
				t.Errorf("%q allowed", bad)
			}
		}
	}
}

func TestAllowedLink(t *testing.T) {
	if err := allowedLink(playerGuideURL); err != nil {
		t.Fatalf("player guide refused: %v", err)
	}
	if err := allowedLink(grafanaURL); err != nil {
		t.Fatalf("grafana refused: %v", err)
	}
	for _, u := range []string{
		"",
		"https://example.com",
		"http://github.com/davidarcher/rimgovernor/blob/main/docs/players/README.md",
		playerGuideURL + "?x=1",
		"file:///C:/Windows/System32/calc.exe",
		"javascript:alert(1)",
		"calc.exe",
		"http://localhost:3000/",
		"http://localhost:3000/explore",
		"http://127.0.0.1:3000",
		"https://localhost:3000",
		"http://localhost:3001",
		"http://localhost:3000.evil.com",
	} {
		if err := allowedLink(u); err == nil {
			t.Errorf("%q allowed", u)
		}
	}
}
