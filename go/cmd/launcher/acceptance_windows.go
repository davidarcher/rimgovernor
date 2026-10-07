//go:build windows

package main

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"
)

// acceptHostApp is the app as the Acceptance tab's host.
type acceptHostApp struct{ a *app }

func (h acceptHostApp) acceptCmd(ctx context.Context, args ...string) *exec.Cmd {
	cmd := exec.CommandContext(ctx, "go", args...)
	cmd.Dir, cmd.Env = filepath.Join(h.a.repo, "go"), goEnv()
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	return cmd
}

func (h acceptHostApp) List() (string, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Minute)
	defer cancel()
	var stdout, stderr bytes.Buffer
	cmd := h.acceptCmd(ctx, "run", "./internal/nativeaccept/cmd/acceptance", "list")
	cmd.Stdout, cmd.Stderr = &stdout, &stderr
	if err := cmd.Run(); err != nil {
		return "", fmt.Errorf("acceptance list: %v: %s", err, strings.TrimSpace(stderr.String()))
	}
	return stdout.String(), nil
}

// Guard refuses while anything else uses the game copy: a run rebuilds the
// mod there as the test build and opens its own game on the launcher's root.
func (h acceptHostApp) Guard() error {
	h.a.poll()
	h.a.mu.Lock()
	defer h.a.mu.Unlock()
	switch {
	case h.a.ctrl != ctrlStopped:
		return errors.New("stop the controller first (Launch tab)")
	case h.a.gameUp:
		return errors.New("close the game first (Launch tab)")
	case h.a.busy[jobSetup] || h.a.busy[jobController]:
		return errors.New("still preparing the game files and controller; try again in a moment")
	}
	return nil
}

func (h acceptHostApp) OutputDir(name string) string {
	return filepath.Join(h.a.layout.Root, "acceptance", "launcher", time.Now().Format("20060102-150405"))
}

func (h acceptHostApp) Spawn(name, output string, fresh bool, out io.Writer) (acceptProc, error) {
	exe := h.a.controllerExe()
	if _, err := os.Stat(exe); err != nil {
		exe = ""
	}
	cmd := h.acceptCmd(context.Background(), AcceptRunArgs(name, h.a.layout.Root, output, exe, fresh)...)
	cmd.Stdout, cmd.Stderr = out, out
	h.a.logf("acceptance: go %s", strings.Join(cmd.Args[1:], " "))
	if err := cmd.Start(); err != nil {
		return nil, err
	}
	return &acceptCmd{cmd}, nil
}

type acceptCmd struct{ cmd *exec.Cmd }

func (c *acceptCmd) Wait() error { return c.cmd.Wait() }

// Stop ends the whole tree (go run, the acceptance command, the services it
// started); the game it opened is closed from the Launch tab.
func (c *acceptCmd) Stop() {
	kill := exec.Command("taskkill", "/T", "/F", "/PID", strconv.Itoa(c.cmd.Process.Pid))
	kill.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	if err := kill.Run(); err != nil {
		c.cmd.Process.Kill()
	}
}
