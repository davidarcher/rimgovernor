package nativeaccept

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// AllowStaleModEnv skips RequireCurrentPackage: set it to run a harness
// against an installed native build whose sources are known to differ from
// the worktree (bisecting the mod against newer Go code, for instance).
const AllowStaleModEnv = "RIMGOVERNOR_ACCEPT_ALLOW_STALE_MOD"

// PackageManifestName is the build record scripts/build_native_mod.ps1
// writes at the package root.
const PackageManifestName = "native-manifest.json"

// PackageManifest is the part of native-manifest.json the harness reads: what
// the installed build was made from.
type PackageManifest struct {
	PackageID      string   `json:"packageId"`
	Role           string   `json:"role"`
	Fixtures       []string `json:"fixtures"`
	SourceRevision string   `json:"sourceRevision"`
	SourceDirty    bool     `json:"sourceDirty"`
	// SourceTree is SourceTreeHash over the inputs the build copied; builds
	// from before it was recorded leave it empty.
	SourceTree string `json:"sourceTree"`
}

// ReadPackageManifest reads pkg's native-manifest.json.
func ReadPackageManifest(pkg string) (*PackageManifest, error) {
	data, err := os.ReadFile(filepath.Join(pkg, PackageManifestName))
	if err != nil {
		return nil, err
	}
	var m PackageManifest
	if err := json.Unmarshal(data, &m); err != nil {
		return nil, fmt.Errorf("%s: %w", PackageManifestName, err)
	}
	return &m, nil
}

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
	dir = mustAbs(dir)
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

// installedPackage is what RequireCurrentPackage found, for Finalize's
// installed_package field: one package per harness process.
var installedPackage map[string]any

// ErrStalePackage is what RequireCurrentPackage returns when the installed
// build's sources differ from the worktree's.
var ErrStalePackage = errors.New("installed native build is stale for this worktree")

// RequireCurrentPackage fails, before any game is launched, when pkg (the
// installed Mods/RimGovernor) was built from native sources that differ
// from the worktree the harness runs in. A stale build otherwise surfaces
// minutes later and obliquely: a missing fixture op, an INVALID_REQUEST
// ProtoJSON refusal, a receipt shape the Go side no longer decodes. The
// check compares the manifest's sourceTree with SourceTreeHash over the
// worktree; a build recorded before sourceTree existed falls back to git
// (the build revision against the worktree over the same inputs). It is
// skipped, and says so in the summary, when there is no manifest, no
// enclosing checkout, no usable git, or AllowStaleModEnv is set.
func RequireCurrentPackage(pkg string) (map[string]any, error) {
	summary := map[string]any{"path": pkg, "checked": false}
	installedPackage = summary
	manifest, err := ReadPackageManifest(pkg)
	if err != nil {
		summary["skipped"] = "no manifest: " + err.Error()
		return summary, nil
	}
	summary["role"], summary["fixtures"] = manifest.Role, manifest.Fixtures
	summary["source_revision"], summary["source_dirty"] = manifest.SourceRevision, manifest.SourceDirty
	if os.Getenv(AllowStaleModEnv) != "" {
		summary["skipped"] = AllowStaleModEnv + " set"
		return summary, nil
	}
	cwd, err := os.Getwd()
	if err != nil {
		summary["skipped"] = err.Error()
		return summary, nil
	}
	repo, ok := FindRepo(cwd)
	if !ok {
		summary["skipped"] = "no enclosing checkout under " + cwd
		return summary, nil
	}
	summary["worktree"] = repo
	if manifest.SourceTree != "" {
		current, err := SourceTreeHash(repo)
		if err != nil {
			summary["skipped"] = err.Error()
			return summary, nil
		}
		summary["checked"], summary["method"] = true, "source_tree"
		summary["source_tree"], summary["worktree_source_tree"] = manifest.SourceTree, current
		if current != manifest.SourceTree {
			return summary, staleError(pkg, manifest, repo, "its native sources differ from this worktree's")
		}
		return summary, nil
	}
	// An older build: ask git whether the inputs changed since its revision.
	if manifest.SourceRevision == "" {
		summary["skipped"] = "manifest records neither sourceTree nor sourceRevision"
		return summary, nil
	}
	args := []string{"-C", repo, "diff", "--quiet", manifest.SourceRevision, "--"}
	for _, input := range nativeSourceInputs {
		args = append(args, input.repo)
	}
	out, err := exec.Command("git", args...).CombinedOutput()
	var exit *exec.ExitError
	switch {
	case err == nil:
		summary["checked"], summary["method"] = true, "git_diff"
		if manifest.SourceDirty {
			summary["note"] = "built from a dirty tree; uncommitted changes at build time are not compared"
		}
		return summary, nil
	case errors.As(err, &exit) && exit.ExitCode() == 1:
		summary["checked"], summary["method"] = true, "git_diff"
		return summary, staleError(pkg, manifest, repo, "native sources changed since revision "+short(manifest.SourceRevision))
	default:
		summary["skipped"] = "git diff: " + strings.TrimSpace(string(out)) + " " + err.Error()
		return summary, nil
	}
}

func staleError(pkg string, manifest *PackageManifest, repo, why string) error {
	fixtures := ""
	if len(manifest.Fixtures) > 0 {
		fixtures = " -Fixture " + strings.Join(manifest.Fixtures, ",")
	}
	return fmt.Errorf("%w: %s was built at %s and %s; rebuild it (pwsh scripts/build_native_mod.ps1%s -OutputRoot <fresh dir>, then install the RimGovernor package over %s) or set %s=1 to run against it anyway",
		ErrStalePackage, pkg, short(manifest.SourceRevision), why, fixtures, pkg, AllowStaleModEnv)
}

func short(revision string) string {
	if len(revision) > 12 {
		return revision[:12]
	}
	if revision == "" {
		return "an unknown revision"
	}
	return revision
}
