package childproc

import (
	"os/exec"

	"golang.org/x/sys/windows"
)

// HideConsole starts cmd with CREATE_NO_WINDOW so a console child never
// opens a terminal window of its own. On Windows 11 the default terminal is
// Windows Terminal, and a console process created from a parent without an
// inherited console (a harness launched with Start-Process, a GUI host)
// otherwise pops a visible window on the desktop for every serve, GABS and
// suite worker it spawns.
func HideConsole(cmd *exec.Cmd) {
	if cmd.SysProcAttr == nil {
		cmd.SysProcAttr = &windows.SysProcAttr{}
	}
	cmd.SysProcAttr.CreationFlags |= windows.CREATE_NO_WINDOW
}
