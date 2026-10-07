package main

import "testing"

func TestAllowedLink(t *testing.T) {
	if err := allowedLink(playerGuideURL); err != nil {
		t.Fatalf("player guide refused: %v", err)
	}
	for _, u := range []string{
		"",
		"https://example.com",
		"http://github.com/davidarcher/rimgovernor/blob/main/docs/players/README.md",
		playerGuideURL + "?x=1",
		"file:///C:/Windows/System32/calc.exe",
		"javascript:alert(1)",
		"calc.exe",
	} {
		if err := allowedLink(u); err == nil {
			t.Errorf("%q allowed", u)
		}
	}
}
