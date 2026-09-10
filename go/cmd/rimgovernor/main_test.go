package main

import (
	"bytes"
	"strings"
	"testing"
)

func TestUnavailableRuntimeCannotBeStarted(t *testing.T) {
	for _, args := range [][]string{{"start"}, {"automate"}, {"--bridge", "local"}, {"version", "start"}} {
		var out, errors bytes.Buffer
		if got := run(args, &out, &errors); got != 2 || out.Len() != 0 || errors.Len() == 0 {
			t.Fatalf("%q: exit=%d stdout=%q stderr=%q", args, got, &out, &errors)
		}
	}
}

func TestVersionReportsMigrationGate(t *testing.T) {
	var out, errors bytes.Buffer
	if run([]string{"version"}, &out, &errors) != 0 || !strings.Contains(out.String(), "native runtime unavailable") {
		t.Fatalf("stdout=%q stderr=%q", &out, &errors)
	}
}
