package telemetry

import (
	"context"
	"log/slog"
	"strings"
	"testing"
)

func TestTraceNestingAndContext(t *testing.T) {
	root := NewTrace()
	if len(root.TraceID) != 16 || root.SpanID != root.TraceID || root.ParentID != "" || root.Empty() {
		t.Fatalf("root: %+v", root)
	}
	child := root.Child()
	if child.TraceID != root.TraceID || child.SpanID == root.SpanID || child.ParentID != root.SpanID {
		t.Fatalf("child: %+v under %+v", child, root)
	}
	if orphan := (Trace{}).Child(); orphan.Empty() || orphan.ParentID != "" {
		t.Fatalf("the child of an empty trace is a new root: %+v", orphan)
	}
	if root.Wire() != root.TraceID+"/"+root.SpanID || (Trace{}).Wire() != "" {
		t.Fatalf("wire form: %q", root.Wire())
	}
	ctx := WithTrace(context.Background(), child)
	if TraceFrom(ctx) != child || !TraceFrom(context.Background()).Empty() || !TraceFrom(nil).Empty() {
		t.Fatal("trace does not round-trip through ctx")
	}
	if WithTrace(context.Background(), Trace{}) != context.Background() {
		t.Fatal("an empty trace changed the ctx")
	}
	kept, same := EnsureTrace(ctx)
	if kept != ctx || same != child {
		t.Fatal("EnsureTrace replaced a present trace")
	}
	fresh, minted := EnsureTrace(context.Background())
	if minted.Empty() || TraceFrom(fresh) != minted {
		t.Fatal("EnsureTrace did not mint a root")
	}
	stamped := map[string]any{}
	child.Stamp(stamped)
	if stamped[TraceIDKey] != child.TraceID || stamped[SpanIDKey] != child.SpanID || stamped[ParentIDKey] != child.ParentID {
		t.Fatalf("stamp: %+v", stamped)
	}
	rootStamp := map[string]any{}
	root.Stamp(rootStamp)
	if _, present := rootStamp[ParentIDKey]; present {
		t.Fatal("a root stamped a parent")
	}
	(Trace{}).Stamp(stamped)
	if stamped[TraceIDKey] != child.TraceID {
		t.Fatal("an empty trace overwrote a stamp")
	}
}

// A kinded record logged under a traced ctx carries the trace in its row
// context and names it on the stderr line; an untraced record and a
// non-kinded record under a trace do neither.
func TestHandlerStampsTraceFromContext(t *testing.T) {
	var out strings.Builder
	recorder := &fakeRecorder{}
	logger := New(&out, slog.LevelInfo, recorder)
	trace := NewTrace().Child()
	ctx := WithTrace(context.Background(), trace)
	logger.InfoContext(ctx, "step done", KindKey, "scheduler_step", ComponentKey, "clock-worker", "admitted", true)
	logger.Info("step done", KindKey, "scheduler_step", ComponentKey, "clock-worker")
	logger.InfoContext(ctx, "not a row", ComponentKey, "clock-scheduler")
	lines := strings.Split(strings.TrimRight(out.String(), "\n"), "\n")
	if len(lines) != 3 || !strings.HasSuffix(lines[0], " admitted=true trace="+trace.TraceID) || strings.Contains(lines[1], "trace=") || strings.Contains(lines[2], "trace=") {
		t.Fatalf("lines:\n%s", out.String())
	}
	if len(recorder.rows) != 2 {
		t.Fatalf("rows: %+v", recorder.rows)
	}
	traced := recorder.rows[0].context
	if traced[TraceIDKey] != trace.TraceID || traced[SpanIDKey] != trace.SpanID || traced[ParentIDKey] != trace.ParentID || traced[ComponentKey] != "clock-worker" {
		t.Fatalf("traced row context: %+v", traced)
	}
	if _, present := recorder.rows[1].context[TraceIDKey]; present {
		t.Fatalf("untraced row stamped: %+v", recorder.rows[1].context)
	}
}
