package telemetry

import (
	"errors"
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

func TestKindedRecordBecomesARowStampedWithTheTick(t *testing.T) {
	recorder := &fakeRecorder{}
	logger := New(recorder)
	ObserveTick(4200)
	logger.Info("step done", KindKey, "scheduler_step", ComponentKey, "clock-worker", "err", errors.New("Fields: deadline"), "held", 250*time.Millisecond, "repeats", 3, "as_of", map[string]int64{"colony": 4200})
	logger.Info("no kind, no row", ComponentKey, "clock-scheduler")

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

func TestUnknownTickIsOmittedFromTheRow(t *testing.T) {
	recorder := &fakeRecorder{}
	ObserveTick(-1)
	New(recorder).Info("early", KindKey, "clock_step")
	if _, has := recorder.rows[0].context["tick"]; has {
		t.Fatalf("no tick observed yet: %+v", recorder.rows[0].context)
	}
}

func TestDebugIsGatedAndWithAttrsReachTheRow(t *testing.T) {
	recorder := &fakeRecorder{}
	logger := New(recorder).With(ComponentKey, "worker")
	logger.Debug("hidden", KindKey, "worker_outcome")
	logger.Info("ran", KindKey, "worker_outcome", "action", "a1")
	if len(recorder.rows) != 1 || recorder.rows[0].context[ComponentKey] != "worker" || recorder.rows[0].payload["action"] != "a1" {
		t.Fatalf("there is no Debug level; With attrs reach the row: %+v", recorder.rows)
	}
}
