package telemetry

import (
	"errors"
	"log/slog"
	"regexp"
	"strings"
	"testing"
	"time"
)

type fakeRecorder struct {
	rows []struct {
		kind    string
		context map[string]any
		payload map[string]any
	}
}

func (f *fakeRecorder) Event(kind string, context map[string]any, _ bool, payload map[string]any) (uint64, error) {
	f.rows = append(f.rows, struct {
		kind    string
		context map[string]any
		payload map[string]any
	}{kind, context, payload})
	return uint64(len(f.rows)), nil
}

var stamp = regexp.MustCompile(`^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z tick=(\S+) (\w+) `)

func TestEveryLineStartsWithTimeAndTick(t *testing.T) {
	var out strings.Builder
	recorder := &fakeRecorder{}
	logger := New(&out, slog.LevelDebug, recorder)
	ObserveTick(-1)
	logger.Debug("before any status", ComponentKey, "clock-scheduler")
	ObserveTick(4200)
	logger.Info("step done", KindKey, "scheduler_step", ComponentKey, "clock-worker", "err", errors.New("Fields: deadline"), "held", 250*time.Millisecond, "repeats", 3, "as_of", map[string]int64{"colony": 4200})
	logger.Info("Fields select: kind=outdoor crop=Plant_Rice\n outdoor Plant_Rice needed=1 cells=2 score=0.5", ComponentKey, "clock-scheduler")

	lines := strings.Split(strings.TrimRight(out.String(), "\n"), "\n")
	if len(lines) != 4 {
		t.Fatalf("want 4 lines (one multi-line message), got %d:\n%s", len(lines), out.String())
	}
	m := stamp.FindStringSubmatch(lines[0])
	if m == nil || m[1] != "-" || m[2] != "DEBUG" || !strings.HasSuffix(lines[0], "[clock-scheduler] before any status") {
		t.Fatalf("unknown tick renders as -: %q", lines[0])
	}
	m = stamp.FindStringSubmatch(lines[1])
	if m == nil || m[1] != "4200" || m[2] != "INFO" {
		t.Fatalf("stamped line: %q", lines[1])
	}
	if !strings.Contains(lines[1], "[clock-worker] step done err=\"Fields: deadline\" held=250ms repeats=3 as_of=map[colony:4200]") {
		t.Fatalf("kind and component are not attrs on the line; err quoted: %q", lines[1])
	}
	if !strings.HasSuffix(lines[2], "[clock-scheduler] Fields select: kind=outdoor crop=Plant_Rice") || lines[3] != " outdoor Plant_Rice needed=1 cells=2 score=0.5" {
		t.Fatalf("multi-line messages keep their continuation lines verbatim: %q %q", lines[2], lines[3])
	}

	if len(recorder.rows) != 1 {
		t.Fatalf("only the kinded record becomes a row, got %d", len(recorder.rows))
	}
	row := recorder.rows[0]
	if row.kind != "scheduler_step" || row.context["tick"] != int64(4200) || row.context["level"] != "INFO" || row.context[ComponentKey] != "clock-worker" {
		t.Fatalf("row context: kind=%s %+v", row.kind, row.context)
	}
	if row.payload["msg"] != "step done" || row.payload["err"] != "Fields: deadline" || row.payload["held"] != 250.0 || row.payload["repeats"] != int64(3) {
		t.Fatalf("row payload: %+v", row.payload)
	}
	// A tick map rides into the row as an object, not its %+v rendering.
	if asOf, ok := row.payload["as_of"].(map[string]int64); !ok || asOf["colony"] != 4200 {
		t.Fatalf("row as_of: %#v", row.payload["as_of"])
	}
}

func TestLevelGateAndWithAttrs(t *testing.T) {
	var out strings.Builder
	recorder := &fakeRecorder{}
	logger := New(&out, slog.LevelInfo, recorder).With(ComponentKey, "worker")
	logger.Debug("hidden", KindKey, "worker_outcome")
	logger.Info("ran", KindKey, "worker_outcome", "action", "a1")
	if strings.Contains(out.String(), "hidden") || len(recorder.rows) != 1 {
		t.Fatalf("debug records are gated before both sinks: %q rows=%d", out.String(), len(recorder.rows))
	}
	if !strings.Contains(out.String(), " INFO [worker] ran action=a1") || recorder.rows[0].context[ComponentKey] != "worker" {
		t.Fatalf("With attrs reach both sinks: %q %+v", out.String(), recorder.rows[0].context)
	}
}
