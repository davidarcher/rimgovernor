package gamehost

import (
	"errors"
	"os/exec"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/childproc"
	"golang.org/x/sys/windows"
)

const stillActive = 259

var errNoProcess = errors.New("gamehost: no such process")

// processStartTime is the creation FILETIME (100 ns since 1601) of a
// running pid; an exited or missing pid is errNoProcess.
func processStartTime(pid int) (int64, error) {
	h, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, uint32(pid))
	if err != nil {
		return 0, errNoProcess
	}
	defer windows.CloseHandle(h)
	var code uint32
	if err := windows.GetExitCodeProcess(h, &code); err != nil {
		return 0, err
	}
	if code != stillActive {
		return 0, errNoProcess
	}
	var creation, exit, kernel, user windows.Filetime
	if err := windows.GetProcessTimes(h, &creation, &exit, &kernel, &user); err != nil {
		return 0, err
	}
	return int64(creation.HighDateTime)<<32 | int64(creation.LowDateTime), nil
}

// startDetached starts cmd with no console window, in its own process group
// and broken away from the caller's job, so closing the controller (or a
// kill-on-close job around it) never takes the game down. Breakaway is
// refused when the enclosing job forbids it; the start is then retried
// without it.
func startDetached(cmd *exec.Cmd) (*exec.Cmd, error) {
	childproc.HideConsole(cmd)
	cmd.SysProcAttr.CreationFlags |= windows.CREATE_NEW_PROCESS_GROUP | windows.CREATE_BREAKAWAY_FROM_JOB
	if err := cmd.Start(); err == nil {
		return cmd, nil
	}
	retry := exec.Command(cmd.Path, cmd.Args[1:]...)
	retry.Dir, retry.Env, retry.Stdout, retry.Stderr = cmd.Dir, cmd.Env, cmd.Stdout, cmd.Stderr
	childproc.HideConsole(retry)
	retry.SysProcAttr.CreationFlags |= windows.CREATE_NEW_PROCESS_GROUP
	return retry, retry.Start()
}

// killTree ends pid and its descendants.
func killTree(pid int) error {
	kill := exec.Command("taskkill", "/T", "/F", "/PID", strconv.Itoa(pid))
	childproc.HideConsole(kill)
	if err := kill.Run(); err != nil {
		h, oerr := windows.OpenProcess(windows.PROCESS_TERMINATE, false, uint32(pid))
		if oerr != nil {
			return err
		}
		defer windows.CloseHandle(h)
		return windows.TerminateProcess(h, 1)
	}
	return nil
}
