package remoteaccept

// Export is the public-artifact boundary. Only suite diagnostics are copied;
// worker layouts, profiles, saves and bootstrap inputs never enter the tree.
import (
	"bytes"
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
	if ext != ".json" && ext != ".jsonl" && ext != ".log" && ext != ".txt" && ext != ".md" {
		return fmt.Errorf("non-diagnostic file %s", p)
	}
	if bytes.IndexByte(b, 0) >= 0 {
		return fmt.Errorf("binary diagnostic %s", p)
	}
	for _, marker := range []string{"AGE-SECRET-KEY-", "-----BEGIN PRIVATE KEY", "-----BEGIN OPENSSH PRIVATE KEY", "<savegame", "<savedgame", "UnityFS"} {
		if bytes.Contains(bytes.ToLower(b), bytes.ToLower([]byte(marker))) {
			return fmt.Errorf("restricted content in %s", p)
		}
	}
	return nil
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
			b, err := os.ReadFile(filepath.Join(e.source, p))
			if err != nil {
				return nil, err
			}
			if hash(b) != d {
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
	b, err := os.ReadFile(filepath.Join(e.source, p))
	if err != nil {
		return Ref{}, err
	}
	if err = publicDiagnostic(p, b); err != nil {
		return Ref{}, err
	}
	if strings.EqualFold(filepath.Ext(p), ".json") || strings.EqualFold(filepath.Ext(p), ".jsonl") {
		check := json.NewDecoder(bytes.NewReader(b))
		for {
			if err := unique(check); err == io.EOF {
				break
			} else if err != nil {
				return Ref{}, err
			}
		}
		d := json.NewDecoder(bytes.NewReader(b))
		d.UseNumber()
		var out bytes.Buffer
		for {
			var v any
			err = d.Decode(&v)
			if err == io.EOF {
				break
			}
			if err != nil {
				return Ref{}, err
			}
			v, err = e.value(v)
			if err != nil {
				return Ref{}, err
			}
			if err = json.NewEncoder(&out).Encode(v); err != nil {
				return Ref{}, err
			}
		}
		b = out.Bytes()
	} else if strings.EqualFold(filepath.Ext(p), ".png") {
		// Re-encode generated frames to strip metadata and trailing payloads.
		frame, err := png.Decode(bytes.NewReader(b))
		if err != nil {
			return Ref{}, err
		}
		var out bytes.Buffer
		if err := png.Encode(&out, frame); err != nil {
			return Ref{}, err
		}
		b = out.Bytes()
	} else {
		b = []byte(e.clean(string(b)))
	}
	if err = e.write(target, b); err != nil {
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
		if (job.Role != "fixture" && job.Role != "production") || roles[job.Role] {
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
