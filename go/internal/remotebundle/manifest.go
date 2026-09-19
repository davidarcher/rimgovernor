// Package remotebundle implements the private remote-acceptance bundle boundary.
// Only encrypted archive parts belong in a shared cache.
package remotebundle

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path"
	"path/filepath"
	"reflect"
	"regexp"
	"strings"
)

type Reference struct {
	Path   string `json:"path"`
	SHA256 string `json:"sha256"`
}
type File struct {
	Path   string `json:"path"`
	Bytes  int64  `json:"bytes"`
	SHA256 string `json:"sha256"`
}
type Inventory struct {
	SchemaVersion int    `json:"schema_version"`
	Files         []File `json:"files"`
}
type Component struct {
	Name       string `json:"name"`
	Version    string `json:"version"`
	PathPrefix string `json:"path_prefix"`
	SHA256     string `json:"sha256"`
}
type Part struct {
	AssetID int64  `json:"asset_id"`
	Name    string `json:"name"`
	Bytes   int64  `json:"bytes"`
	SHA256  string `json:"sha256"`
}
type Origin struct {
	Repository string `json:"repository"`
	ReleaseID  int64  `json:"release_id"`
}
type Game struct {
	Version  string `json:"version"`
	Platform string `json:"platform"`
	CoreOnly bool   `json:"core_only"`
}
type Encryption struct {
	Format string `json:"format"`
	KeyID  string `json:"key_id"`
}
type Manifest struct {
	SchemaVersion int         `json:"schema_version"`
	Game          Game        `json:"game"`
	Components    []Component `json:"components"`
	Origin        Origin      `json:"origin"`
	Parts         []Part      `json:"parts"`
	UnpackedBytes int64       `json:"unpacked_bytes"`
	Inventory     Reference   `json:"inventory"`
	Encryption    Encryption  `json:"encryption"`
}

var digestPattern = regexp.MustCompile(`^[0-9a-f]{64}$`)
var repositoryPattern = regexp.MustCompile(`^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$`)

func Digest(b []byte) string { d := sha256.Sum256(b); return hex.EncodeToString(d[:]) }

// SafePath rejects Windows aliases as well as traversal, even on non-Windows hosts.
func SafePath(p string) error {
	if p == "" || path.Clean(p) != p || strings.HasPrefix(p, "/") || strings.ContainsAny(p, "\\:\x00\r\n\t*?\"<>|") {
		return fmt.Errorf("unsafe bundle path %q", p)
	}
	for _, s := range strings.Split(p, "/") {
		if s == "." || s == ".." || strings.TrimRight(s, " .") != s {
			return fmt.Errorf("unsafe bundle path %q", p)
		}
		base := strings.ToUpper(strings.SplitN(s, ".", 2)[0])
		if base == "CON" || base == "PRN" || base == "AUX" || base == "NUL" || regexp.MustCompile(`^(COM|LPT)[0-9]$`).MatchString(base) {
			return fmt.Errorf("reserved Windows path %q", p)
		}
	}
	return nil
}

// Decode rejects duplicate keys and missing fields, including explicit nulls.
// Optional future fields remain allowed by the versioned contract.
// Reflection is confined to this JSON boundary to check required-field presence;
// decoded manifests and all bundle operations use concrete types.
func Decode(data []byte, out any) error {
	d := json.NewDecoder(bytes.NewReader(data))
	if err := uniqueValue(d); err != nil {
		return err
	}
	if _, err := d.Token(); err != io.EOF {
		return fmt.Errorf("trailing JSON data")
	}
	if err := requiredFields(data, reflect.TypeOf(out).Elem()); err != nil {
		return err
	}
	return json.Unmarshal(data, out)
}

func requiredFields(data []byte, t reflect.Type) error {
	if t.Kind() == reflect.Struct {
		var obj map[string]json.RawMessage
		if err := json.Unmarshal(data, &obj); err != nil {
			return err
		}
		for i := 0; i < t.NumField(); i++ {
			f := t.Field(i)
			name := strings.Split(f.Tag.Get("json"), ",")[0]
			if name == "" {
				name = f.Name
			}
			if name == "-" {
				continue
			}
			v, ok := obj[name]
			if !ok {
				return fmt.Errorf("missing required field %s", name)
			}
			if err := requiredFields(v, f.Type); err != nil {
				return fmt.Errorf("%s: %w", name, err)
			}
		}
	} else if t.Kind() == reflect.Slice {
		var rows []json.RawMessage
		if err := json.Unmarshal(data, &rows); err != nil {
			return err
		}
		for _, v := range rows {
			if err := requiredFields(v, t.Elem()); err != nil {
				return err
			}
		}
	}
	return nil
}

func uniqueValue(d *json.Decoder) error {
	t, err := d.Token()
	if err != nil {
		return err
	}
	if t == nil {
		return fmt.Errorf("null bundle field")
	}
	delim, ok := t.(json.Delim)
	if !ok {
		return nil
	}
	if delim == '{' {
		seen := map[string]bool{}
		for d.More() {
			k, err := d.Token()
			if err != nil {
				return err
			}
			key, ok := k.(string)
			if !ok || seen[key] {
				return fmt.Errorf("duplicate or invalid JSON key %v", k)
			}
			seen[key] = true
			if err := uniqueValue(d); err != nil {
				return err
			}
		}
	} else if delim == '[' {
		for d.More() {
			if err := uniqueValue(d); err != nil {
				return err
			}
		}
	} else {
		return fmt.Errorf("unexpected JSON delimiter")
	}
	_, err = d.Token()
	return err
}

func ReadManifest(filename, expected string) (Manifest, error) {
	return readManifest(filename, expected, true)
}

// ReadLocalDraft permits missing release asset IDs only for offline verification
// before publication. Bootstrap always calls ReadManifest.
func ReadLocalDraft(filename, expected string) (Manifest, error) {
	return readManifest(filename, expected, false)
}

func readManifest(filename, expected string, published bool) (Manifest, error) {
	var m Manifest
	b, err := os.ReadFile(filename)
	if err != nil {
		return m, err
	}
	if !digestPattern.MatchString(expected) || Digest(b) != expected {
		return m, fmt.Errorf("bundle manifest digest mismatch")
	}
	if err := Decode(b, &m); err != nil {
		return m, err
	}
	return m, m.Validate(published)
}

func (m Manifest) Validate(published bool) error {
	if m.SchemaVersion != 1 || m.Game.Version == "" || m.Game.Platform != "windows-x64" || !m.Game.CoreOnly {
		return fmt.Errorf("bundle requires schema 1, exact game version and Core-only windows-x64")
	}
	if !repositoryPattern.MatchString(m.Origin.Repository) || m.Origin.ReleaseID <= 0 {
		return fmt.Errorf("bundle requires pinned origin repository and release ID")
	}
	if m.Encryption.Format != "age-v1" || m.Encryption.KeyID == "" {
		return fmt.Errorf("bundle requires age-v1 encryption and key_id")
	}
	if m.UnpackedBytes < 0 || m.UnpackedBytes > 1<<40 || SafePath(m.Inventory.Path) != nil || !digestPattern.MatchString(m.Inventory.SHA256) {
		return fmt.Errorf("invalid inventory reference or unpacked size")
	}
	if len(m.Parts) == 0 || len(m.Parts) > 1024 || len(m.Components) == 0 {
		return fmt.Errorf("empty parts or components")
	}
	names, ids := map[string]bool{}, map[int64]bool{}
	for _, p := range m.Parts {
		key := strings.ToLower(p.Name)
		if SafePath(p.Name) != nil || strings.Contains(p.Name, "/") || p.Bytes <= 0 || p.Bytes > 1900000000 || !digestPattern.MatchString(p.SHA256) || names[key] || (published && (p.AssetID <= 0 || ids[p.AssetID])) {
			return fmt.Errorf("invalid or duplicate archive part %q", p.Name)
		}
		names[key], ids[p.AssetID] = true, true
	}
	names = map[string]bool{}
	for i, c := range m.Components {
		if c.Name == "" || c.Version == "" || names[c.Name] || !strings.HasSuffix(c.PathPrefix, "/") || SafePath(strings.TrimSuffix(c.PathPrefix, "/")) != nil || !digestPattern.MatchString(c.SHA256) {
			return fmt.Errorf("invalid component %q", c.Name)
		}
		names[c.Name] = true
		for _, other := range m.Components[:i] {
			a, b := strings.ToLower(c.PathPrefix), strings.ToLower(other.PathPrefix)
			if strings.HasPrefix(a, b) || strings.HasPrefix(b, a) {
				return fmt.Errorf("overlapping component prefixes")
			}
		}
	}
	return nil
}

func ReadInventory(root string, m Manifest) (Inventory, error) {
	var inv Inventory
	filename := filepath.Join(root, filepath.FromSlash(m.Inventory.Path))
	resolved, err := resolveSource(filename)
	if err != nil {
		return inv, err
	}
	resolvedRoot, err := resolveSource(root)
	if err != nil {
		return inv, err
	}
	if !within(resolvedRoot, resolved) {
		return inv, fmt.Errorf("inventory reference escapes evidence root")
	}
	b, err := os.ReadFile(filename)
	if err != nil {
		return inv, err
	}
	if Digest(b) != m.Inventory.SHA256 {
		return inv, fmt.Errorf("inventory digest mismatch")
	}
	if err := Decode(b, &inv); err != nil {
		return inv, err
	}
	return inv, ValidateInventory(inv, m)
}

func ValidateInventory(inv Inventory, m Manifest) error {
	if inv.SchemaVersion != 1 || inv.Files == nil {
		return fmt.Errorf("inventory requires schema 1 and files")
	}
	seen := map[string]bool{}
	previous := ""
	var total int64
	componentLines := make([]strings.Builder, len(m.Components))
	for _, f := range inv.Files {
		if SafePath(f.Path) != nil || f.Path <= previous || seen[strings.ToLower(f.Path)] || f.Bytes < 0 || !digestPattern.MatchString(f.SHA256) {
			return fmt.Errorf("invalid, unsorted or duplicate inventory file %q", f.Path)
		}
		seen[strings.ToLower(f.Path)] = true
		previous = f.Path
		if f.Bytes > m.UnpackedBytes-total {
			return fmt.Errorf("inventory size exceeds manifest")
		}
		total += f.Bytes
		owner := -1
		for i, c := range m.Components {
			if strings.HasPrefix(f.Path, c.PathPrefix) {
				owner = i
				break
			}
		}
		if owner < 0 {
			return fmt.Errorf("file %s has no component", f.Path)
		}
		fmt.Fprintf(&componentLines[owner], "%s\t%d\t%s\n", f.Path, f.Bytes, f.SHA256)
	}
	for _, f := range inv.Files {
		for p := path.Dir(f.Path); p != "."; p = path.Dir(p) {
			if seen[strings.ToLower(p)] {
				return fmt.Errorf("file/directory collision %s", p)
			}
		}
	}
	if total != m.UnpackedBytes {
		return fmt.Errorf("unpacked size mismatch")
	}
	for i, c := range m.Components {
		if Digest([]byte(componentLines[i].String())) != c.SHA256 {
			return fmt.Errorf("component %s inventory digest mismatch", c.Name)
		}
	}
	return nil
}

func HashFile(filename string) (string, int64, error) {
	f, err := os.Open(filename)
	if err != nil {
		return "", 0, err
	}
	defer f.Close()
	h := sha256.New()
	n, err := io.Copy(h, f)
	return hex.EncodeToString(h.Sum(nil)), n, err
}

func VerifyFile(filename string, size int64, digest string) error {
	i, err := os.Lstat(filename)
	if err != nil {
		return err
	}
	if !i.Mode().IsRegular() || i.Size() != size {
		return fmt.Errorf("%s: not a regular file of expected size %d", filename, size)
	}
	got, _, err := HashFile(filename)
	if err != nil {
		return err
	}
	if got != digest {
		return fmt.Errorf("%s: SHA-256 mismatch", filename)
	}
	return nil
}

// VerifyTree refuses links and extra files, including a profile accidentally archived.
func VerifyTree(root string, inv Inventory) error {
	want := map[string]File{}
	for _, f := range inv.Files {
		want[f.Path] = f
	}
	err := filepath.WalkDir(root, func(p string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		info, err := d.Info()
		if err != nil {
			return err
		}
		if info.Mode()&(os.ModeSymlink|os.ModeIrregular) != 0 {
			return fmt.Errorf("archive link/reparse point %s", p)
		}
		if d.IsDir() {
			return nil
		}
		rel, err := filepath.Rel(root, p)
		if err != nil {
			return err
		}
		rel = filepath.ToSlash(rel)
		f, ok := want[rel]
		if !ok {
			return fmt.Errorf("unlisted archive file %s", rel)
		}
		if err := VerifyFile(p, f.Bytes, f.SHA256); err != nil {
			return err
		}
		delete(want, rel)
		return nil
	})
	if err != nil {
		return err
	}
	if len(want) > 0 {
		return fmt.Errorf("archive missing %d inventory files", len(want))
	}
	return nil
}

func WriteJSON(filename string, value any) error {
	b, err := json.MarshalIndent(value, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(filename, append(b, '\n'), 0600)
}
