package main

import (
	"bytes"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// worktree is one entry of `git worktree list --porcelain`.
type worktree struct {
	Path, Branch string // Branch is "" for a detached HEAD
}

func parseWorktrees(porcelain string) []worktree {
	var out []worktree
	for _, line := range strings.Split(porcelain, "\n") {
		line = strings.TrimRight(line, "\r")
		switch {
		case strings.HasPrefix(line, "worktree "):
			out = append(out, worktree{Path: filepath.Clean(strings.TrimPrefix(line, "worktree "))})
		case strings.HasPrefix(line, "branch refs/heads/") && len(out) > 0:
			out[len(out)-1].Branch = strings.TrimPrefix(line, "branch refs/heads/")
		}
	}
	return out
}

func listWorktrees(repo string) ([]worktree, error) {
	out, err := git(repo, "worktree", "list", "--porcelain")
	if err != nil {
		return nil, err
	}
	return parseWorktrees(out), nil
}

// dirtyPath is one uncommitted path in the main checkout with what land
// could learn about who put it there.
type dirtyPath struct {
	Status   string    // porcelain XY
	Path     string    // repository-relative, slash-separated
	Modified time.Time // zero when the file is gone
	Landed   string    // main's last commit touching the path: "<hash> <date> <subject>"
	Owners   []string  // worktrees holding the same content
}

// parseStatusZ reads `git status --porcelain -z` into XY and path pairs; a
// rename or copy carries its source as a second entry, which is skipped.
func parseStatusZ(out string) []dirtyPath {
	var paths []dirtyPath
	entries := strings.Split(out, "\x00")
	for i := 0; i < len(entries); i++ {
		e := entries[i]
		if len(e) < 4 {
			continue
		}
		paths = append(paths, dirtyPath{Status: e[:2], Path: e[3:]})
		if e[0] == 'R' || e[0] == 'C' {
			i++
		}
	}
	return paths
}

// requireCleanMain refuses a dirty main checkout, naming each dirty path,
// when it last changed, main's last landing on it and every worktree that
// holds the same content, so the operator can find the session that
// edited main directly.
func requireCleanMain(mainCheckout string, trees []worktree) error {
	out, err := gitOutput(mainCheckout, "status", "--porcelain", "-z", "--untracked-files=no")
	if err != nil {
		return err
	}
	paths := parseStatusZ(out)
	if len(paths) == 0 {
		return nil
	}
	for i := range paths {
		p := &paths[i]
		content, readErr := os.ReadFile(filepath.Join(mainCheckout, filepath.FromSlash(p.Path)))
		if info, err := os.Stat(filepath.Join(mainCheckout, filepath.FromSlash(p.Path))); err == nil {
			p.Modified = info.ModTime()
		}
		p.Landed, _ = git(mainCheckout, "log", "-1", "--format=%h %cd %s", "--date=format:%Y-%m-%d %H:%M:%S", "main", "--", p.Path)
		if readErr != nil {
			continue
		}
		for _, wt := range trees {
			if filepath.Clean(wt.Path) == filepath.Clean(mainCheckout) {
				continue
			}
			other, err := os.ReadFile(filepath.Join(wt.Path, filepath.FromSlash(p.Path)))
			if err != nil || !bytes.Equal(other, content) {
				continue
			}
			where := "uncommitted there"
			if _, err := git(wt.Path, "diff", "--quiet", "HEAD", "--", p.Path); err == nil {
				where = "committed at its HEAD"
			}
			p.Owners = append(p.Owners, fmt.Sprintf("%s (%s), %s", wt.Path, branchLabel(wt.Branch), where))
		}
	}
	return fmt.Errorf("%s", dirtyMessage(mainCheckout, paths, time.Now()))
}

func branchLabel(branch string) string {
	if branch == "" {
		return "detached"
	}
	return branch
}

// dirtyMessage is the refusal for a dirty main checkout.
func dirtyMessage(dir string, paths []dirtyPath, now time.Time) string {
	var b strings.Builder
	fmt.Fprintf(&b, "main checkout %s has uncommitted changes; land never edits it and no session may (AGENTS.md):\n", dir)
	for _, p := range paths {
		fmt.Fprintf(&b, "  %s %s\n", p.Status, p.Path)
		if p.Modified.IsZero() {
			b.WriteString("      file is gone\n")
		} else {
			fmt.Fprintf(&b, "      modified %s (%s ago)\n", p.Modified.Format("2006-01-02 15:04:05"), now.Sub(p.Modified).Round(time.Second))
		}
		if p.Landed != "" {
			fmt.Fprintf(&b, "      main last changed it in %s\n", p.Landed)
		}
		if len(p.Owners) == 0 && !p.Modified.IsZero() {
			b.WriteString("      no other worktree holds this content\n")
		}
		for _, o := range p.Owners {
			fmt.Fprintf(&b, "      same content in %s\n", o)
		}
	}
	b.WriteString("the session that owns an edit moves it to its own worktree; a leftover is restored with git checkout -- <path> in the main checkout; then run land again")
	return b.String()
}
