package doctor

import (
	"errors"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/setup"
)

func TestWorktreeOf(t *testing.T) {
	main := filepath.FromSlash("C:/Users/x/code/rimgovernor")
	wt := filepath.Join(main, ".claude", "worktrees", "github-issue-170-abc")
	for _, tc := range []struct {
		name string
		p    setup.Process
		want string
	}{
		{"exe path", setup.Process{Path: filepath.Join(wt, ".rimgovernor", "native-rimworld", "RimWorldWin64.exe")}, wt},
		{"quoted command line only", setup.Process{CommandLine: `"` + filepath.Join(wt, ".rimgovernor", "native-rimworld", "RimWorldWin64.exe") + `" -savedatafolder=` + filepath.Join(wt, ".rimgovernor", "bridge", "profile")}, wt},
		{"forward slashes", setup.Process{Path: "C:/Users/x/code/rimgovernor/.claude/worktrees/github-issue-170-abc/.rimgovernor/bin/rimgovernor.exe"}, wt},
		{"main checkout", setup.Process{Path: filepath.Join(main, ".rimgovernor", "native-rimworld", "RimWorldWin64.exe")}, ""},
		{"steam install", setup.Process{Path: `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64.exe`}, ""},
	} {
		if got := worktreeOf(tc.p); got != tc.want {
			t.Errorf("%s: worktreeOf = %q, want %q", tc.name, got, tc.want)
		}
	}
}

func TestOrphansAgainstKnownWorktrees(t *testing.T) {
	main := filepath.FromSlash("C:/Users/x/code/rimgovernor")
	other := filepath.FromSlash("D:/other/repo")
	game := func(root, name string) setup.Process {
		return setup.Process{PID: len(name), Name: "RimWorldWin64.exe", Path: filepath.Join(root, ".claude", "worktrees", name, ".rimgovernor", "native-rimworld", "RimWorldWin64.exe")}
	}
	procs := []setup.Process{
		game(main, "live"),
		game(main, "gone"),
		{PID: 99, Name: "gabs.exe", Path: filepath.Join(main, ".claude", "worktrees", "gone", ".rimgovernor", "bridge", "gabs", "gabs.exe")},
		game(other, "unknowable"),
		{PID: 7, Name: "RimWorldWin64.exe", Path: filepath.Join(main, ".rimgovernor", "native-rimworld", "RimWorldWin64.exe")},
	}
	calls := 0
	known := func(m string) (map[string]bool, error) {
		calls++
		if m == other {
			return nil, errors.New("not a git repository")
		}
		if m != main {
			t.Fatalf("known asked about %q", m)
		}
		return map[string]bool{
			worktreeKey(main): true,
			worktreeKey(filepath.Join(main, ".claude", "worktrees", "LIVE")):               true,
			worktreeKey(filepath.Join(main, ".claude", "worktrees", "somewhere-else-too")): true,
		}, nil
	}
	got := Orphans(procs, known)
	if len(got) != 2 {
		t.Fatalf("orphans = %+v, want the two under gone", got)
	}
	for _, o := range got {
		if filepath.Base(o.Worktree) != "gone" {
			t.Errorf("orphan %d from %s, want gone", o.PID, o.Worktree)
		}
	}
	if got[0].PID != len("gone") || got[1].PID != 99 {
		t.Errorf("orphans ordered %d, %d; want pid order within a worktree", got[0].PID, got[1].PID)
	}
	if calls != 2 {
		t.Errorf("known called %d times, want once per main checkout", calls)
	}
}

func TestStuckBoot(t *testing.T) {
	now := time.Date(2026, 9, 19, 6, 46, 0, 0, time.UTC)
	twelveHours := now.Add(-12 * time.Hour)
	for _, tc := range []struct {
		name string
		p    setup.Process
		want bool
	}{
		{"never loaded, pegged overnight", setup.Process{WorkingSet: 100 << 20, CPU: 12 * time.Hour, Started: twelveHours}, true},
		{"loaded colony", setup.Process{WorkingSet: 900 << 20, CPU: 12 * time.Hour, Started: twelveHours}, false},
		{"small but idle", setup.Process{WorkingSet: 100 << 20, CPU: 30 * time.Minute, Started: twelveHours}, false},
		{"still booting", setup.Process{WorkingSet: 100 << 20, CPU: 2 * time.Minute, Started: now.Add(-2 * time.Minute)}, false},
		{"no start time", setup.Process{WorkingSet: 100 << 20, CPU: 12 * time.Hour}, false},
	} {
		if got := StuckBoot(tc.p, now); got != tc.want {
			t.Errorf("%s: StuckBoot = %v, want %v", tc.name, got, tc.want)
		}
	}
}
