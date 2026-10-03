package remoteaccept

// Export is the public-artifact boundary. Only suite diagnostics are copied;
// worker layouts, profiles, saves and bootstrap inputs never enter the tree.
import (
	"bufio"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"image/png"
	"io"
	"io/fs"
	"math"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strings"
)

type ExportJob struct {
	Role      string `json:"role"`
	Output    string `json:"output"`
	Bootstrap string `json:"bootstrap"`
}

type exporter struct {
	dest, prefix, source string
	remaining            *int64
	files                map[string]string
	active               map[string]bool
	secrets              []string
}

var runnerPath = regexp.MustCompile(`(?i)[a-z]:[\\/][^\s"<>]*`)
var workerRoot = regexp.MustCompile(`^workers/[0-9]+$`)

func (e *exporter) clean(s string) string {
	for _, secret := range e.secrets {
		if secret != "" {
			s = strings.ReplaceAll(s, secret, "[redacted]")
		}
	}
	// Paths within the diagnostic tree remain navigable; other runner paths
	// disclose no useful portable identity.
	s = strings.ReplaceAll(s, e.source+string(filepath.Separator), e.prefix+"/")
	s = strings.ReplaceAll(s, filepath.ToSlash(e.source)+"/", e.prefix+"/")
	if s == e.source {
		return e.prefix
	}
	s = runnerPath.ReplaceAllString(s, "[runner-path]")
	return strings.ReplaceAll(s, "\\", "/")
}

// SnapshotDir is the job-output subdirectory a shard records its colony
// snapshot streams into (docs/developers/testing/colony-snapshots.md).
const SnapshotDir = "snapshots"

var restrictedMarkers = []string{"AGE-SECRET-KEY-", "-----BEGIN PRIVATE KEY", "-----BEGIN OPENSSH PRIVATE KEY", "<savegame", "<savedgame", "UnityFS"}

func publicExt(p string) error {
	switch strings.ToLower(filepath.Ext(p)) {
	case ".json", ".jsonl", ".log", ".txt", ".md":
		return nil
	}
	return fmt.Errorf("non-diagnostic file %s", p)
}

// publicText scans a text diagnostic in fixed chunks, so a multi-GB stream is
// checked without being held in memory. Markers spanning chunks are caught by
// carrying over the longest marker's tail.
func publicText(p string, r io.Reader) error {
	const overlap = 32
	buf := make([]byte, 1<<20)
	carry := 0
	for {
		n, err := r.Read(buf[carry:])
		if n > 0 {
			chunk := buf[:carry+n]
			if bytes.IndexByte(chunk[carry:], 0) >= 0 {
				return fmt.Errorf("binary diagnostic %s", p)
			}
			lower := bytes.ToLower(chunk)
			for _, marker := range restrictedMarkers {
				if bytes.Contains(lower, bytes.ToLower([]byte(marker))) {
					return fmt.Errorf("restricted content in %s", p)
				}
			}
			carry = min(overlap, len(chunk))
			copy(buf, chunk[len(chunk)-carry:])
		}
		if err == io.EOF {
			return nil
		}
		if err != nil {
			return err
		}
	}
}

func hashFile(p string) (string, error) {
	f, err := os.Open(p)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// capWriter fails a copy that would exceed the remaining allowance instead of
// truncating it.
type capWriter struct {
	w         io.Writer
	remaining *int64
}

func (c *capWriter) Write(b []byte) (int, error) {
	if int64(len(b)) > *c.remaining {
		return 0, fmt.Errorf("artifact allowance exhausted; required evidence is incomplete")
	}
	*c.remaining -= int64(len(b))
	return c.w.Write(b)
}

func publicDiagnostic(p string, b []byte) error {
	ext := strings.ToLower(filepath.Ext(p))
	if ext == ".png" {
		config, err := png.DecodeConfig(bytes.NewReader(b))
		if err != nil || config.Width < 1 || config.Height < 1 || config.Width > 4096 || config.Height > 4096 {
			return fmt.Errorf("invalid or oversized rendered PNG %s", p)
		}
		_, err = png.Decode(bytes.NewReader(b))
		return err
	}
	if err := publicExt(p); err != nil {
		return err
	}
	return publicText(p, bytes.NewReader(b))
}

func (e *exporter) write(p string, b []byte) error {
	if int64(len(b)) > *e.remaining {
		return fmt.Errorf("artifact allowance exhausted; required evidence is incomplete")
	}
	if err := publicDiagnostic(p, b); err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(filepath.Join(e.dest, p)), 0700); err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(e.dest, p), b, 0600); err != nil {
		return err
	}
	*e.remaining -= int64(len(b))
	return nil
}

func (e *exporter) value(v any) (any, error) {
	switch x := v.(type) {
	case string:
		return e.clean(x), nil
	case []any:
		for i := range x {
			y, err := e.value(x[i])
			if err != nil {
				return nil, err
			}
			x[i] = y
		}
		return x, nil
	case map[string]any:
		if len(x) == 2 && x["path"] != nil && x["sha256"] != nil {
			p, ok := x["path"].(string)
			if !ok || !validPath(p) || e.files[strings.ToLower(p)] != p {
				return nil, fmt.Errorf("unsafe diagnostic reference %q", p)
			}
			d, ok := x["sha256"].(string)
			if !ok {
				return nil, fmt.Errorf("invalid diagnostic digest")
			}
			got, err := hashFile(filepath.Join(e.source, p))
			if err != nil {
				return nil, err
			}
			if got != d {
				return nil, fmt.Errorf("source diagnostic digest mismatch: %s", p)
			}
			ref, err := e.copy(p)
			if err != nil {
				return nil, err
			}
			return ref, nil
		}
		for k, v := range x {
			y, err := e.value(v)
			if err != nil {
				return nil, err
			}
			x[k] = y
		}
		return x, nil
	}
	return v, nil
}

func (e *exporter) copy(p string) (Ref, error) {
	if !validPath(p) || e.files[strings.ToLower(p)] != p {
		return Ref{}, fmt.Errorf("missing/unsafe diagnostic %s", p)
	}
	target := e.prefix + "/" + p
	if e.active[p] {
		return Ref{}, fmt.Errorf("cyclic diagnostic reference %s", p)
	}
	if _, err := os.Stat(filepath.Join(e.dest, target)); err == nil {
		return FileRef(e.dest, target)
	}
	e.active[p] = true
	defer delete(e.active, p)
	info, err := os.Stat(filepath.Join(e.source, p))
	if err != nil {
		return Ref{}, err
	}
	if info.Size() > *e.remaining {
		return Ref{}, fmt.Errorf("artifact allowance exhausted at %s", p)
	}
	src := filepath.Join(e.source, p)
	ext := strings.ToLower(filepath.Ext(p))
	if ext == ".png" {
		b, err := os.ReadFile(src)
		if err != nil {
			return Ref{}, err
		}
		if err = publicDiagnostic(p, b); err != nil {
			return Ref{}, err
		}
		// Re-encode generated frames to strip metadata and trailing payloads.
		frame, err := png.Decode(bytes.NewReader(b))
		if err != nil {
			return Ref{}, err
		}
		var out bytes.Buffer
		if err := png.Encode(&out, frame); err != nil {
			return Ref{}, err
		}
		if err = e.write(target, out.Bytes()); err != nil {
			return Ref{}, err
		}
		return FileRef(e.dest, target)
	}
	// Text diagnostics stream: a multi-GB recording is scanned, rewritten and
	// hashed without being held in memory, and exhausting the allowance fails
	// the copy instead of dropping or truncating the file.
	if err = publicExt(p); err != nil {
		return Ref{}, err
	}
	f, err := os.Open(src)
	if err != nil {
		return Ref{}, err
	}
	defer f.Close()
	if err = publicText(p, f); err != nil {
		return Ref{}, err
	}
	dst := filepath.Join(e.dest, target)
	if err = os.MkdirAll(filepath.Dir(dst), 0700); err != nil {
		return Ref{}, err
	}
	tmp, err := os.CreateTemp(filepath.Dir(dst), ".export-*")
	if err != nil {
		return Ref{}, err
	}
	defer os.Remove(tmp.Name())
	out := bufio.NewWriterSize(&capWriter{w: tmp, remaining: e.remaining}, 1<<20)
	if _, err = f.Seek(0, io.SeekStart); err != nil {
		return Ref{}, err
	}
	if ext == ".json" || ext == ".jsonl" {
		check := json.NewDecoder(bufio.NewReaderSize(f, 1<<20))
		for {
			if err := unique(check); err == io.EOF {
				break
			} else if err != nil {
				return Ref{}, err
			}
		}
		if _, err = f.Seek(0, io.SeekStart); err != nil {
			return Ref{}, err
		}
		d := json.NewDecoder(bufio.NewReaderSize(f, 1<<20))
		d.UseNumber()
		enc := json.NewEncoder(out)
		for {
			var v any
			err = d.Decode(&v)
			if err == io.EOF {
				break
			}
			if err != nil {
				return Ref{}, err
			}
			if v, err = e.value(v); err != nil {
				return Ref{}, err
			}
			if err = enc.Encode(v); err != nil {
				return Ref{}, err
			}
		}
	} else {
		in := bufio.NewReaderSize(f, 1<<20)
		for {
			line, rerr := in.ReadString('\n')
			if _, err = out.WriteString(e.clean(line)); err != nil {
				return Ref{}, err
			}
			if rerr == io.EOF {
				break
			}
			if rerr != nil {
				return Ref{}, rerr
			}
		}
	}
	if err = out.Flush(); err != nil {
		return Ref{}, err
	}
	if err = tmp.Close(); err != nil {
		return Ref{}, err
	}
	if err = os.Rename(tmp.Name(), dst); err != nil {
		return Ref{}, err
	}
	return FileRef(e.dest, target)
}

// ExportShard writes a portable attempts manifest after all role suites finish.
// Missing attempts or diagnostics are errors, never synthesized passes. The
// partial tree remains useful to the always-run upload and incomplete verdict.
func ExportShard(root, shard string, jobs []ExportJob, secrets []string) error {
	if _, err := openTree(root); err != nil {
		return err
	}
	var run Run
	var selection Selection
	b, err := os.ReadFile(filepath.Join(root, "run.json"))
	if err != nil {
		return err
	}
	if err = Decode(b, &run); err != nil {
		return err
	}
	b, err = os.ReadFile(filepath.Join(root, "selection.json"))
	if err != nil {
		return err
	}
	if err = Decode(b, &selection); err != nil {
		return err
	}
	want := map[string]bool{}
	for _, s := range selection.Shards {
		if s.ID == shard {
			for _, c := range s.Cases {
				want[c] = true
			}
		}
	}
	if len(want) == 0 || !validPath(shard) || strings.Contains(shard, "/") {
		return fmt.Errorf("unknown shard")
	}
	if run.Limits.Bytes < 0 || (run.Limits.Bytes > 0 && run.Limits.Bytes <= 64<<20) {
		return fmt.Errorf("artifact allowance cannot accommodate plan and verdict")
	}
	// Zero disables the operator's raw-byte cap. It is not a GitHub quota:
	// GitHub stores compressed uploads, and public-repository usage differs
	// from the included private-repository storage allowance.
	remaining := int64(math.MaxInt64)
	if run.Limits.Bytes > 0 {
		remaining = (run.Limits.Bytes - (64 << 20)) / (2 * int64(len(selection.Shards))) // shard artifacts + final aggregate, with plan/manifest reserve
	}
	a := Attempts{Version: 1, ShardID: shard, Attempts: []Attempt{}}
	a.Run, err = FileRef(root, "run.json")
	if err != nil {
		return err
	}
	a.Selection, err = FileRef(root, "selection.json")
	if err != nil {
		return err
	}
	seen := map[string]bool{}
	roles := map[string]bool{}
	for _, job := range jobs {
		if job.Role != "fixture" || roles[job.Role] {
			return fmt.Errorf("invalid/duplicate role")
		}
		roles[job.Role] = true
		source, err := filepath.Abs(job.Output)
		if err != nil {
			return err
		}
		e := exporter{dest: root, prefix: shard + "/" + job.Role, source: source, remaining: &remaining, files: map[string]string{}, active: map[string]bool{}, secrets: secrets}
		err = filepath.WalkDir(source, func(p string, d fs.DirEntry, err error) error {
			if err != nil {
				return err
			}
			rel, _ := filepath.Rel(source, p)
			rel = filepath.ToSlash(rel)
			if d.IsDir() {
				// Numeric suite roots share the workers prefix with case diagnostics.
				if workerRoot.MatchString(rel) || rel == "checkpoints" || rel == "profile" {
					return filepath.SkipDir
				}
				return nil
			}
			resolved, err := filepath.EvalSymlinks(p)
			if err != nil {
				return err
			}
			if !strings.EqualFold(resolved, p) || d.Type()&os.ModeSymlink != 0 {
				return fmt.Errorf("linked diagnostic")
			}
			if !validPath(rel) {
				return fmt.Errorf("unsafe diagnostic path")
			}
			info, err := d.Info()
			if err != nil {
				return err
			}
			if !info.Mode().IsRegular() {
				return fmt.Errorf("nonregular diagnostic")
			}
			ext := strings.ToLower(filepath.Ext(rel))
			if ext == ".json" || ext == ".jsonl" || ext == ".log" || ext == ".txt" || ext == ".md" || ext == ".png" {
				if _, ok := e.files[strings.ToLower(rel)]; ok {
					return fmt.Errorf("case-colliding diagnostic")
				}
				e.files[strings.ToLower(rel)] = rel
			}
			return nil
		})
		if err != nil {
			return err
		}
		// Copy the verdict and its reference closure first, then optional diagnostics.
		if _, err = e.copy("result.json"); err != nil {
			return err
		}
		paths := []string{}
		for _, p := range e.files {
			paths = append(paths, p)
		}
		slices.Sort(paths)
		for _, p := range paths {
			// Colony snapshot streams (RIMGOVERNOR_SNAPSHOT_DIR=<output>/snapshots)
			// are optional recordings: under a raw-byte cap they yield to the
			// verdict's evidence rather than fail the shard.
			if strings.HasPrefix(p, SnapshotDir+"/") {
				if info, err := os.Stat(filepath.Join(source, p)); err == nil && info.Size() > remaining {
					continue
				}
			}
			if _, err = e.copy(p); err != nil {
				return err
			}
		}
		var suite struct {
			Cases []struct {
				Name     string    `json:"name"`
				Attempts []Attempt `json:"attempts"`
			} `json:"cases"`
		}
		b, err = os.ReadFile(filepath.Join(root, e.prefix, "result.json"))
		if err != nil {
			return err
		}
		if err = Decode(b, &suite); err != nil {
			return err
		}
		for _, row := range suite.Cases {
			if !want[row.Name] || seen[row.Name] || len(row.Attempts) == 0 {
				return fmt.Errorf("missing/duplicate/unplanned attempts for %s", row.Name)
			}
			seen[row.Name] = true
			a.Attempts = append(a.Attempts, row.Attempts...)
		}
		b, err = os.ReadFile(job.Bootstrap)
		if err != nil {
			return err
		}
		var boot struct {
			Passed      bool   `json:"passed"`
			OS          string `json:"os"`
			Image       string `json:"image_version"`
			CPUs        int    `json:"cpu_count"`
			Memory      int64  `json:"memory_bytes"`
			Disk        int64  `json:"free_disk_bytes"`
			CacheHit    *bool  `json:"cache_hit,omitempty"`
			RestoreMS   int64  `json:"restore_ms,omitempty"`
			ExtractMS   int64  `json:"extract_ms,omitempty"`
			BuildMS     int64  `json:"build_ms,omitempty"`
			TotalMS     int64  `json:"total_ms,omitempty"`
			ProvisionMS int64  `json:"provision_ms,omitempty"`
		}
		if err = Decode(b, &boot); err != nil {
			return err
		}
		if !boot.Passed {
			return fmt.Errorf("bootstrap failed")
		}
		// A projection intentionally excludes absolute paths and dependency metadata.
		b, err = json.Marshal(boot)
		if err != nil {
			return err
		}
		bp := e.prefix + "/bootstrap.json"
		if err = e.write(bp, b); err != nil {
			return err
		}
		if a.Runner.Bootstrap.Path == "" {
			a.Runner.OS = boot.OS
			a.Runner.Arch = "x64"
			a.Runner.Image = boot.Image
			a.Runner.CPUs = boot.CPUs
			a.Runner.Memory = boot.Memory
			a.Runner.Disk = boot.Disk
			a.Runner.Bootstrap, err = FileRef(root, bp)
			if err != nil {
				return err
			}
		}
	}
	if len(seen) != len(want) {
		return fmt.Errorf("shard coverage incomplete")
	}
	_, err = WriteJSON(root, shard+"/attempts.json", a)
	return err
}
