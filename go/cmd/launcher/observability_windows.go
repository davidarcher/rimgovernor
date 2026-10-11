//go:build windows

package main

import (
	"context"
	"os/exec"
	"syscall"
)

// execRunner runs real commands without a console window.
type execRunner struct{}

func (execRunner) Run(ctx context.Context, name string, args ...string) ([]byte, error) {
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x08000000}
	return cmd.CombinedOutput()
}

func (execRunner) LookPath(name string) (string, error) { return exec.LookPath(name) }
