package nativeaccept

import (
	"os"
	"path/filepath"
	"testing"
)

func TestGameLogCaptureCopiesTheCaseSliceAndCountsExceptions(t *testing.T) {
	root := t.TempDir()
	source := filepath.Join(root, "HeadlessPlayer.log")
	earlier := "[HeadlessRim] Bootstrap armed.\nNullReferenceException: an earlier case's\n"
	if err := os.WriteFile(source, []byte(earlier), 0644); err != nil {
		t.Fatal(err)
	}
	capture := OpenGameLog(source)
	mine := "Loading save\nInvalidOperationException: mid-case\n  at Verse.Thing\nException in tick\nlast line without newline"
	if err := os.WriteFile(source, []byte(earlier+mine), 0644); err != nil {
		t.Fatal(err)
	}
	output := t.TempDir()
	report := Report{}
	capture.Close(output, report)
	copied, err := os.ReadFile(filepath.Join(output, GameLogName))
	if err != nil {
		t.Fatal(err)
	}
	if string(copied) != mine {
		t.Errorf("game.log = %q, want the slice written after open", copied)
	}
	row, _ := report[GameLogKey].(map[string]any)
	if row["path"] != filepath.Join(output, GameLogName) || row["bytes"] != int64(len(mine)) || row["exceptions"] != 2 {
		t.Errorf("game_log = %v", report[GameLogKey])
	}
}

func TestGameLogCaptureStartsOverAfterARelaunch(t *testing.T) {
	root := t.TempDir()
	source := filepath.Join(root, "HeadlessPlayer.log")
	if err := os.WriteFile(source, []byte("a long earlier log that a relaunch truncates\n"), 0644); err != nil {
		t.Fatal(err)
	}
	capture := OpenGameLog(source)
	if err := os.WriteFile(source, []byte("fresh launch\n"), 0644); err != nil {
		t.Fatal(err)
	}
	output := t.TempDir()
	report := Report{}
	capture.Close(output, report)
	copied, _ := os.ReadFile(filepath.Join(output, GameLogName))
	if string(copied) != "fresh launch\n" {
		t.Errorf("game.log after a relaunch = %q", copied)
	}
	if row, _ := report[GameLogKey].(map[string]any); row["exceptions"] != 0 {
		t.Errorf("game_log = %v", report[GameLogKey])
	}
}

func TestGameLogCaptureWithoutASourceRecordsNothing(t *testing.T) {
	capture := OpenGameLog(filepath.Join(t.TempDir(), "missing.log"))
	output := t.TempDir()
	report := Report{}
	capture.Close(output, report)
	if _, has := report[GameLogKey]; has {
		t.Errorf("game_log recorded for a missing source: %v", report[GameLogKey])
	}
	if _, err := os.Stat(filepath.Join(output, GameLogName)); err == nil {
		t.Error("game.log written for a missing source")
	}
}
