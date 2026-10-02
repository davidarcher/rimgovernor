package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestLogTailReadsAppendedLinesOnly(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "controller-20261002-100000.err.log")
	write := func(s string, appendTo bool) {
		flag := os.O_CREATE | os.O_WRONLY | os.O_TRUNC
		if appendTo {
			flag = os.O_CREATE | os.O_WRONLY | os.O_APPEND
		}
		f, err := os.OpenFile(path, flag, 0o644)
		if err != nil {
			t.Fatal(err)
		}
		f.WriteString(s)
		f.Close()
	}
	tail := &logTail{dir: dir}
	if len(tail.rows()) != 0 {
		t.Fatal("rows without a log")
	}
	line := "2026-10-02T19:23:29.048Z tick=464 WARN [a] boom 1\n"
	write(line+"2026-10-02T19:23:30.048Z tick=465 WARN [a] bo", false)
	if rows := tail.rows(); len(rows) != 1 || rows[0].Count != 1 {
		t.Fatalf("%+v", rows)
	}
	write("om 2\n", true) // finishes the half line: the same message again
	rows := tail.rows()
	if len(rows) != 1 || rows[0].Count != 2 || rows[0].Text == "" {
		t.Fatalf("%+v", rows)
	}
}
