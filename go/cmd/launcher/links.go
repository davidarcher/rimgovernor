package main

import "fmt"

// playerGuideURL is the one address the launcher page may open in the
// browser. The bind is an allowlist, not a general open-URL: page
// content must not be able to launch arbitrary URLs.
const playerGuideURL = "https://github.com/davidarcher/rimgovernor/blob/main/docs/players/README.md"

// allowedLink returns nil only for an allowlisted URL, compared exactly.
func allowedLink(url string) error {
	if url == playerGuideURL {
		return nil
	}
	return fmt.Errorf("link not allowed: %q", url)
}
