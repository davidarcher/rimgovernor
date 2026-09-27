//go:build !windows

package gamehost

import (
	"os/exec"
	"syscall"
)

// startDetached starts cmd in a new session so it leaves the controller's
// process group and terminal and survives the controller's exit.
func startDetached(cmd *exec.Cmd) (*exec.Cmd, error) {
	cmd.SysProcAttr = &syscall.SysProcAttr{Setsid: true}
	return cmd, cmd.Start()
}

// killTree kills the game's session process group (its pgid is its pid
// under Setsid), then the pid itself in case it left the group.
func killTree(pid int) error {
	gerr := syscall.Kill(-pid, syscall.SIGKILL)
	perr := syscall.Kill(pid, syscall.SIGKILL)
	if gerr != nil && perr != nil && perr != syscall.ESRCH {
		return perr
	}
	return nil
}
