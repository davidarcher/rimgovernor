package doctor

import (
	"context"
	"fmt"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/setup"
)

// Orphan is a harness process (setup.HarnessImages) launched under a
// `.claude/worktrees/<name>/` that git no longer lists (#346): the
// worktree went with its root, so `acceptance stop -root` cannot reach
// it, and GABS keeps the game detached by design.
type Orphan struct {
	setup.Process
	// Worktree is the vanished checkout the process ran under.
	Worktree string
}

// worktreesDir is the segment every agent worktree lives under.
var worktreesDir = filepath.FromSlash("/.claude/worktrees/")

// worktreeOf is the `<main>/.claude/worktrees/<name>` enclosing any path
// the process names, "" when none does.
func worktreeOf(p setup.Process) string {
	for _, s := range []string{p.Path, p.CommandLine} {
		s = filepath.FromSlash(s)
		lower := strings.ToLower(s)
		i := strings.Index(lower, strings.ToLower(worktreesDir))
		if i < 0 {
			continue
		}
		// The command line may quote or prefix the path; the segment
		// before .claude is the main checkout, which ends at the last
		// separator or quote before it.
		start := strings.LastIndexAny(lower[:i], "\"' ") + 1
		rest := s[i+len(worktreesDir):]
		name, _, _ := strings.Cut(rest, string(filepath.Separator))
		name, _, _ = strings.Cut(name, "\"")
		if name == "" {
			continue
		}
		return filepath.Join(s[start:i], ".claude", "worktrees", name)
	}
	return ""
}

// Orphans picks, from procs, those under a worktree that known(main)
// does not list; known returns the worktrees of a main checkout, and an
// error leaves that checkout's processes alone (never stop what cannot
// be classified).
func Orphans(procs []setup.Process, known func(main string) (map[string]bool, error)) []Orphan {
	cache := map[string]map[string]bool{}
	var out []Orphan
	for _, p := range procs {
		wt := worktreeOf(p)
		if wt == "" {
			continue
		}
		main := filepath.Dir(filepath.Dir(filepath.Dir(wt)))
		key := strings.ToLower(main)
		list, ok := cache[key]
		if !ok {
			var err error
			list, err = known(main)
			if err != nil {
				list = nil
			}
			cache[key] = list
		}
		if list == nil || list[worktreeKey(wt)] {
			continue
		}
		out = append(out, Orphan{Process: p, Worktree: wt})
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Worktree != out[j].Worktree {
			return out[i].Worktree < out[j].Worktree
		}
		return out[i].PID < out[j].PID
	})
	return out
}

// worktreeKey is how known worktree paths are compared: cleaned,
// backslashed, lower-cased.
func worktreeKey(path string) string {
	return strings.ToLower(filepath.Clean(filepath.FromSlash(path)))
}

// GitWorktrees is Orphans' known: `git worktree list --porcelain` from
// main, keyed by lower-cased clean path.
func GitWorktrees(ctx context.Context) func(main string) (map[string]bool, error) {
	return func(main string) (map[string]bool, error) {
		gitCtx, cancel := context.WithTimeout(ctx, 20*time.Second)
		defer cancel()
		out, err := exec.CommandContext(gitCtx, "git", "-C", main, "worktree", "list", "--porcelain").Output()
		if err != nil {
			return nil, fmt.Errorf("git -C %s worktree list: %w", main, err)
		}
		known := map[string]bool{}
		for _, line := range strings.Split(string(out), "\n") {
			if path, ok := strings.CutPrefix(strings.TrimSpace(line), "worktree "); ok {
				known[worktreeKey(path)] = true
			}
		}
		return known, nil
	}
}

// FindOrphans lists every harness process orphaned by a removed
// worktree on this machine.
func FindOrphans(ctx context.Context) ([]Orphan, error) {
	procs, err := setup.ListProcesses(setup.HarnessImages...)
	if err != nil {
		return nil, err
	}
	return Orphans(procs, GitWorktrees(ctx)), nil
}

// Stuck-boot bounds: a game that has run longer than StuckBootAge with a
// working set under StuckBootWorkingSet while spending StuckBootCPUShare
// of its wall time on a core never loaded a world (#346: ~100 MB, one
// core pegged for hours). A loaded colony sits at 700 MB and above.
const (
	StuckBootAge        = 5 * time.Minute
	StuckBootWorkingSet = 250 << 20
	StuckBootCPUShare   = 0.8
)

// StuckBoot reports whether p looks like a boot that never finished, as
// of now.
func StuckBoot(p setup.Process, now time.Time) bool {
	if p.Started.IsZero() || p.WorkingSet == 0 || p.WorkingSet >= StuckBootWorkingSet {
		return false
	}
	age := now.Sub(p.Started)
	if age < StuckBootAge {
		return false
	}
	return float64(p.CPU) >= StuckBootCPUShare*float64(age)
}

// orphans is the doctor's sweep: Warn naming each orphan by pid, image
// and vanished worktree, healed by stopping them (HealOrphans).
func orphans(ctx context.Context, o Options) Check {
	c := Check{Name: "orphans"}
	found, err := FindOrphans(ctx)
	if err != nil {
		c.Detail = "could not list harness processes: " + err.Error()
		return c
	}
	if len(found) == 0 {
		c.Detail = "no game, gabs or rimgovernor process from a removed worktree"
		return c
	}
	c.Status, c.Code = Warn, HealOrphans
	now := time.Now()
	var lines []string
	for _, p := range found {
		line := fmt.Sprintf("pid %d %s from %s", p.PID, p.Name, filepath.Base(p.Worktree))
		if StuckBoot(p.Process, now) {
			line += " (stuck boot)"
		}
		lines = append(lines, line)
		c.PIDs = append(c.PIDs, p.PID)
	}
	c.Detail = fmt.Sprintf("%d processes outlive their removed worktrees: %s", len(found), strings.Join(lines, "; "))
	c.Fix = "`acceptance doctor -root " + o.Root + " -heal` stops them by pid (a run's preflight does the same unless -no-heal)"
	return c
}

// stuckNote is the process check's addendum for this root's own games
// that look like a boot that never finished.
func stuckNote(procs []setup.Process, now time.Time) string {
	var pids []int
	for _, p := range procs {
		if StuckBoot(p, now) {
			pids = append(pids, p.PID)
		}
	}
	if len(pids) == 0 {
		return ""
	}
	return fmt.Sprintf("; pids %v sit under %d MB with a core pegged for over %s: a boot that never finished", pids, StuckBootWorkingSet>>20, StuckBootAge)
}
