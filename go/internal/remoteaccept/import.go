package remoteaccept

import (
	"archive/zip"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"os"
	"os/exec"
	"path/filepath"
	"reflect"
	"strings"
)

// Trust is supplied by the operator, independently of downloaded manifests.
// Land reads these pins from local git config, never from artifact contents.
type Trust struct {
	Repository     string `json:"repository"`
	Workflow       string `json:"workflow"`
	WorkflowCommit string `json:"workflow_commit"`
	BundleSHA256   string `json:"bundle_sha256"`
}
type Provenance struct {
	Trust         Trust  `json:"trust"`
	ArtifactID    int64  `json:"artifact_id"`
	ArchiveSHA256 string `json:"archive_sha256"`
	RunID         int64  `json:"run_id"`
	Attempt       int    `json:"attempt"`
}

// API only reads authenticated GitHub endpoints. Tests provide recorded metadata.
type API interface {
	Get(endpoint string, dst io.Writer) error
}
type GitHub struct{}

func (GitHub) Get(endpoint string, dst io.Writer) error {
	cmd := exec.Command("gh", "api", endpoint)
	cmd.Stdout = dst
	var stderr bytes.Buffer
	cmd.Stderr = &stderr
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("gh api: %w: %s", err, strings.TrimSpace(stderr.String()))
	}
	return nil
}
func apiJSON(api API, endpoint string, dst any) error {
	var b bytes.Buffer
	if err := api.Get(endpoint, &b); err != nil {
		return err
	}
	return json.Unmarshal(b.Bytes(), dst)
}

type artifactIdentity struct {
	digest string
	size   int64
}

func Authenticate(api API, p Provenance) (string, error) {
	a, err := authenticateArtifact(api, p)
	return a.digest, err
}

func authenticateArtifact(api API, p Provenance) (artifactIdentity, error) {
	t := p.Trust
	if !repository.MatchString(t.Repository) || !validPath(t.Workflow) || !strings.HasPrefix(t.Workflow, ".github/workflows/") || !oid.MatchString(t.WorkflowCommit) || !digest.MatchString(t.BundleSHA256) || p.ArtifactID <= 0 || p.RunID <= 0 || p.Attempt < 1 {
		return artifactIdentity{}, fmt.Errorf("missing/invalid operator trust pins or Actions identity")
	}
	var run struct {
		ID         int64  `json:"id"`
		Attempt    int    `json:"run_attempt"`
		Head       string `json:"head_sha"`
		Path       string `json:"path"`
		Event      string `json:"event"`
		Status     string `json:"status"`
		Conclusion string `json:"conclusion"`
		Repository struct {
			FullName string `json:"full_name"`
		} `json:"repository"`
		HeadRepository struct {
			FullName string `json:"full_name"`
		} `json:"head_repository"`
	}
	if err := apiJSON(api, fmt.Sprintf("repos/%s/actions/runs/%d/attempts/%d", t.Repository, p.RunID, p.Attempt), &run); err != nil {
		return artifactIdentity{}, err
	}
	if run.ID != p.RunID || run.Attempt != p.Attempt || run.Head != t.WorkflowCommit || run.Path != t.Workflow || run.Repository.FullName != t.Repository || run.HeadRepository.FullName != t.Repository || (run.Event != "push" && run.Event != "workflow_dispatch" && run.Event != "schedule") || run.Status != "completed" {
		return artifactIdentity{}, fmt.Errorf("actions run is not the pinned completed same-repository workflow")
	}
	if run.Conclusion != "success" && run.Conclusion != "failure" {
		return artifactIdentity{}, fmt.Errorf("actions run was cancelled, timed out, or has an unknown conclusion")
	}
	var artifact struct {
		ID          int64  `json:"id"`
		Expired     bool   `json:"expired"`
		Digest      string `json:"digest"`
		Size        int64  `json:"size_in_bytes"`
		WorkflowRun struct {
			ID   int64  `json:"id"`
			Head string `json:"head_sha"`
		} `json:"workflow_run"`
	}
	if err := apiJSON(api, fmt.Sprintf("repos/%s/actions/artifacts/%d", t.Repository, p.ArtifactID), &artifact); err != nil {
		return artifactIdentity{}, err
	}
	d := strings.TrimPrefix(artifact.Digest, "sha256:")
	if artifact.ID != p.ArtifactID || artifact.Expired || artifact.WorkflowRun.ID != p.RunID || artifact.WorkflowRun.Head != t.WorkflowCommit || artifact.Size < 1 || !digest.MatchString(d) || artifact.Digest != "sha256:"+d {
		return artifactIdentity{}, fmt.Errorf("artifact metadata/digest does not match the trusted run")
	}
	if p.ArchiveSHA256 != "" && p.ArchiveSHA256 != d {
		return artifactIdentity{}, fmt.Errorf("artifact digest changed")
	}
	return artifactIdentity{digest: d, size: artifact.Size}, nil
}

// Download always uses a new directory, preserves the archive and extracts only
// regular diagnostic files. It never downloads licensed bundle parts.
func Download(api API, p Provenance, output, repo string) error {
	identity, err := authenticateArtifact(api, p)
	if err != nil {
		return err
	}
	p.ArchiveSHA256 = identity.digest
	if err = os.Mkdir(output, 0o755); err != nil {
		return fmt.Errorf("new import directory: %w", err)
	}
	archive := filepath.Join(output, "actions-artifact.zip")
	f, err := os.OpenFile(archive, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
	if err != nil {
		return err
	}
	limited := &boundedWriter{w: f, left: identity.size}
	err = api.Get(fmt.Sprintf("repos/%s/actions/artifacts/%d/zip", p.Trust.Repository, p.ArtifactID), limited)
	closeErr := f.Close()
	if err != nil {
		return err
	}
	if closeErr != nil {
		return closeErr
	}
	if err = verifyArchive(archive, identity.digest); err != nil {
		return err
	}
	root := filepath.Join(output, "evidence")
	if err = os.Mkdir(root, 0o755); err != nil {
		return err
	}
	if err = extract(archive, root); err != nil {
		return err
	}
	ref, err := FileRef(root, "aggregate.json")
	if err != nil {
		return err
	}
	e, err := Verify(root, ref)
	if err != nil {
		return err
	}
	if err = matchProvenance(e.Run, p); err != nil {
		return err
	}
	if err = VerifySource(repo, e.Run); err != nil {
		return err
	}
	if err = VerifySelectionSource(repo, e.Run, e.Selection); err != nil {
		return err
	}
	if err = rejectSynthetic(e); err != nil {
		return err
	}
	e.Report.Remote.Import = &p
	// The result stays beside untouched manifests, preserving every relative link.
	if _, err = WriteJSON(root, "result.json", e.Report); err != nil {
		return err
	}
	if !e.Report.Passed {
		return fmt.Errorf("remote acceptance %s: %s", e.Aggregate.Status, *e.Report.Error)
	}
	return nil
}

type boundedWriter struct {
	w    io.Writer
	left int64
}

func (w *boundedWriter) Write(p []byte) (int, error) {
	if int64(len(p)) > w.left {
		return 0, fmt.Errorf("artifact exceeds size limit")
	}
	n, err := w.w.Write(p)
	w.left -= int64(n)
	return n, err
}
func verifyArchive(p, d string) error {
	f, err := os.Open(p)
	if err != nil {
		return err
	}
	defer f.Close()
	h := sha256.New()
	if _, err = io.Copy(h, f); err != nil {
		return err
	}
	if hex.EncodeToString(h.Sum(nil)) != d {
		return fmt.Errorf("archive digest mismatch")
	}
	return nil
}
func extract(archive, root string) error {
	z, err := zip.OpenReader(archive)
	if err != nil {
		return err
	}
	defer z.Close()
	seen := map[string]bool{}
	var total uint64
	for _, f := range z.File {
		p := strings.TrimSuffix(f.Name, "/")
		if !validPath(p) || f.Mode()&os.ModeSymlink != 0 {
			return fmt.Errorf("unsafe archive entry %s", f.Name)
		}
		key := strings.ToLower(p)
		if seen[key] {
			return fmt.Errorf("duplicate archive path %s", p)
		}
		seen[key] = true
		if f.FileInfo().IsDir() {
			continue
		}
		if !f.Mode().IsRegular() {
			return fmt.Errorf("nonregular archive entry")
		}
		ext := strings.ToLower(filepath.Ext(p))
		switch ext {
		case ".json", ".jsonl", ".log", ".txt", ".md", ".png":
		default:
			return fmt.Errorf("non-diagnostic archive entry %s", p)
		}
		if f.UncompressedSize64 >= math.MaxInt64 || total > math.MaxInt64-f.UncompressedSize64 {
			return fmt.Errorf("expanded evidence exceeds size limit")
		}
		total += f.UncompressedSize64
	}
	for _, f := range z.File {
		p := filepath.Join(root, filepath.FromSlash(f.Name))
		if f.FileInfo().IsDir() {
			if err = os.MkdirAll(p, 0o755); err != nil {
				return err
			}
			continue
		}
		if err = os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			return err
		}
		if err = extractFile(f, p); err != nil {
			return err
		}
		if strings.EqualFold(filepath.Ext(p), ".png") {
			b, err := os.ReadFile(p)
			if err != nil {
				return err
			}
			if err := publicDiagnostic(p, b); err != nil {
				return err
			}
		}
	}
	_, err = openTree(root)
	return err
}
func extractFile(f *zip.File, p string) error {
	in, err := f.Open()
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.OpenFile(p, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return err
	}
	_, err = io.Copy(out, io.LimitReader(in, int64(f.UncompressedSize64)+1))
	closeErr := out.Close()
	if err != nil {
		return err
	}
	return closeErr
}
func matchProvenance(r Run, p Provenance) error {
	if r.Repository != p.Trust.Repository || r.WorkflowCommit != p.Trust.WorkflowCommit || r.Bundle.SHA256 != p.Trust.BundleSHA256 || r.Trigger.RunID != p.RunID || r.Trigger.Attempt != p.Attempt {
		return fmt.Errorf("manifest does not match authenticated provenance")
	}
	return nil
}
func rejectSynthetic(e Evaluation) error {
	// #381 currently ships native-read-v1-empty. No production classifier
	// authorizes a retry; a supplied classification alone cannot enable one.
	for _, c := range e.Aggregate.Cases {
		if c.AttemptCount > 1 {
			return fmt.Errorf("production retry is not enabled by native-read-v1-empty: %s", c.Name)
		}
	}
	for _, b := range e.Report.Cases {
		var row struct {
			Fixture bool `json:"fixture_only"`
		}
		if err := json.Unmarshal(b, &row); err != nil {
			return err
		}
		if row.Fixture {
			return fmt.Errorf("synthetic fixture is not native acceptance evidence")
		}
	}
	return nil
}

// VerifyImported is called before the lane merges main. It reauthenticates the
// artifact and replays its immutable archive, so editing local manifests and
// recomputing their hashes cannot manufacture an imported pass.
func VerifyImported(root, repo string, trust Trust, api API) (Evaluation, error) {
	b, err := os.ReadFile(filepath.Join(root, "result.json"))
	if err != nil {
		return Evaluation{}, err
	}
	var report Report
	if err = Decode(b, &report); err != nil {
		return Evaluation{}, err
	}
	p := report.Remote.Import
	if p == nil || p.Trust != trust {
		return Evaluation{}, fmt.Errorf("remote evidence requires an authenticated import matching local trust pins")
	}
	d, err := Authenticate(api, *p)
	if err != nil {
		return Evaluation{}, err
	}
	archive := filepath.Join(filepath.Dir(root), "actions-artifact.zip")
	if err = verifyArchive(archive, d); err != nil {
		return Evaluation{}, err
	}
	tmp, err := os.MkdirTemp("", "remote-evidence-")
	if err != nil {
		return Evaluation{}, err
	}
	defer os.RemoveAll(tmp)
	if err = extract(archive, tmp); err != nil {
		return Evaluation{}, err
	}
	e, err := Verify(tmp, report.Remote.Aggregate)
	if err != nil {
		return e, err
	}
	if err = matchProvenance(e.Run, *p); err != nil {
		return e, err
	}
	e.Report.Remote.Import = p
	if !ReportsEqual(report, e.Report) {
		return e, fmt.Errorf("imported result differs from authenticated archive")
	}
	local, err := Verify(root, report.Remote.Aggregate)
	if err != nil {
		return e, err
	}
	if !reflect.DeepEqual(local.Aggregate, e.Aggregate) {
		return e, fmt.Errorf("local evidence changed")
	}
	if err = rejectSynthetic(e); err != nil {
		return e, err
	}
	if !e.Report.Passed {
		return e, fmt.Errorf("remote acceptance did not pass")
	}
	if err = VerifySelectionSource(repo, e.Run, e.Selection); err != nil {
		return e, err
	}
	return e, VerifySource(repo, e.Run)
}

// ReportsEqual ignores indentation of preserved native JSON, not its values.
func ReportsEqual(a, b Report) bool {
	if len(a.Cases) != len(b.Cases) {
		return false
	}
	for i := range a.Cases {
		var x, y bytes.Buffer
		if json.Compact(&x, a.Cases[i]) != nil || json.Compact(&y, b.Cases[i]) != nil || !bytes.Equal(x.Bytes(), y.Bytes()) {
			return false
		}
	}
	a.Cases = nil
	b.Cases = nil
	return reflect.DeepEqual(a, b)
}
