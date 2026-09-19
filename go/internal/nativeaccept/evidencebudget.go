package nativeaccept

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

// The evidence budget (#302): a row's payload (a native reply envelope
// under "result", a service response under "response") is kept inline up
// to EvidencePayloadCap and past it replaced by its preview, size and
// hash; the untouched row survives only under EvidenceFull, in
// <output>/full/. Finalize's artifact manifest hashes the capped rows and
// the files the report names, never the flight ring, service database or
// logs.

// EvidenceEnv overrides the evidence mode ("capped" or "full") for a
// process whose runner sets no -evidence flag.
const EvidenceEnv = "RIMGOVERNOR_ACCEPT_EVIDENCE"

// EvidenceMode is how much of each payload the evidence files keep.
type EvidenceMode string

const (
	// EvidenceCapped keeps a payload up to EvidencePayloadCap bytes and
	// marks the row truncated past it; the full payload is dropped.
	EvidenceCapped EvidenceMode = "capped"
	// EvidenceFull also writes every row that overflowed the cap, untouched,
	// to <output>/full/<same file name>.
	EvidenceFull EvidenceMode = "full"
)

// EvidencePayloadCap is the largest payload an evidence row keeps inline:
// the flight recorder's payload cap, so a capped row and the flight row for
// the same call agree on what survived.
const EvidencePayloadCap = 256 << 10

// FullEvidenceDir is the directory under an output where EvidenceFull
// writes the overflowing rows.
const FullEvidenceDir = "full"

// evidencePayloadKeys are the row keys writeEvidence caps.
var evidencePayloadKeys = []string{"result", "response"}

var evidenceMode struct {
	sync.Mutex
	mode EvidenceMode
}

// SetEvidenceMode sets the process-wide evidence mode; "" restores the
// default (EvidenceEnv, else capped).
func SetEvidenceMode(mode EvidenceMode) error {
	switch mode {
	case "", EvidenceCapped, EvidenceFull:
	default:
		return fmt.Errorf("evidence mode must be %q or %q, got %q", EvidenceCapped, EvidenceFull, mode)
	}
	evidenceMode.Lock()
	evidenceMode.mode = mode
	evidenceMode.Unlock()
	return nil
}

// CurrentEvidenceMode is the mode SetEvidenceMode chose, else EvidenceEnv
// when it names a valid mode, else EvidenceCapped.
func CurrentEvidenceMode() EvidenceMode {
	evidenceMode.Lock()
	mode := evidenceMode.mode
	evidenceMode.Unlock()
	if mode != "" {
		return mode
	}
	if v := EvidenceMode(os.Getenv(EvidenceEnv)); v == EvidenceCapped || v == EvidenceFull {
		return v
	}
	return EvidenceCapped
}

// capEvidenceRow returns row with every evidencePayloadKeys payload
// (json.RawMessage or []byte) over EvidencePayloadCap replaced by
// "<key>_preview" (its first EvidencePayloadCap bytes as a string, since
// the cut is not valid JSON), "<key>_bytes" and "<key>_sha256", plus
// "truncated": true, and whether anything was cut. An uncut row is
// returned as is.
func capEvidenceRow(row map[string]any) (map[string]any, bool) {
	var capped map[string]any
	for _, key := range evidencePayloadKeys {
		var payload []byte
		switch v := row[key].(type) {
		case json.RawMessage:
			payload = v
		case []byte:
			payload = v
		default:
			continue
		}
		if len(payload) <= EvidencePayloadCap {
			continue
		}
		if capped == nil {
			capped = make(map[string]any, len(row)+4)
			for k, v := range row {
				capped[k] = v
			}
			capped["truncated"] = true
		}
		sum := sha256.Sum256(payload)
		delete(capped, key)
		capped[key+"_preview"] = string(payload[:EvidencePayloadCap])
		capped[key+"_bytes"] = len(payload)
		capped[key+"_sha256"] = hex.EncodeToString(sum[:])
	}
	if capped == nil {
		return row, false
	}
	return capped, true
}

// writeEvidence writes one evidence row to path, capped (capEvidenceRow);
// under EvidenceFull a cut row is also written untouched to
// <dir>/full/<name>.
func writeEvidence(path string, row map[string]any) {
	capped, cut := capEvidenceRow(row)
	if cut && CurrentEvidenceMode() == EvidenceFull {
		fullDir := filepath.Join(filepath.Dir(path), FullEvidenceDir)
		if err := os.MkdirAll(fullDir, 0755); err == nil {
			writeJSONFile(filepath.Join(fullDir, filepath.Base(path)), row)
		}
	}
	writeJSONFile(path, capped)
}

func writeJSONFile(path string, v any) {
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return
	}
	_ = os.WriteFile(path, data, 0644)
}

// isEvidenceFile reports whether relative (slash-separated, relative to a
// case or suite output) is a capped evidence file: a .json file outside
// FullEvidenceDir.
func isEvidenceFile(relative string) bool {
	if !strings.HasSuffix(relative, ".json") {
		return false
	}
	for _, part := range strings.Split(relative, "/") {
		if part == FullEvidenceDir {
			return false
		}
	}
	return true
}

// referencedFiles returns the slash-separated paths of the files under
// output that the report's string values name, relative to output or
// absolute.
func referencedFiles(report Report, output string) map[string]bool {
	found := map[string]bool{}
	absOutput, err := filepath.Abs(output)
	if err != nil {
		return found
	}
	var walk func(v any)
	walk = func(v any) {
		switch t := v.(type) {
		case string:
			if t == "" || strings.ContainsAny(t, "\n\"{}") {
				return
			}
			candidate := t
			if !filepath.IsAbs(candidate) {
				candidate = filepath.Join(absOutput, filepath.FromSlash(candidate))
			}
			relative, err := filepath.Rel(absOutput, candidate)
			if err != nil || strings.HasPrefix(relative, "..") {
				return
			}
			if info, err := os.Stat(candidate); err == nil && !info.IsDir() {
				found[filepath.ToSlash(relative)] = true
			}
		case map[string]any:
			for _, e := range t {
				walk(e)
			}
		case []any:
			for _, e := range t {
				walk(e)
			}
		}
	}
	walk(map[string]any(report))
	return found
}

// Artifacts hashes (sha256, keyed by slash-separated path relative to
// output) every capped evidence file under output and every file the
// report names; other files (flight segments, service databases, logs,
// saves, the full rows) are skipped without being read.
func Artifacts(output string, report Report) (map[string]string, error) {
	hashes := map[string]string{}
	referenced := referencedFiles(report, output)
	err := filepath.WalkDir(output, func(path string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if entry.IsDir() {
			return nil
		}
		relative, err := filepath.Rel(output, path)
		if err != nil {
			return err
		}
		relative = filepath.ToSlash(relative)
		if !isEvidenceFile(relative) && !referenced[relative] {
			return nil
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		sum := sha256.Sum256(data)
		hashes[relative] = hex.EncodeToString(sum[:])
		return nil
	})
	return hashes, err
}

// ArtifactHashes is Artifacts for a report that names no files.
func ArtifactHashes(output string) (map[string]string, error) {
	return Artifacts(output, nil)
}
