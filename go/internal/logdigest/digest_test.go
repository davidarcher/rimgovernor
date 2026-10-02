package logdigest

import (
	"strings"
	"testing"
)

func TestDigestCollapsesRepeatsAndKeepsEvents(t *testing.T) {
	d := New()
	for _, l := range []string{
		"2026-10-02T19:23:29.048Z tick=464 WARN [clock-worker] step failed: held planner_failures=[a] took 12ms",
		"2026-10-02T19:23:30.000Z tick=500 INFO [layout] map survey read cells=33322",
		"2026-10-02T19:23:31.048Z tick=672 WARN [clock-worker] step failed: held planner_failures=[a] took 15ms",
		"2026-10-02T19:23:32.000Z tick=700 ERROR [worker] boom action=4f7eda10-011f-4c5e-bfb2-aeceb95c8115-0",
		"   at Foo.Bar()",
		"   at Foo.Baz()",
		"2026-10-02T19:23:33.000Z tick=710 INFO [worker] worker outcome action=1 stage_after=completed",
		"2026-10-02T19:23:34.000Z tick=720 INFO [worker] worker outcome action=2 stage_after=failed",
		"2026-10-02T19:23:35.000Z tick=730 INFO [clock-scheduler] window not admitted refused=[no_work]",
		"2026-10-02T19:23:36.000Z tick=740 INFO [defense] raid arrived points=300",
	} {
		d.Feed(l)
	}
	rows := d.Rows()
	if len(rows) != 4 {
		t.Fatalf("%d rows: %+v", len(rows), rows)
	}
	if rows[0].Level != "INFO" || !strings.Contains(rows[0].Message, "raid") {
		t.Fatalf("newest first: %+v", rows[0])
	}
	var warn, boom *Row
	for i := range rows {
		switch rows[i].Level {
		case "WARN":
			warn = &rows[i]
		case "ERROR":
			boom = &rows[i]
		}
	}
	if warn == nil || warn.Count != 2 || warn.FirstTick != 464 || warn.LastTick != 672 || !warn.Problem {
		t.Fatalf("warn: %+v", warn)
	}
	if boom == nil || !strings.Contains(boom.Detail, "Foo.Baz") || !strings.Contains(boom.Text(), "Foo.Bar") {
		t.Fatalf("trace kept with its error: %+v", boom)
	}
}
