package remotebundle

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
)

type Asset struct {
	ID   int64  `json:"id"`
	Name string `json:"name"`
	Size int64  `json:"size"`
}
type Release struct {
	ID     int64   `json:"id"`
	Tag    string  `json:"tag_name"`
	Draft  bool    `json:"draft"`
	Assets []Asset `json:"assets"`
}

// OriginClient is the authenticated metadata/download boundary. gh owns token
// handling; tokens are never serialized or passed to child build processes.
type OriginClient interface {
	Release(context.Context, Origin) (Release, error)
	Download(context.Context, Origin, Part, io.Writer) error
}

type GitHub struct{}

func (GitHub) Release(ctx context.Context, o Origin) (Release, error) {
	var r Release
	if !repositoryPattern.MatchString(o.Repository) || o.ReleaseID <= 0 {
		return r, fmt.Errorf("invalid origin")
	}
	c := exec.CommandContext(ctx, "gh", "api", "repos/"+o.Repository)
	b, err := c.Output()
	if err != nil {
		return r, fmt.Errorf("read origin repository metadata: %w", err)
	}
	var repo struct {
		FullName string `json:"full_name"`
		Private  bool   `json:"private"`
	}
	if err := json.Unmarshal(b, &repo); err != nil {
		return r, err
	}
	if repo.FullName != o.Repository || repo.Private {
		return r, fmt.Errorf("origin must be the pinned public repository %s", o.Repository)
	}
	c = exec.CommandContext(ctx, "gh", "api", "repos/"+o.Repository+"/releases/"+strconv.FormatInt(o.ReleaseID, 10))
	b, err = c.Output()
	if err != nil {
		return r, fmt.Errorf("read pinned release: %w", err)
	}
	if err := json.Unmarshal(b, &r); err != nil {
		return r, err
	}
	if r.ID != o.ReleaseID || r.Draft {
		return r, fmt.Errorf("origin release is missing, draft or mismatched")
	}
	c = exec.CommandContext(ctx, "gh", "api", "--paginate", "--slurp", "repos/"+o.Repository+"/releases/"+strconv.FormatInt(o.ReleaseID, 10)+"/assets?per_page=100")
	b, err = c.Output()
	if err != nil {
		return r, fmt.Errorf("read release assets: %w", err)
	}
	var pages [][]Asset
	if err := json.Unmarshal(b, &pages); err != nil {
		return r, err
	}
	r.Assets = nil
	for _, page := range pages {
		r.Assets = append(r.Assets, page...)
	}
	return r, nil
}

func (GitHub) Download(ctx context.Context, o Origin, p Part, w io.Writer) error {
	c := exec.CommandContext(ctx, "gh", "api", "-H", "Accept: application/octet-stream", "repos/"+o.Repository+"/releases/assets/"+strconv.FormatInt(p.AssetID, 10))
	c.Stdout = w
	if err := c.Run(); err != nil {
		return fmt.Errorf("download asset %d: %w", p.AssetID, err)
	}
	return nil
}

func VerifyOrigin(ctx context.Context, c OriginClient, m Manifest) error {
	r, err := c.Release(ctx, m.Origin)
	if err != nil {
		return err
	}
	if r.ID != m.Origin.ReleaseID || r.Draft {
		return fmt.Errorf("release identity mismatch or unpublished release")
	}
	for _, p := range m.Parts {
		found := false
		for _, a := range r.Assets {
			if a.ID == p.AssetID && a.Name == p.Name && a.Size == p.Bytes {
				found = true
				break
			}
		}
		if !found {
			return fmt.Errorf("asset %d (%s) is not in pinned release with expected size", p.AssetID, p.Name)
		}
	}
	return nil
}

// Restore verifies cache hits too. A corrupt cache is an error, never a fallback.
// Only encrypted parts are written to this directory; manifest/inventory stay in
// the trusted input directory and plaintext goes to a separate per-job layout.
func Restore(ctx context.Context, c OriginClient, cache, digest string, m Manifest) (bool, error) {
	if !digestPattern.MatchString(digest) {
		return false, fmt.Errorf("invalid cache identity")
	}
	if err := VerifyOrigin(ctx, c, m); err != nil {
		return false, err
	}
	dir := filepath.Join(cache, CacheKey(digest))
	if _, err := os.Lstat(dir); err == nil {
		return true, VerifyParts(dir, m)
	} else if !os.IsNotExist(err) {
		return false, err
	}
	if err := os.MkdirAll(cache, 0700); err != nil {
		return false, err
	}
	tmp, err := os.MkdirTemp(cache, "download-")
	if err != nil {
		return false, err
	}
	defer os.RemoveAll(tmp)
	for _, p := range m.Parts {
		f, err := os.OpenFile(filepath.Join(tmp, p.Name), os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
		if err != nil {
			return false, err
		}
		err = c.Download(ctx, m.Origin, p, &limitedWriter{Writer: f, remaining: p.Bytes})
		ce := f.Close()
		if err != nil {
			return false, err
		}
		if ce != nil {
			return false, ce
		}
		if err := VerifyFile(filepath.Join(tmp, p.Name), p.Bytes, p.SHA256); err != nil {
			return false, err
		}
	}
	if err := VerifyParts(tmp, m); err != nil {
		return false, err
	}
	if err := os.Rename(tmp, dir); err != nil {
		return false, err
	}
	return false, nil
}

type limitedWriter struct {
	io.Writer
	remaining int64
}

func (w *limitedWriter) Write(p []byte) (int, error) {
	if int64(len(p)) > w.remaining {
		return 0, fmt.Errorf("asset exceeds declared size")
	}
	n, e := w.Writer.Write(p)
	w.remaining -= int64(n)
	return n, e
}

func CacheKey(digest string) string { return "rimgovernor-bundle-v1-windows-x64-" + digest }

// Publish is an explicit maintainer operation. Uploads never use --clobber; the
// final manifest is produced only after asset IDs are read back from the origin.
func Publish(ctx context.Context, dir string, m Manifest) (string, error) {
	if err := m.Validate(false); err != nil {
		return "", err
	}
	if err := VerifyParts(dir, m); err != nil {
		return "", err
	}
	if _, err := ReadInventory(dir, m); err != nil {
		return "", err
	}
	g := GitHub{}
	release, err := g.Release(ctx, m.Origin)
	if err != nil {
		return "", err
	}
	upload := func(file string) error {
		c := exec.CommandContext(ctx, "gh", "release", "upload", release.Tag, file, "--repo", m.Origin.Repository)
		if out, err := c.CombinedOutput(); err != nil {
			return fmt.Errorf("immutable asset upload: %w: %s", err, out)
		}
		return nil
	}
	for _, p := range m.Parts {
		if err := upload(filepath.Join(dir, p.Name)); err != nil {
			return "", err
		}
	}
	invName := "inventory-" + m.Inventory.SHA256 + ".json"
	if err := Materialize(filepath.Join(dir, m.Inventory.Path), filepath.Join(dir, invName)); err != nil {
		return "", err
	}
	m.Inventory.Path = invName
	if err := upload(filepath.Join(dir, invName)); err != nil {
		return "", err
	}
	release, err = g.Release(ctx, m.Origin)
	if err != nil {
		return "", err
	}
	for i, p := range m.Parts {
		for _, a := range release.Assets {
			if a.Name == p.Name && a.Size == p.Bytes {
				m.Parts[i].AssetID = a.ID
				break
			}
		}
	}
	if err := m.Validate(true); err != nil {
		return "", err
	}
	tmp := filepath.Join(dir, "bundle.published.json")
	if err := WriteJSON(tmp, m); err != nil {
		return "", err
	}
	digest, _, err := HashFile(tmp)
	if err != nil {
		return "", err
	}
	name := "bundle-" + digest + ".json"
	if err := os.Rename(tmp, filepath.Join(dir, name)); err != nil {
		return "", err
	}
	if err := upload(filepath.Join(dir, name)); err != nil {
		return "", err
	}
	return name, nil
}
