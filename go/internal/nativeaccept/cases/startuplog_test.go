package cases

import (
	"fmt"
	"os"
	"strings"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// startupRow is one mod_log flight line as the recorder writes it.
func startupRow(seq int, level, source, msg string) string {
	return fmt.Sprintf(`{"sequence":%d,"kind":"mod_log","context":{},"payload":{"seq":%d,"level":%q,"source":%q,"msg":%q}}`, seq, seq, level, source, msg)
}

func TestCheckStartupRows(t *testing.T) {
	active := startupRow(1, "info", "startup", "headless mode active")
	cases := []struct {
		name     string
		rows     []string
		headless bool
		want     string // substring of the error; empty means pass
	}{
		{"headless with the active row", []string{active}, true, ""},
		{"windowed without the row", []string{startupRow(1, "info", "other", "headless mode active")}, false, ""},
		{"bootstrap error", []string{startupRow(1, "error", "startup", "bootstrap failed: boom"), active}, true, "bootstrap failed"},
		{"post-init error", []string{active, startupRow(2, "error", "startup", "post-init failed: boom")}, true, "post-init failed"},
		{"headless requested, row trimmed away", []string{startupRow(1, "info", "other", "x")}, true, ""},
		{"windowed run with the row", []string{active}, false, "disagrees"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			dir := t.TempDir()
			if err := os.WriteFile(na.FlightRecorderPath(dir), []byte(strings.Join(c.rows, "\n")+"\n"), 0o644); err != nil {
				t.Fatal(err)
			}
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
	write := func(path string, rows ...string) {
		if err := os.WriteFile(path, []byte(strings.Join(rows, "\n")+"\n"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	// The startup rows rotated out of the live file into .2.
	write(base, startupRow(9, "info", "other", "later"))
	write(base+".1", startupRow(5, "info", "other", "mid"))
	write(base+".2", startupRow(1, "info", "startup", "headless mode active"))
	if err := checkStartupRows(dir, true); err != nil {
		t.Fatalf("headless with a rotated active row: %v", err)
	}
	if err := checkStartupRows(dir, false); err == nil || !strings.Contains(err.Error(), "disagrees") {
		t.Fatalf("windowed with a rotated active row: error = %v, want disagrees", err)
	}
	write(base+".2", startupRow(1, "error", "startup", "post-init failed: boom"))
	if err := checkStartupRows(dir, true); err == nil || !strings.Contains(err.Error(), "post-init failed") {
		t.Fatalf("rotated error row: error = %v", err)
	}
}

func TestCheckStartupRowsSkipsWithoutFlightRecorder(t *testing.T) {
	for _, headless := range []bool{true, false} {
		if err := checkStartupRows(t.TempDir(), headless); err != nil {
			t.Fatalf("headless=%t: no flight.jsonl must skip, got %v", headless, err)
		}
	}
}
