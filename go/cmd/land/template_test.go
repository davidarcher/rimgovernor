package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

// templateRepo is main with one commit, built once per package run; newRepo
// copies it instead of paying for init, config, add and commit in every test.
var templateRepo string

func TestMain(m *testing.M) {
	dir, err := os.MkdirTemp("", "land-template-")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	templateRepo = filepath.Join(dir, "repo")
	code := 1
	if err = buildTemplateRepo(templateRepo); err == nil {
		code = m.Run()
	} else {
		fmt.Fprintln(os.Stderr, err)
	}
	os.RemoveAll(dir)
	os.Exit(code)
}

func buildTemplateRepo(root string) error {
	run := func(args ...string) error {
		if out, err := exec.Command("git", args...).CombinedOutput(); err != nil {
			return fmt.Errorf("git %s: %v\n%s", strings.Join(args, " "), err, out)
		}
		return nil
	}
	if err := run("init", "-q", "-b", "main", root); err != nil {
		return err
	}
	if err := run("-C", root, "config", "user.email", "t@example.com"); err != nil {
		return err
	}
	if err := run("-C", root, "config", "user.name", "t"); err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(root, "a.txt"), []byte("a\n"), 0o644); err != nil {
		return err
	}
	if err := run("-C", root, "add", "."); err != nil {
		return err
	}
	return run("-C", root, "commit", "-qm", "init")
}
