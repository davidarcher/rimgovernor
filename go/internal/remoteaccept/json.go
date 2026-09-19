package remoteaccept

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

var oid = regexp.MustCompile(`^[0-9a-f]{40}$`)
var digest = regexp.MustCompile(`^[0-9a-f]{64}$`)
var repository = regexp.MustCompile(`^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$`)

func hash(data []byte) string { sum := sha256.Sum256(data); return hex.EncodeToString(sum[:]) }

// Decode is the JSON boundary: duplicate keys (also case aliases), missing
// required fields, null nonnullable fields and trailing values fail closed.
// Reflection is limited to checking presence against concrete wire structs.
func Decode(data []byte, dst any) error {
	d := json.NewDecoder(bytes.NewReader(data))
	if err := unique(d); err != nil {
		return err
	}
	if _, err := d.Token(); err != io.EOF {
		return fmt.Errorf("trailing JSON data")
	}
	if err := required(data, reflect.TypeOf(dst).Elem()); err != nil {
		return err
	}
	return json.Unmarshal(data, dst)
}
func unique(d *json.Decoder) error {
	t, err := d.Token()
	if err != nil {
		return err
	}
	delim, ok := t.(json.Delim)
	if !ok {
		return nil
	}
	switch delim {
	case '{':
		seen := map[string]bool{}
		for d.More() {
			k, err := d.Token()
			if err != nil {
				return err
			}
			key, ok := k.(string)
			if !ok {
				return fmt.Errorf("invalid object key")
			}
			fold := strings.ToLower(key)
			if seen[fold] {
				return fmt.Errorf("duplicate JSON key %q", key)
			}
			seen[fold] = true
			if err := unique(d); err != nil {
				return err
			}
		}
	case '[':
		for d.More() {
			if err := unique(d); err != nil {
				return err
			}
		}
	default:
		return fmt.Errorf("unexpected JSON delimiter")
	}
	_, err = d.Token()
	return err
}
func required(data []byte, typ reflect.Type) error {
	if typ == reflect.TypeFor[json.RawMessage]() {
		return nil
	}
	if typ.Kind() == reflect.Pointer {
		if string(data) == "null" {
			return nil
		}
		return required(data, typ.Elem())
	}
	if string(data) == "null" {
		return fmt.Errorf("null %s", typ)
	}
	switch typ.Kind() {
	case reflect.Struct:
		var obj map[string]json.RawMessage
		if err := json.Unmarshal(data, &obj); err != nil {
			return err
		}
		for i := 0; i < typ.NumField(); i++ {
			f := typ.Field(i)
			tag := f.Tag.Get("json")
			name, opts, _ := strings.Cut(tag, ",")
			if name == "-" || name == "" {
				continue
			}
			v, ok := obj[name]
			if !ok {
				if strings.Contains(opts, "omitempty") {
					continue
				}
				return fmt.Errorf("missing %s", name)
			}
			if err := required(v, f.Type); err != nil {
				return fmt.Errorf("%s: %w", name, err)
			}
		}
	case reflect.Slice:
		var rows []json.RawMessage
		if err := json.Unmarshal(data, &rows); err != nil {
			return err
		}
		for _, row := range rows {
			if err := required(row, typ.Elem()); err != nil {
				return err
			}
		}
	}
	return nil
}

func validPath(p string) bool {
	if p == "" || p == "." || path.Clean(p) != p || strings.ContainsAny(p, "\\:\x00<>\"|?*") || strings.HasPrefix(p, "/") {
		return false
	}
	for _, r := range p {
		if r < 32 {
			return false
		}
	}
	for _, part := range strings.Split(p, "/") {
		if part == ".." || strings.HasSuffix(part, ".") || strings.HasSuffix(part, " ") {
			return false
		}
		stem := strings.ToUpper(strings.SplitN(part, ".", 2)[0])
		if stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || regexp.MustCompile(`^(COM|LPT)[0-9]$`).MatchString(stem) {
			return false
		}
	}
	return true
}

type tree struct {
	root  string
	files map[string]string
	used  map[string]bool
}

func openTree(root string) (*tree, error) {
	abs, err := filepath.Abs(root)
	if err != nil {
		return nil, err
	}
	t := &tree{abs, map[string]string{}, map[string]bool{}}
	err = filepath.WalkDir(abs, func(p string, d os.DirEntry, e error) error {
		if e != nil {
			return e
		}
		if d.Type()&os.ModeSymlink != 0 {
			return fmt.Errorf("linked evidence: %s", p)
		}
		resolved, e := filepath.EvalSymlinks(p)
		if e != nil {
			return e
		}
		if !strings.EqualFold(filepath.Clean(resolved), filepath.Clean(p)) {
			return fmt.Errorf("redirected evidence: %s", p)
		}
		if p == abs {
			return nil
		}
		rel, e := filepath.Rel(abs, p)
		if e != nil {
			return e
		}
		rel = filepath.ToSlash(rel)
		if !validPath(rel) {
			return fmt.Errorf("unsafe path %q", rel)
		}
		key := strings.ToLower(rel)
		if _, ok := t.files[key]; ok {
			return fmt.Errorf("case-colliding path %s", rel)
		}
		t.files[key] = rel
		if !d.IsDir() {
			info, e := d.Info()
			if e != nil {
				return e
			}
			if !info.Mode().IsRegular() {
				return fmt.Errorf("nonregular evidence %s", rel)
			}
		}
		return nil
	})
	return t, err
}
func (t *tree) read(ref Ref, dst any) error {
	if !validPath(ref.Path) || !digest.MatchString(ref.SHA256) {
		return fmt.Errorf("invalid reference %+v", ref)
	}
	if t.files[strings.ToLower(ref.Path)] != ref.Path {
		return fmt.Errorf("missing or case-mismatched evidence %s", ref.Path)
	}
	data, err := os.ReadFile(filepath.Join(t.root, filepath.FromSlash(ref.Path)))
	if err != nil {
		return err
	}
	if hash(data) != ref.SHA256 {
		return fmt.Errorf("digest mismatch: %s", ref.Path)
	}
	t.used[ref.Path] = true
	if dst != nil {
		if err := Decode(data, dst); err != nil {
			return fmt.Errorf("%s: %w", ref.Path, err)
		}
	}
	return nil
}
func FileRef(root, p string) (Ref, error) {
	if !validPath(p) {
		return Ref{}, fmt.Errorf("unsafe path %q", p)
	}
	b, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(p)))
	if err != nil {
		return Ref{}, err
	}
	return Ref{p, hash(b)}, nil
}
func WriteJSON(root, p string, v any) (Ref, error) {
	if !validPath(p) {
		return Ref{}, fmt.Errorf("unsafe output path %q", p)
	}
	abs, err := filepath.Abs(filepath.Join(root, filepath.FromSlash(p)))
	if err != nil {
		return Ref{}, err
	}
	parent := filepath.Dir(abs)
	resolved, err := filepath.EvalSymlinks(parent)
	if err != nil {
		return Ref{}, err
	}
	if !strings.EqualFold(filepath.Clean(resolved), parent) {
		return Ref{}, fmt.Errorf("linked output directory")
	}
	if info, err := os.Lstat(abs); err == nil {
		if !info.Mode().IsRegular() {
			return Ref{}, fmt.Errorf("nonregular output %s", p)
		}
	} else if !os.IsNotExist(err) {
		return Ref{}, err
	}
	b, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return Ref{}, err
	}
	b = append(b, '\n')
	if err = os.WriteFile(filepath.Join(root, p), b, 0o644); err != nil {
		return Ref{}, err
	}
	return Ref{p, hash(b)}, nil
}
