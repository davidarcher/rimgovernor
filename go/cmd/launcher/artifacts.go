package main

import (
	"archive/zip"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"io/fs"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// Artifact states the status panel shows.
const (
	StateOK       = "ok"
	StateStale    = "stale"
	StateMissing  = "missing"
	StateBuilding = "building"
	StateFailed   = "failed"
	StatePending  = "pending"
)

// GABS release the bridge root pins (setup.GABSRelative names its path).
const (
	gabsURL    = "https://github.com/pardeike/GABS/releases/download/v1.1.1/gabs-v1.1.1-windows-amd64.zip"
	gabsSHA256 = "ad555373c9065931b58c736677e1660ece17aa19e95aaf7c1af01348fcecf861"
)

// ModState is the installed production mod's state against the
// worktree's native source hash.
func ModState(m *na.PackageManifest, sourceTree string) (state, reason string) {
	switch {
	case m == nil:
		return StateMissing, "no installed build"
	case m.SourceTree != sourceTree:
		return StateStale, "installed build's sources differ from the worktree"
	case len(m.Fixtures) > 0:
		return StateStale, "installed build is a fixture build"
	}
	return StateOK, "installed build is current"
}

// dashboardInputs are the dashboard files a build reads, relative to
// dashboard/; directories count by their newest file.
var dashboardInputs = []string{"src", "public", "index.html", "timeline.html", "package.json", "pnpm-lock.yaml", "vite.config.ts", "tsconfig.json"}

// DashboardState compares dist/index.html with the newest input.
func DashboardState(dashboard string) (state, reason string) {
	built, err := os.Stat(filepath.Join(dashboard, "dist", "index.html"))
	if err != nil {
		return StateMissing, "dist/index.html is missing"
	}
	newest, name := newestMTime(dashboard, dashboardInputs)
	if newest.After(built.ModTime()) {
		return StateStale, name + " is newer than the build"
	}
	return StateOK, "built after its sources"
}

// newestMTime is the newest modification time under the named paths of
// root, and the path that holds it.
func newestMTime(root string, names []string) (time.Time, string) {
	var newest time.Time
	var at string
	for _, n := range names {
		filepath.WalkDir(filepath.Join(root, n), func(path string, d fs.DirEntry, err error) error {
			if err != nil || d.IsDir() {
				return nil
			}
			if info, err := d.Info(); err == nil && info.ModTime().After(newest) {
				newest, at = info.ModTime(), path
			}
			return nil
		})
	}
	if rel, err := filepath.Rel(root, at); err == nil {
		at = filepath.ToSlash(rel)
	}
	return newest, at
}

// DownloadGABS fetches the pinned release zip, verifies its SHA-256 and
// unpacks it into dir (the bridge root's gabs/ directory).
func DownloadGABS(dir string, log io.Writer) error {
	fmt.Fprintf(log, "downloading %s\n", gabsURL)
	resp, err := (&http.Client{Timeout: 5 * time.Minute}).Get(gabsURL)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("GET %s: %s", gabsURL, resp.Status)
	}
	data, err := io.ReadAll(io.LimitReader(resp.Body, 64<<20))
	if err != nil {
		return err
	}
	return unpackGABS(data, dir)
}

// unpackGABS verifies data against gabsSHA256 and extracts it under dir.
func unpackGABS(data []byte, dir string) error {
	sum := sha256.Sum256(data)
	if got := hex.EncodeToString(sum[:]); got != gabsSHA256 {
		return fmt.Errorf("GABS download has SHA-256 %s, want %s; refusing it", got, gabsSHA256)
	}
	zr, err := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if err != nil {
		return err
	}
	for _, f := range zr.File {
		target := filepath.Join(dir, filepath.FromSlash(f.Name))
		if !strings.HasPrefix(target, filepath.Clean(dir)+string(filepath.Separator)) {
			return fmt.Errorf("zip entry %s escapes %s", f.Name, dir)
		}
		if f.FileInfo().IsDir() {
			if err := os.MkdirAll(target, 0755); err != nil {
				return err
			}
			continue
		}
		if err := os.MkdirAll(filepath.Dir(target), 0755); err != nil {
			return err
		}
		if err := extract(f, target); err != nil {
			return err
		}
	}
	return nil
}

func extract(f *zip.File, target string) error {
	r, err := f.Open()
	if err != nil {
		return err
	}
	defer r.Close()
	out, err := os.Create(target)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, r); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}
