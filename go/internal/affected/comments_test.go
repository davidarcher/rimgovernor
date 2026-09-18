package affected

import (
	"os"
	"os/exec"
	"path/filepath"
	"slices"
	"testing"
)

const baseSource = `// Package p is the base.
package p

//go:build !windows

// F does something.
func F() int { return 1 }
`

func TestCommentOnly(t *testing.T) {
	cases := []struct {
		name string
		src  string
		want bool
	}{
		{"comment reworded", `// Package p is the base, reworded.
package p

//go:build !windows

// F does something else, says the comment.
func F() int { return 1 }
`, true},
		{"comment added inline", `// Package p is the base.
package p

//go:build !windows

// F does something.
// A second line.
func F() int { return 1 } // inline
`, true},
		{"reflowed across lines", `// Package p is the base.
package p

//go:build !windows

// F does something.
func F() int {
	return 1
}
`, false},
		{"code changed", `// Package p is the base.
package p

//go:build !windows

// F does something.
func F() int { return 2 }
`, false},
		{"directive changed", `// Package p is the base.
package p

//go:build windows

// F does something.
func F() int { return 1 }
`, false},
		{"directive dropped", `// Package p is the base.
package p

// F does something.
func F() int { return 1 }
`, false},
		{"string literal changed", `// Package p is the base.
package p

//go:build !windows

// F does something.
func F() int { return 1 }

const S = "text"
`, false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			repo := scratchRepo(t, "p.go", baseSource)
			write(t, repo, "p.go", tc.src)
			if got := commentOnly(repo, "HEAD", "p.go"); got != tc.want {
				t.Errorf("commentOnly = %v, want %v", got, tc.want)
			}
		})
	}
}

// ChangedFiles drops a comment-only Go edit, keeps a code edit and never
// looks inside a non-Go file.
func TestChangedFilesDropsCommentOnlyEdits(t *testing.T) {
	repo := scratchRepo(t, "p.go", baseSource)
	write(t, repo, "q.go", "package p\n\nfunc G() {}\n")
	write(t, repo, "README.md", "# base\n")
	gitRun(t, repo, "add", ".")
	gitRun(t, repo, "commit", "-q", "-m", "more")
	gitRun(t, repo, "branch", "-q", "base")

	write(t, repo, "p.go", "// Package p, reworded.\npackage p\n\n//go:build !windows\n\nfunc F() int { return 1 }\n")
	write(t, repo, "q.go", "package p\n\nfunc G() int { return 0 }\n")
	write(t, repo, "README.md", "# reworded\n")
	write(t, repo, "new.go", "package p\n")

	got, err := ChangedFiles(repo, "base")
	if err != nil {
		t.Fatal(err)
	}
	if want := []string{"README.md", "new.go", "q.go"}; !slices.Equal(got, want) {
		t.Errorf("ChangedFiles = %v, want %v", got, want)
	}
}

func scratchRepo(t *testing.T, name, src string) string {
	t.Helper()
	repo := t.TempDir()
	gitRun(t, repo, "init", "-q")
	gitRun(t, repo, "config", "user.email", "t@example.com")
	gitRun(t, repo, "config", "user.name", "t")
	write(t, repo, name, src)
	gitRun(t, repo, "add", ".")
	gitRun(t, repo, "commit", "-q", "-m", "base")
	return repo
}

func write(t *testing.T, repo, name, src string) {
	t.Helper()
	if err := os.WriteFile(filepath.Join(repo, name), []byte(src), 0o644); err != nil {
		t.Fatal(err)
	}
}

func gitRun(t *testing.T, repo string, args ...string) {
	t.Helper()
	cmd := exec.Command("git", args...)
	cmd.Dir = repo
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("git %v: %v\n%s", args, err, out)
	}
}
