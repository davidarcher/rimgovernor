package main

import (
	"context"
	"flag"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const warmUsage = `
  acceptance warm -root <dir> [-game <id> -headless=false -background]`

// warm prepares the root's profile and boots its game to the main menu so
// the next run attaches instead of launching (#285). -background hands
// the work to a detached copy of this command, logging to
// <root>/acceptance/warm/warm.log, and returns at once with its pid; a
// post-build hook uses it after the native mod is installed.
func warm(ctx context.Context, args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("warm", flag.ContinueOnError)
	fs.SetOutput(stderr)
	root := fs.String("root", "", "absolute disposable worker root")
	game := fs.String("game", "rimgovernor-trial", "configured game ID")
	headless := fs.Bool("headless", true, "boot the headless profile (false: windowed)")
	background := fs.Bool("background", false, "boot in a detached process and return its pid")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	if *root == "" {
		fmt.Fprintln(stderr, "-root is required")
		return 2
	}
	if !filepath.IsAbs(*root) {
		fmt.Fprintf(stderr, "-root must be absolute: %s\n", *root)
		return 2
	}
	output := filepath.Join(*root, "acceptance", "warm")
	if *background {
		pid, err := warmDetached(output, *root, *game, *headless)
		if err != nil {
			fmt.Fprintln(stderr, err)
			return 1
		}
		fmt.Fprintf(stdout, "warming %s in pid %d (log: %s)\n", *root, pid, filepath.Join(output, "warm.log"))
		return 0
	}
	ctx, cancel := context.WithTimeout(ctx, 5*time.Minute)
	defer cancel()
	warmed, err := na.WarmGame(ctx, &na.Config{Root: *root, Output: output, GameID: *game, Headless: *headless})
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	switch {
	case warmed.Reused:
		fmt.Fprintf(stdout, "warm\t%s\treused a running process at the menu\t%s\n", *root, warmed.Open.Round(time.Millisecond))
	case warmed.Relaunched != "":
		fmt.Fprintf(stdout, "warm\t%s\trelaunched (%s) to the menu\t%s\n", *root, warmed.Relaunched, warmed.Open.Round(time.Millisecond))
	default:
		fmt.Fprintf(stdout, "warm\t%s\tlaunched to the menu\t%s\n", *root, warmed.Open.Round(time.Millisecond))
	}
	return 0
}

// warmDetached starts this binary's foreground warm as a process that
// outlives the caller's console, its output appended to output/warm.log.
func warmDetached(output, root, game string, headless bool) (int, error) {
	self, err := os.Executable()
	if err != nil {
		return 0, err
	}
	if err := os.MkdirAll(output, 0755); err != nil {
		return 0, err
	}
	log, err := os.OpenFile(filepath.Join(output, "warm.log"), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
	if err != nil {
		return 0, err
	}
	defer log.Close()
	fmt.Fprintf(log, "%s warm -root %s -game %s -headless=%t\n", time.Now().Format(time.RFC3339), root, game, headless)
	cmd := exec.Command(self, "warm", "-root", root, "-game", game, fmt.Sprintf("-headless=%t", headless))
	cmd.Stdout, cmd.Stderr = log, log
	cmd.SysProcAttr = detachedAttr()
	if err := cmd.Start(); err != nil {
		return 0, fmt.Errorf("start detached warm: %w", err)
	}
	pid := cmd.Process.Pid
	_ = cmd.Process.Release()
	return pid, nil
}
