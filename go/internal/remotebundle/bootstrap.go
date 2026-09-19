package remotebundle

import (
	"context"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"time"
)

// Trust is supplied by the trusted default-branch workflow after review of the
// exact tested commit. It is not accepted from the tested checkout or a PR.
type Trust struct {
	Repository     string `json:"repository"`
	TestedCommit   string `json:"tested_commit"`
	WorkflowCommit string `json:"workflow_commit"`
	Event          string `json:"event"`
}

func (t Trust) Validate(repository, head, event, workflow string) error {
	sha := regexp.MustCompile(`^[0-9a-f]{40}$`)
	if !repositoryPattern.MatchString(t.Repository) || !sha.MatchString(t.TestedCommit) || !sha.MatchString(t.WorkflowCommit) || t.Repository != repository || t.TestedCommit != head || t.WorkflowCommit != workflow || t.Event != event || (event != "workflow_dispatch" && event != "push" && event != "schedule") {
		return fmt.Errorf("bootstrap trust mismatch: require same repository, reviewed exact commit, trusted workflow commit and push/dispatch event")
	}
	return nil
}

func CheckTrust(ctx context.Context, repo string, t Trust) error {
	c := exec.CommandContext(ctx, "git", "-C", repo, "rev-parse", "HEAD")
	b, err := c.Output()
	if err != nil {
		return err
	}
	if err := t.Validate(os.Getenv("GITHUB_REPOSITORY"), strings.TrimSpace(string(b)), os.Getenv("GITHUB_EVENT_NAME"), os.Getenv("GITHUB_WORKFLOW_SHA")); err != nil {
		return err
	}
	if (t.Event == "push" || t.Event == "schedule") && (os.Getenv("GITHUB_REF") != "refs/heads/main" || os.Getenv("GITHUB_REF_PROTECTED") != "true") {
		return fmt.Errorf("push bootstrap requires protected main")
	}
	c = exec.CommandContext(ctx, "git", "-C", repo, "status", "--porcelain", "--untracked-files=no")
	b, err = c.Output()
	if err != nil {
		return err
	}
	if strings.TrimSpace(string(b)) != "" {
		return fmt.Errorf("bootstrap requires a clean tested checkout")
	}
	return nil
}

// CheckAuthorizationPath prevents a tested checkout from supplying its own
// authorization. The trusted workflow still owns the file's creation/content.
func CheckAuthorizationPath(repo, authorization string) error {
	r, err := resolveSource(repo)
	if err != nil {
		return err
	}
	p, err := resolveSource(authorization)
	if err != nil {
		return err
	}
	if within(r, p) {
		return fmt.Errorf("authorization must be supplied outside the tested checkout by the trusted workflow")
	}
	return nil
}

type BootstrapOptions struct {
	Repo, Work, Cache, Manifest, ManifestSHA256, Identity string
	Trust                                                 Trust
	Tools                                                 Tools
	// Role is fixture or production. A job that needs both bootstraps two
	// distinct Work directories before starting either game.
	Role string
	Log  io.Writer
}
type BootstrapReport struct {
	SchemaVersion int    `json:"schema_version"`
	BundleSHA256  string `json:"bundle_sha256"`
	CacheKey      string `json:"cache_key"`
	CacheHit      bool   `json:"cache_hit"`
	OS            string `json:"os"`
	Arch          string `json:"arch"`
	CPUs          int    `json:"cpu_count"`
	ImageVersion  string `json:"image_version"`
	Role          string `json:"role"`
	Starts        string `json:"starts"`
	RestoreMS     int64  `json:"restore_ms"`
	ExtractMS     int64  `json:"extract_ms"`
	BuildMS       int64  `json:"build_ms"`
	TotalMS       int64  `json:"total_ms"`
	Root          string `json:"root"`
	Acceptance    string `json:"acceptance"`
	Controller    string `json:"controller"`
	Passed        bool   `json:"passed"`
	Error         string `json:"error"`
	FreeDiskBytes uint64 `json:"free_disk_bytes"`
}

// Bootstrap is called only after workflow trust gating and before any game is
// launched. Even cache hits authenticate the origin and verify all ciphertext.
func Bootstrap(ctx context.Context, c OriginClient, o BootstrapOptions) (report BootstrapReport, resultErr error) {
	started := time.Now()
	if err := CheckTrust(ctx, o.Repo, o.Trust); err != nil {
		return report, err
	} // before cache access
	if runtime.GOOS != "windows" || runtime.GOARCH != "amd64" {
		return report, fmt.Errorf("bootstrap requires Windows x64")
	}
	if o.Role != "fixture" && o.Role != "production" {
		return report, fmt.Errorf("role must be fixture or production")
	}
	if !filepath.IsAbs(o.Work) || len(filepath.Join(o.Work, "layout", "native-rimworld")) > 140 {
		return report, fmt.Errorf("use a short absolute writable job directory")
	}
	if within(o.Work, o.Cache) || within(o.Cache, o.Work) {
		return report, fmt.Errorf("ciphertext cache and plaintext work must be disjoint")
	}
	if err := os.Mkdir(o.Work, 0700); err != nil {
		return report, fmt.Errorf("job work directory must be new: %w", err)
	}
	report = BootstrapReport{SchemaVersion: 1, BundleSHA256: o.ManifestSHA256, CacheKey: CacheKey(o.ManifestSHA256), OS: runtime.GOOS, Arch: runtime.GOARCH, CPUs: runtime.NumCPU(), ImageVersion: os.Getenv("ImageVersion"), Role: o.Role}
	defer func() {
		report.TotalMS = time.Since(started).Milliseconds()
		report.Passed = resultErr == nil
		if resultErr != nil {
			report.Error = resultErr.Error()
		}
		if e := WriteJSON(filepath.Join(o.Work, "bootstrap.json"), report); resultErr == nil && e != nil {
			resultErr = e
		}
	}()
	m, err := ReadManifest(o.Manifest, o.ManifestSHA256)
	if err != nil {
		return report, err
	}
	if m.Origin.Repository != o.Trust.Repository {
		return report, fmt.Errorf("bundle origin must be the trusted public source repository")
	}
	inv, err := ReadInventory(filepath.Dir(o.Manifest), m)
	if err != nil {
		return report, err
	}
	report.FreeDiskBytes, err = freeDisk(o.Work)
	if err != nil {
		return report, err
	}
	required := uint64(m.UnpackedBytes)*2 + 2<<30
	for _, p := range m.Parts {
		required += uint64(p.Bytes) * 2
	}
	if report.FreeDiskBytes < required {
		return report, fmt.Errorf("insufficient disk: need %d bytes for verified extraction/build, have %d", required, report.FreeDiskBytes)
	}
	now := time.Now()
	report.CacheHit, err = Restore(ctx, c, o.Cache, o.ManifestSHA256, m)
	report.RestoreMS = time.Since(now).Milliseconds()
	if err != nil {
		return report, err
	}
	tree := filepath.Join(o.Work, "dependencies")
	now = time.Now()
	if err := Extract(ctx, o.Tools, filepath.Join(o.Cache, report.CacheKey), tree, o.Identity, m, inv); err != nil {
		return report, err
	}
	report.ExtractMS = time.Since(now).Milliseconds()
	report.Starts, err = ValidateStarts(tree, o.Repo, m)
	if err != nil {
		return report, err
	}
	if o.Log == nil {
		o.Log = io.Discard
	}
	fmt.Fprintln(o.Log, report.Starts)
	// Tool installation/pinning is handled by bootstrap-remote.ps1 before Go
	// starts; setup remains the only native-build entry point.
	layout := filepath.Join(o.Work, "layout")
	report.Root = filepath.Join(layout, "bridge")
	report.Acceptance = filepath.Join(layout, "bin", "acceptance.exe")
	report.Controller = filepath.Join(layout, "bin", "rimgovernor.exe")
	args := []string{"run", "./internal/nativeaccept/cmd/acceptance", "setup", "-worktree", o.Repo, "-layout", layout, "-explicit", "-rimworld", filepath.Join(tree, "game"), "-bridge", filepath.Join(tree, "bridge"), "-sdk", filepath.Join(tree, "bridge", "1.6", "Assemblies"), "-harmony-mod", filepath.Join(tree, "harmony"), "-harmony", filepath.Join(tree, "harmony", "Current", "Assemblies", "0Harmony.dll"), "-gabs", filepath.Join(tree, "gabs", "gabs.exe")}
	if o.Role == "production" {
		args = append(args, "-production")
	}
	run := func(exe string, args ...string) error {
		cmd := exec.CommandContext(ctx, exe, args...)
		cmd.Dir = filepath.Join(o.Repo, "go")
		cmd.Env = PublicEnvironment()
		cmd.Stdout = o.Log
		cmd.Stderr = o.Log
		return cmd.Run()
	}
	now = time.Now()
	if err := run("go", args...); err != nil {
		return report, fmt.Errorf("explicit acceptance setup: %w", err)
	}
	report.BuildMS = time.Since(now).Milliseconds()
	if err := StageStarts(tree, report.Root, report.Starts); err != nil {
		return report, err
	}
	if err := run(report.Acceptance, "doctor", "-root", report.Root, "-worktree", o.Repo, "-rimgovernor", report.Controller); err != nil {
		return report, fmt.Errorf("runner preflight: %w", err)
	}
	return report, nil
}
