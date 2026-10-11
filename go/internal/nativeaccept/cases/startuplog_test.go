package cases

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

// startupRow is one mod_log row as the bridge emits it.
func startupRow(level, source, msg string) telemetrytest.Row {
	return telemetrytest.Row{Kind: "mod_log", Context: map[string]any{"component": "bridge"},
		Payload: map[string]any{"level": level, "source": source, "msg": msg}}
}

func TestCheckStartupRows(t *testing.T) {
	active := startupRow("info", "startup", "headless mode active")
	cases := []struct {
		name     string
		rows     []telemetrytest.Row
		headless bool
		want     string // substring of the error; empty means pass
	}{
		{"headless with the active row", []telemetrytest.Row{active}, true, ""},
		{"windowed without the row", []telemetrytest.Row{startupRow("info", "other", "headless mode active")}, false, ""},
		{"bootstrap error", []telemetrytest.Row{startupRow("error", "startup", "bootstrap failed: boom"), active}, true, "bootstrap failed"},
		{"post-init error", []telemetrytest.Row{active, startupRow("error", "startup", "post-init failed: boom")}, true, "post-init failed"},
		{"headless requested, row trimmed away", []telemetrytest.Row{startupRow("info", "other", "x")}, true, ""},
		{"windowed run with the row", []telemetrytest.Row{active}, false, "disagrees"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			dir := t.TempDir()
			telemetrytest.WriteLogs(t, filepath.Dir(na.FlightRecorderPath(dir)), c.rows...)
			err := checkStartupRows(dir, c.headless)
			switch {
			case c.want == "" && err != nil:
				t.Fatalf("unexpected error: %v", err)
			case c.want != "" && (err == nil || !strings.Contains(err.Error(), c.want)):
				t.Fatalf("error = %v, want one containing %q", err, c.want)
			}
		})
	}
}

func TestCheckStartupRowsReadsRotatedSegments(t *testing.T) {
	dir := t.TempDir()
	base := na.FlightRecorderPath(dir)
	if err := os.MkdirAll(filepath.Dir(base), 0o755); err != nil {
		t.Fatal(err)
	}
	write := func(path string, rows ...telemetrytest.Row) {
		lines := telemetrytest.LogLines(t, rows...)
		if err := os.WriteFile(path, []byte(strings.Join(lines, "\n")+"\n"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	// The startup rows rotated out of the live file into .2.
	write(base, startupRow("info", "other", "later"))
	write(base+".1", startupRow("info", "other", "mid"))
	write(base+".2", startupRow("info", "startup", "headless mode active"))
	if err := checkStartupRows(dir, true); err != nil {
		t.Fatalf("headless with a rotated active row: %v", err)
	}
	if err := checkStartupRows(dir, false); err == nil || !strings.Contains(err.Error(), "disagrees") {
		t.Fatalf("windowed with a rotated active row: error = %v, want disagrees", err)
	}
	write(base+".2", startupRow("error", "startup", "post-init failed: boom"))
	if err := checkStartupRows(dir, true); err == nil || !strings.Contains(err.Error(), "post-init failed") {
		t.Fatalf("rotated error row: error = %v", err)
	}
}

func TestCheckStartupRowsSkipsWithoutLogRing(t *testing.T) {
	for _, headless := range []bool{true, false} {
		if err := checkStartupRows(t.TempDir(), headless); err != nil {
			t.Fatalf("headless=%t: no log ring must skip, got %v", headless, err)
		}
	}
}
