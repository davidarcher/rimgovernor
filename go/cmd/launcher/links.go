package main

import "fmt"

// playerGuideURL is an address the launcher page may open in the
// browser. The bind is an allowlist, not a general open-URL: page
// content must not be able to launch arbitrary URLs.
const playerGuideURL = "https://github.com/davidarcher/rimgovernor/blob/main/docs/players/README.md"

// grafanaURL is the local observability stack's Grafana (tools/observability/compose.yml).
const grafanaURL = "http://localhost:3000"

// allowedLink returns nil only for an allowlisted URL, compared exactly.
func allowedLink(url string) error {
	if url == playerGuideURL || url == grafanaURL {
		return nil
	}
	return fmt.Errorf("link not allowed: %q", url)
}
