// Package inputs knows which checked-in files an acceptance harness run and
// the native mod build depend on, and hashes them. It has no game, bridge
// or protobuf dependencies so the tools that only need the hashes (the
// verified, land and affected commands) build and link in a moment
// instead of carrying the whole nativeaccept package.
package inputs

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// nativeSourceInput is one rule of the source copy build_native_mod.ps1
// makes under build/source: which repository path it takes, where it lands
// relative to the copy and which files it keeps. SourceTreeHash reproduces
// the copy's file list from these so the harness can hash the worktree
// without running the build.
type nativeSourceInput struct {
	repo, copy string
	// keep decides whether a file (relative path under repo, forward
	// slashes) is copied; nil keeps everything.
	keep func(relative string) bool
}

var (
	buildOutputDir = regexp.MustCompile(`(^|/)(obj|bin)(/|$)`)
	nativeBuildDir = regexp.MustCompile(`(^|/)(obj|bin|Assemblies|BridgeTools)(/|$)`)
	binaryExt      = map[string]bool{".dll": true, ".pdb": true, ".exe": true}
)

func notNativeBuildOutput(relative string) bool { return !nativeBuildDir.MatchString(relative) }

func contractSource(relative string) bool {
	return !buildOutputDir.MatchString(relative) && !binaryExt[strings.ToLower(filepath.Ext(relative))]
}

func fixtureSource(relative string) bool {
	ext := strings.ToLower(filepath.Ext(relative))
	return (ext == ".cs" || ext == ".csproj") && !buildOutputDir.MatchString(relative)
}

// nativeSourceInputs mirrors build_native_mod.ps1's copy loops; keep the
// two in step when the build takes a new input.
var nativeSourceInputs = []nativeSourceInput{
	{repo: "integrations/rimgovernor-native/src", copy: "integrations/rimgovernor-native/src", keep: notNativeBuildOutput},
	{repo: "integrations/rimgovernor-native/About", copy: "integrations/rimgovernor-native/About", keep: notNativeBuildOutput},
	{repo: "integrations/rimgovernor-native/Notices", copy: "integrations/rimgovernor-native/Notices", keep: notNativeBuildOutput},
	{repo: "integrations/rimgovernor-native/README.md", copy: "integrations/rimgovernor-native/README.md"},
	{repo: "contracts/proto", copy: "contracts/proto", keep: contractSource},
	{repo: "contracts/generated/protobuf/csharp", copy: "contracts/generated/protobuf/csharp", keep: contractSource},
	{repo: "tools/protobuf", copy: "tools/protobuf", keep: contractSource},
	{repo: "scripts/build_native_mod.ps1", copy: "scripts/build_native_mod.ps1"},
	{repo: "go/internal/protobufgen/cmd/generatecsharp/main.go", copy: "scripts/generate_protobuf.go"},
	{repo: "scripts/fixtures", copy: "scripts/fixtures", keep: fixtureSource},
	{repo: "THIRD_PARTY.md", copy: "THIRD_PARTY.md"},
}

// NativeSourceRoots lists, repo-relative with forward slashes, the paths
// build_native_mod.ps1 copies from (the same rules SourceTreeHash walks).
func NativeSourceRoots() []string {
	roots := make([]string, 0, len(nativeSourceInputs))
	for _, input := range nativeSourceInputs {
		roots = append(roots, input.repo)
	}
	return roots
}

// SourceTreeHash is the hash build_native_mod.ps1 records as sourceTree:
// over the sorted list of the build's copied source files, one
// "<path>\t<sha256>\n" line each with the path relative to the copy root
// (forward slashes), it is the hex SHA-256 of those lines. Computed here
// over the worktree at repo, it equals the manifest's when the installed
// build was made from these exact sources.
func SourceTreeHash(repo string) (string, error) {
	lines := map[string]string{}
	for _, input := range nativeSourceInputs {
		root := filepath.Join(repo, filepath.FromSlash(input.repo))
		info, err := os.Stat(root)
		if err != nil {
			return "", fmt.Errorf("native build input %s: %w", input.repo, err)
		}
		if !info.IsDir() {
			sum, err := fileHash(root)
			if err != nil {
				return "", err
			}
			lines[input.copy] = sum
			continue
		}
		err = filepath.WalkDir(root, func(path string, entry fs.DirEntry, err error) error {
			if err != nil || entry.IsDir() {
				return err
			}
			relative, err := filepath.Rel(root, path)
			if err != nil {
				return err
			}
			relative = filepath.ToSlash(relative)
			if input.keep != nil && !input.keep(relative) {
				return nil
			}
			sum, err := fileHash(path)
			if err != nil {
				return err
			}
			lines[input.copy+"/"+relative] = sum
			return nil
		})
		if err != nil {
			return "", err
		}
	}
	return hashLines(lines), nil
}

// hashLines hashes a path→sha256 map the way SourceTreeHash describes.
func hashLines(lines map[string]string) string {
	paths := make([]string, 0, len(lines))
	for path := range lines {
		paths = append(paths, path)
	}
	sort.Strings(paths)
	h := sha256.New()
	for _, path := range paths {
		fmt.Fprintf(h, "%s\t%s\n", path, lines[path])
	}
	return hex.EncodeToString(h.Sum(nil))
}

func fileHash(path string) (string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:]), nil
}

// FindRepo walks up from dir to the checkout root (the directory holding
// .git, a directory in the main checkout and a file in a worktree).
func FindRepo(dir string) (string, bool) {
	if abs, err := filepath.Abs(dir); err == nil {
		dir = abs
	}
	for {
		if _, err := os.Stat(filepath.Join(dir, ".git")); err == nil {
			return dir, true
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return "", false
		}
		dir = parent
	}
}
