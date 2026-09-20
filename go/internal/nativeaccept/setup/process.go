package setup

import (
	"context"
	"fmt"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

// Process is one row of the harness's process listing (Win32_Process):
// enough to tell whose game it is (Path, CommandLine) and whether it is
// doing anything (WorkingSet, CPU against Started).
type Process struct {
	PID  int
	Name string
	// Path is the executable path; CommandLine the full command line
	// (the game's -savedatafolder names its profile). Either is "" when
	// Windows withholds it.
	Path        string
	CommandLine string
	// WorkingSet is the working set in bytes; CPU the kernel plus user
	// time consumed so far; Started the creation time (zero when
	// unreadable).
	WorkingSet uint64
	CPU        time.Duration
	Started    time.Time
}

// HarnessImages are the images a root launches: the game, the GABS
// bridge and the serve binary.
var HarnessImages = []string{"RimWorldWin64.exe", "gabs.exe", "rimgovernor.exe"}

// ListProcesses lists the running processes of the named images (every
// one on the machine, peers' included; callers narrow by path).
func ListProcesses(names ...string) ([]Process, error) {
	var filters []string
	for _, n := range names {
		filters = append(filters, fmt.Sprintf("Name='%s'", n))
	}
	script := "Get-CimInstance Win32_Process -Filter \"" + strings.Join(filters, " OR ") + "\" | ForEach-Object { " +
		"$t = 0; if ($_.KernelModeTime) { $t += $_.KernelModeTime }; if ($_.UserModeTime) { $t += $_.UserModeTime }; " +
		"$s = ''; if ($_.CreationDate) { $s = $_.CreationDate.ToUniversalTime().ToString('o') }; " +
		"\"$($_.ProcessId)`t$($_.Name)`t$($_.WorkingSetSize)`t$t`t$s`t$($_.ExecutablePath)`t$($_.CommandLine)\" }"
	out, err := exec.Command("powershell", "-NoProfile", "-NonInteractive", "-Command", script).Output()
	if err != nil {
		return nil, fmt.Errorf("list processes %v: %w", names, err)
	}
	return parseProcesses(string(out)), nil
}

// parseProcesses reads ListProcesses' tab-separated rows: pid, name,
// working set, CPU in 100 ns units, ISO start, executable path, command
// line (the last field keeps any tab of its own).
func parseProcesses(out string) []Process {
	var procs []Process
	for _, line := range strings.Split(out, "\n") {
		fields := strings.SplitN(strings.TrimRight(line, "\r"), "\t", 7)
		if len(fields) < 7 {
			continue
		}
		pid, err := strconv.Atoi(strings.TrimSpace(fields[0]))
		if err != nil {
			continue
		}
		p := Process{PID: pid, Name: fields[1], Path: strings.TrimSpace(fields[5]), CommandLine: strings.TrimSpace(fields[6])}
		p.WorkingSet, _ = strconv.ParseUint(fields[2], 10, 64)
		if ticks, err := strconv.ParseInt(fields[3], 10, 64); err == nil {
			p.CPU = time.Duration(ticks) * 100 * time.Nanosecond
		}
		if fields[4] != "" {
			p.Started, _ = time.Parse(time.RFC3339Nano, fields[4])
		}
		procs = append(procs, p)
	}
	return procs
}

// Under reports whether the process's executable or command line names a
// path under dir (case-insensitive, as Windows paths are). Both sides are
// compared with forward slashes: the listing carries Windows backslashes,
// and filepath.Clean only normalises to them on Windows, so a `/` dir
// never matched on the Linux CI runner that runs this package's tests.
func (p Process) Under(dir string) bool {
	prefix := strings.ToLower(slashed(filepath.Clean(dir)))
	return strings.Contains(strings.ToLower(slashed(p.Path)), prefix) || strings.Contains(strings.ToLower(slashed(p.CommandLine)), prefix)
}

func slashed(path string) string { return strings.ReplaceAll(path, "\\", "/") }

// RunningGames lists the PIDs of RimWorldWin64.exe processes whose
// executable lives under gameCopy: this worktree's own games, never a
// peer's (AGENTS.md: stop your own by root or pid).
func RunningGames(gameCopy string) ([]int, error) {
	procs, err := RunningGameProcesses(gameCopy)
	if err != nil {
		return nil, err
	}
	var pids []int
	for _, p := range procs {
		pids = append(pids, p.PID)
	}
	return pids, nil
}

// RunningGameProcesses is RunningGames with each process's full row.
func RunningGameProcesses(gameCopy string) ([]Process, error) {
	all, err := ListProcesses("RimWorldWin64.exe")
	if err != nil {
		return nil, fmt.Errorf("list RimWorld processes: %w", err)
	}
	var procs []Process
	for _, p := range all {
		if p.Under(gameCopy) {
			procs = append(procs, p)
		}
	}
	return procs, nil
}

// StopPIDs force-stops the given processes by pid (Stop-Process -Id,
// never by image name: peers' games run beside yours). Nothing to stop
// is not an error.
func StopPIDs(ctx context.Context, pids []int) error {
	if len(pids) == 0 {
		return nil
	}
	var ids []string
	for _, pid := range pids {
		ids = append(ids, strconv.Itoa(pid))
	}
	script := "Stop-Process -Force -ErrorAction SilentlyContinue -Id " + strings.Join(ids, ",")
	if out, err := exec.CommandContext(ctx, "powershell", "-NoProfile", "-NonInteractive", "-Command", script).CombinedOutput(); err != nil {
		return fmt.Errorf("stop pids %v: %s %w", pids, strings.TrimSpace(string(out)), err)
	}
	return nil
}

// StopGames stops every RimWorldWin64.exe running from gameCopy by pid
// (RunningGames: this worktree's own games, never a peer's, since each
// worktree launches its private copy) and waits until none is left, so a
// rebuilt mod can be installed under nobody (#276). It returns the pids it
// stopped; none running is not an error.
func StopGames(ctx context.Context, gameCopy string) ([]int, error) {
	pids, err := RunningGames(gameCopy)
	if err != nil || len(pids) == 0 {
		return nil, err
	}
	if err := StopPIDs(ctx, pids); err != nil {
		return nil, err
	}
	deadline := time.Now().Add(30 * time.Second)
	for {
		left, err := RunningGames(gameCopy)
		if err != nil {
			return pids, err
		}
		if len(left) == 0 {
			return pids, nil
		}
		if time.Now().After(deadline) {
			return pids, fmt.Errorf("pids %v still run from %s after the stop", left, gameCopy)
		}
		select {
		case <-ctx.Done():
			return pids, ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
}
