package setup

import (
	"testing"
	"time"
)

func TestParseProcesses(t *testing.T) {
	out := "66460\tRimWorldWin64.exe\t120365056\t100660937500\t2026-09-19T13:21:37.1234567Z\tC:\\wt\\a\\.rimgovernor\\native-rimworld\\RimWorldWin64.exe\t\"C:\\wt\\a\\.rimgovernor\\native-rimworld\\RimWorldWin64.exe\" -savedatafolder=C:\\wt\\a\\.rimgovernor\\bridge\\profile\r\n" +
		"12\tgabs.exe\t\t0\t\t\t\r\n" +
		"garbage line\r\n"
	procs := parseProcesses(out)
	if len(procs) != 2 {
		t.Fatalf("parsed %d rows, want 2: %+v", len(procs), procs)
	}
	p := procs[0]
	if p.PID != 66460 || p.Name != "RimWorldWin64.exe" || p.WorkingSet != 120365056 {
		t.Errorf("row 0 = %+v", p)
	}
	if want := time.Duration(100660937500) * 100 * time.Nanosecond; p.CPU != want {
		t.Errorf("CPU = %s, want %s", p.CPU, want)
	}
	if p.Started.IsZero() || p.Started.Hour() != 13 {
		t.Errorf("Started = %s", p.Started)
	}
	if !p.Under("C:/wt/a/.rimgovernor/native-rimworld") || p.Under("C:/wt/b") {
		t.Errorf("Under mismatch for %q", p.Path)
	}
	if procs[1].PID != 12 || procs[1].Name != "gabs.exe" || !procs[1].Started.IsZero() {
		t.Errorf("row 1 = %+v", procs[1])
	}
}
