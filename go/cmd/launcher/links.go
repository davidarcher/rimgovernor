package main

import "fmt"

// playerGuideURL is an address the launcher page may open in the
// browser. The bind is an allowlist, not a general open-URL: page
// content must not be able to launch arbitrary URLs.
const playerGuideURL = "https://github.com/davidarcher/rimgovernor/blob/main/docs/players/README.md"

// grafanaURL is the local observability stack's Grafana (tools/observability/compose.yml).
const grafanaURL = "http://localhost:3000"

// DashboardLink is one provisioned dashboard the launcher opens, in button
// order. The uids are the files' in tools/observability/dashboards; a test
// keeps the two in step.
type DashboardLink struct {
	Name string `json:"name"`
	URL  string `json:"url"`
}

var dashboardLinks = []DashboardLink{
	{"Now", grafanaURL + "/d/rg-now"},
	{"Explain", grafanaURL + "/d/rg-concern-transitions"},
	{"Problems", grafanaURL + "/d/rg-problems"},
	{"Log", grafanaURL + "/d/rg-log"},
	{"Ledger", grafanaURL + "/d/rg-ledger"},
}

// allowedLink returns nil only for an allowlisted URL, compared exactly.
func allowedLink(url string) error {
	if url == playerGuideURL || url == grafanaURL {
		return nil
	}
	for _, d := range dashboardLinks {
		if url == d.URL {
			return nil
		}
	}
	return fmt.Errorf("link not allowed: %q", url)
}
