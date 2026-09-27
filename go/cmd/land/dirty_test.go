package main

import (
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"time"
)

func TestParseStatusZ(t *testing.T) {
	got := parseStatusZ(" M go/a.go\x00R  new.cs\x00old.cs\x00D  gone.txt\x00")
	want := []dirtyPath{{Status: " M", Path: "go/a.go"}, {Status: "R ", Path: "new.cs"}, {Status: "D ", Path: "gone.txt"}}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("got %+v, want %+v", got, want)
	}
}

func TestParseWorktrees(t *testing.T) {
	got := parseWorktrees("worktree C:/r\nHEAD 1\nbranch refs/heads/main\n\nworktree C:/r/wt\nHEAD 2\ndetached\n\nworktree C:/w2\nHEAD 3\nbranch refs/heads/issue-9\n")
	want := []worktree{{filepath.Clean("C:/r"), "main"}, {filepath.Clean("C:/r/wt"), ""}, {filepath.Clean("C:/w2"), "issue-9"}}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("got %+v, want %+v", got, want)
	}
}

func TestDirtyMessageNamesPathsAndOwners(t *testing.T) {
	now := time.Date(2026, 9, 27, 10, 0, 0, 0, time.Local)
	msg := dirtyMessage("C:/r", []dirtyPath{
		{Status: " M", Path: "go/clock_poll.go", Modified: now.Add(-90 * time.Second),
			Landed: "66df7885d 2026-09-27 09:46:13 Clock rows", Owners: []string{"C:/rg-clockrows (clock-unparseable-rows), uncommitted there"}},
		{Status: " M", Path: "src/Native.cs", Modified: now.Add(-time.Hour)},
		{Status: " D", Path: "gone.txt"},
	}, now)
	for _, want := range []string{
		"main checkout C:/r has uncommitted changes",
		"   M go/clock_poll.go\n      modified 2026-09-27 09:58:30 (1m30s ago)\n      main last changed it in 66df7885d 2026-09-27 09:46:13 Clock rows\n      same content in C:/rg-clockrows (clock-unparseable-rows), uncommitted there\n",
		"   M src/Native.cs\n      modified 2026-09-27 09:00:00 (1h0m0s ago)\n      no other worktree holds this content\n",
		"   D gone.txt\n      file is gone\n",
		"git checkout -- <path>",
	} {
		if !strings.Contains(msg, want) {
			t.Errorf("message lacks %q:\n%s", want, msg)
		}
	}
}
