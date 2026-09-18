package nativeaccept

import (
	"bytes"
	"fmt"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// VerifiedTrailerKey is the commit trailer that records an acceptance
// harness run: "Verified: <harness> inputs=<hash>". The hash covers every
// checked-in file the harness outcome depends on (HarnessInputs), so a
// landing commit whose hash still matches the trailer counts as run; a
// merge or rebase that only moved unrelated files does not invalidate it.
const VerifiedTrailerKey = "Verified"

// VerifiedHashLength is how many hex digits of the sha256 the trailer keeps.
const VerifiedHashLength = 16

// VerifiedTrailer is one parsed "Verified:" trailer.
type VerifiedTrailer struct {
	Harness string
	Inputs  string
}

// String renders the trailer line as it appears in a commit message.
func (t VerifiedTrailer) String() string {
	return fmt.Sprintf("%s: %s inputs=%s", VerifiedTrailerKey, t.Harness, t.Inputs)
}

var verifiedTrailerLine = regexp.MustCompile(`^` + VerifiedTrailerKey + `:\s+(\S+)\s+inputs=([0-9a-f]+)\s*$`)

// ParseVerifiedTrailers returns the Verified trailers in a commit message,
// in order. Lines that do not parse are ignored.
func ParseVerifiedTrailers(message string) []VerifiedTrailer {
	var trailers []VerifiedTrailer
	for _, line := range strings.Split(message, "\n") {
		if m := verifiedTrailerLine.FindStringSubmatch(strings.TrimSpace(line)); m != nil {
			trailers = append(trailers, VerifiedTrailer{Harness: m[1], Inputs: m[2]})
		}
	}
	return trailers
}

// harnessSharedInputs are the checked-in inputs every harness run depends
// on besides its Go packages: the native mod build (nativeSourceInputs, the
// same list RequireCurrentPackage compares) and the fixture saves and
// contract fixtures a harness loads.
var harnessSharedInputs = []string{
	"go/go.mod",
	"go/go.sum",
	"scripts/fixtures",
	"contracts/fixtures",
}

// HarnessInputs lists, repo-relative with forward slashes, the files an
// acceptance harness run depends on: every non-test file of each in-module
// Go package the harness and the rimgovernor binary it drives import, the
// native mod's build inputs and harnessSharedInputs. harness is the
// directory name under go/internal/nativeaccept/cmd. It does not cover the
// game, GABS or the machine: those are environment, not inputs.
func HarnessInputs(repo, harness string) ([]string, error) {
	if harness == "" || strings.ContainsAny(harness, `/\.`) {
		return nil, fmt.Errorf("harness %q is not a directory name under go/internal/nativeaccept/cmd", harness)
	}
	goDir := filepath.Join(repo, "go")
	if _, err := os.Stat(filepath.Join(goDir, "internal", "nativeaccept", "cmd", harness)); err != nil {
		return nil, fmt.Errorf("harness %s: %w", harness, err)
	}
	dirs, err := goPackageDirs(goDir, "./internal/nativeaccept/cmd/"+harness, "./cmd/rimgovernor")
	if err != nil {
		return nil, err
	}
	seen := map[string]bool{}
	var files []string
	add := func(relative string) {
		if !seen[relative] {
			seen[relative] = true
			files = append(files, relative)
		}
	}
	for _, dir := range dirs {
		entries, err := os.ReadDir(dir)
		if err != nil {
			return nil, err
		}
		relDir, err := filepath.Rel(repo, dir)
		if err != nil {
			return nil, err
		}
		for _, entry := range entries {
			if entry.IsDir() || strings.HasSuffix(entry.Name(), "_test.go") {
				continue
			}
			add(filepath.ToSlash(filepath.Join(relDir, entry.Name())))
		}
	}
	for _, input := range nativeSourceInputs {
		if err := walkInput(repo, input.repo, input.keep, add); err != nil {
			return nil, err
		}
	}
	for _, input := range harnessSharedInputs {
		if err := walkInput(repo, input, nil, add); err != nil {
			return nil, err
		}
	}
	sort.Strings(files)
	return files, nil
}

// walkInput calls add for each file under the repo-relative input (or the
// input itself when it is a file) that keep accepts; a missing input is an
// error since every listed input is checked in.
func walkInput(repo, input string, keep func(string) bool, add func(string)) error {
	root := filepath.Join(repo, filepath.FromSlash(input))
	info, err := os.Stat(root)
	if err != nil {
		return fmt.Errorf("harness input %s: %w", input, err)
	}
	if !info.IsDir() {
		add(input)
		return nil
	}
	return filepath.WalkDir(root, func(path string, entry fs.DirEntry, err error) error {
		if err != nil || entry.IsDir() {
			return err
		}
		relative, err := filepath.Rel(root, path)
		if err != nil {
			return err
		}
		relative = filepath.ToSlash(relative)
		if keep != nil && !keep(relative) {
			return nil
		}
		add(input + "/" + relative)
		return nil
	})
}

// goPackageDirs returns the directories of the in-module packages the
// given patterns (relative to goDir) transitively import.
func goPackageDirs(goDir string, patterns ...string) ([]string, error) {
	module, err := goList(goDir, "-m", "-f", "{{.Path}}")
	if err != nil {
		return nil, err
	}
	modulePath := strings.TrimSpace(module)
	out, err := goList(goDir, append([]string{"-deps", "-f", "{{.ImportPath}}\t{{.Dir}}"}, patterns...)...)
	if err != nil {
		return nil, err
	}
	var dirs []string
	for _, line := range strings.Split(out, "\n") {
		importPath, dir, ok := strings.Cut(strings.TrimSpace(line), "\t")
		if !ok || importPath != modulePath && !strings.HasPrefix(importPath, modulePath+"/") {
			continue
		}
		dirs = append(dirs, dir)
	}
	sort.Strings(dirs)
	return dirs, nil
}

func goList(goDir string, args ...string) (string, error) {
	cmd := exec.Command("go", append([]string{"list"}, args...)...)
	cmd.Dir = goDir
	var stderr bytes.Buffer
	cmd.Stderr = &stderr
	out, err := cmd.Output()
	if err != nil {
		return "", fmt.Errorf("go list %s: %w: %s", strings.Join(args, " "), err, strings.TrimSpace(stderr.String()))
	}
	return string(out), nil
}

// HarnessInputHash hashes the working-tree contents of HarnessInputs the
// way SourceTreeHash does, truncated to VerifiedHashLength. Run it in the
// checkout the harness ran from, before committing anything else, and in
// the landing checkout to compare.
func HarnessInputHash(repo, harness string) (string, error) {
	files, err := HarnessInputs(repo, harness)
	if err != nil {
		return "", err
	}
	lines := make(map[string]string, len(files))
	for _, file := range files {
		sum, err := fileHash(filepath.Join(repo, filepath.FromSlash(file)))
		if err != nil {
			return "", err
		}
		lines[file] = sum
	}
	return hashLines(lines)[:VerifiedHashLength], nil
}

// NewVerifiedTrailer records that harness ran against the current working
// tree of repo.
func NewVerifiedTrailer(repo, harness string) (VerifiedTrailer, error) {
	hash, err := HarnessInputHash(repo, harness)
	if err != nil {
		return VerifiedTrailer{}, err
	}
	return VerifiedTrailer{Harness: harness, Inputs: hash}, nil
}

// RecordedVerifiedTrailers returns each harness's newest Verified trailer
// among the commit messages in the git revision range rev (e.g. main..HEAD).
func RecordedVerifiedTrailers(repo, rev string) (map[string]VerifiedTrailer, error) {
	cmd := exec.Command("git", "log", "--format=%B%x1e", rev)
	cmd.Dir = repo
	var stderr bytes.Buffer
	cmd.Stderr = &stderr
	out, err := cmd.Output()
	if err != nil {
		return nil, fmt.Errorf("git log %s: %w: %s", rev, err, strings.TrimSpace(stderr.String()))
	}
	latest := map[string]VerifiedTrailer{}
	for _, message := range strings.Split(string(out), string(rune(0x1e))) {
		for _, trailer := range ParseVerifiedTrailers(message) {
			if _, seen := latest[trailer.Harness]; !seen {
				latest[trailer.Harness] = trailer
			}
		}
	}
	return latest, nil
}
