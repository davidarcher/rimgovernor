package telemetry_test

import (
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

func TestKindedRecordBecomesARowStampedWithTheTick(t *testing.T) {
	rows := telemetrytest.Install(t)
	logger := telemetry.New()
	telemetry.ObserveTick(4200)
	logger.Info("step done", telemetry.KindKey, "scheduler_step", telemetry.ComponentKey, "clock-worker", "err", errors.New("Fields: deadline"), "held", 250*time.Millisecond, "repeats", 3, "as_of", map[string]int64{"colony": 4200})
	logger.Info("no kind, no row", telemetry.ComponentKey, "clock-scheduler")

	got := rows.All()
	if len(got) != 1 {
		t.Fatalf("only the kinded record becomes a row, got %d", len(got))
	}
	row := got[0]
	if row.Kind != "scheduler_step" || row.Context["tick"] != int64(4200) || row.Context["level"] != "INFO" || row.Context[telemetry.ComponentKey] != "clock-worker" {
		t.Fatalf("row context: kind=%s %+v", row.Kind, row.Context)
	}
	if row.Payload["msg"] != "step done" || row.Payload["err"] != "Fields: deadline" || row.Payload["held"] != 250.0 || row.Payload["repeats"] != int64(3) {
		t.Fatalf("row payload: %+v", row.Payload)
	}
	// A tick map rides into the row as an object, not its %+v rendering.
	if asOf, ok := row.Payload["as_of"].(map[string]any); !ok || asOf["colony"] != int64(4200) {
		t.Fatalf("row as_of: %#v", row.Payload["as_of"])
	}
}

func TestUnknownTickIsOmittedFromTheRow(t *testing.T) {
	rows := telemetrytest.Install(t)
	telemetry.ObserveTick(-1)
	telemetry.New().Info("early", telemetry.KindKey, "clock_step")
	got := rows.All()
	if len(got) != 1 {
		t.Fatalf("rows: %+v", got)
	}
	if _, has := got[0].Context["tick"]; has {
		t.Fatalf("no tick observed yet: %+v", got[0].Context)
	}
}

func TestDebugIsGatedAndWithAttrsReachTheRow(t *testing.T) {
	rows := telemetrytest.Install(t)
	logger := telemetry.New().With(telemetry.ComponentKey, "worker")
	logger.Debug("hidden", telemetry.KindKey, "worker_outcome")
	logger.Info("ran", telemetry.KindKey, "worker_outcome", "action", "a1")
	got := rows.All()
	if len(got) != 1 || got[0].Context[telemetry.ComponentKey] != "worker" || got[0].Payload["action"] != "a1" {
		t.Fatalf("there is no Debug level; With attrs reach the row: %+v", got)
	}
}
