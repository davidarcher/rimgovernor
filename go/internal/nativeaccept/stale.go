package nativeaccept

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/inputs"
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

// SourceTreeHash is inputs.SourceTreeHash: the hash build_native_mod.ps1
// records as sourceTree, computed over the worktree at repo.
func SourceTreeHash(repo string) (string, error) { return inputs.SourceTreeHash(repo) }

// FindRepo is inputs.FindRepo: the checkout root enclosing dir.
func FindRepo(dir string) (string, bool) { return inputs.FindRepo(dir) }

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
	args = append(args, inputs.NativeSourceRoots()...)
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
