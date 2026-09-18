//go:build !windows

package childproc

import "os/exec"

// HideConsole is a no-op off Windows: there is no console window to hide.
func HideConsole(*exec.Cmd) {}
