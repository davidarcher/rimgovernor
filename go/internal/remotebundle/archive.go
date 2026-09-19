package remotebundle

import (
	"bytes"
	"context"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

// Tools are explicit executables from the trusted runner's pinned tool installation.
type Tools struct{ SevenZip, Age string }

func command(ctx context.Context, dir, tool string, args ...string) error {
	c := exec.CommandContext(ctx, tool, args...)
	c.Dir = dir
	// Never inherit tokens into compression/decryption/build programs.
	c.Env = PublicEnvironment()
	if out, err := c.CombinedOutput(); err != nil {
		return fmt.Errorf("%s failed: %w: %s", filepath.Base(tool), err, out)
	}
	return nil
}

func PublicEnvironment() []string {
	var result []string
	for _, e := range os.Environ() {
		k, _, _ := strings.Cut(e, "=")
		k = strings.ToUpper(k)
		if strings.Contains(k, "TOKEN") || strings.Contains(k, "SECRET") || strings.Contains(k, "PASSWORD") || strings.Contains(k, "PRIVATE_KEY") {
			continue
		}
		result = append(result, e)
	}
	return result
}

// Materialize copies a selected dependency subtree, following source junctions,
// but creates only regular destination files. Cycles and destination overlap fail.
func Materialize(source, dest string) error {
	source, err := resolveSource(source)
	if err != nil {
		return fmt.Errorf("resolve materialization source: %w", err)
	}
	source, err = filepath.Abs(source)
	if err != nil {
		return err
	}
	dest, err = resolveDestination(dest)
	if err != nil {
		return err
	}
	if within(source, dest) || within(dest, source) {
		return fmt.Errorf("source and staging destination overlap")
	}
	return copyMaterialized(source, dest, map[string]bool{})
}

func resolveDestination(dest string) (string, error) {
	p, err := filepath.Abs(dest)
	if err != nil {
		return "", err
	}
	var tail []string
	for {
		_, err := os.Lstat(p)
		if err == nil {
			real, err := resolveSource(p)
			if err != nil {
				return "", err
			}
			for i := len(tail) - 1; i >= 0; i-- {
				real = filepath.Join(real, tail[i])
			}
			return real, nil
		}
		if !os.IsNotExist(err) {
			return "", err
		}
		parent := filepath.Dir(p)
		if parent == p {
			return "", fmt.Errorf("no existing destination ancestor")
		}
		tail = append(tail, filepath.Base(p))
		p = parent
	}
}

func within(parent, child string) bool {
	r, e := filepath.Rel(strings.ToLower(parent), strings.ToLower(child))
	return e == nil && (r == "." || r != ".." && !strings.HasPrefix(r, ".."+string(filepath.Separator)))
}

func copyMaterialized(source, dest string, ancestors map[string]bool) error {
	real, err := resolveSource(source)
	if err != nil {
		return fmt.Errorf("resolve %s: %w", source, err)
	}
	i, err := os.Stat(real)
	if err != nil {
		return err
	}
	if i.IsDir() {
		key := strings.ToLower(real)
		if ancestors[key] {
			return fmt.Errorf("source junction cycle at %s", source)
		}
		ancestors[key] = true
		defer delete(ancestors, key)
		if err := os.MkdirAll(dest, 0700); err != nil {
			return err
		}
		entries, err := os.ReadDir(real)
		if err != nil {
			return err
		}
		for _, e := range entries {
			if err := SafePath(e.Name()); err != nil {
				return err
			}
			if err := copyMaterialized(filepath.Join(real, e.Name()), filepath.Join(dest, e.Name()), ancestors); err != nil {
				return fmt.Errorf("copy %s: %w", filepath.Join(real, e.Name()), err)
			}
		}
		return nil
	}
	if !i.Mode().IsRegular() {
		return fmt.Errorf("nonregular source %s", source)
	}
	if err := os.MkdirAll(filepath.Dir(dest), 0700); err != nil {
		return err
	}
	in, err := os.Open(real)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.OpenFile(dest, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return err
	}
	_, err = io.Copy(out, in)
	closeErr := out.Close()
	if err != nil {
		return err
	}
	return closeErr
}

func InventoryTree(root string) (Inventory, error) {
	inv := Inventory{SchemaVersion: 1, Files: []File{}}
	err := filepath.WalkDir(root, func(p string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		i, err := d.Info()
		if err != nil {
			return err
		}
		if i.Mode()&(os.ModeSymlink|os.ModeIrregular) != 0 {
			return fmt.Errorf("staging contains link %s", p)
		}
		if d.IsDir() {
			return nil
		}
		rel, err := filepath.Rel(root, p)
		if err != nil {
			return err
		}
		rel = filepath.ToSlash(rel)
		if err := SafePath(rel); err != nil {
			return err
		}
		h, n, err := HashFile(p)
		if err != nil {
			return err
		}
		inv.Files = append(inv.Files, File{rel, n, h})
		return nil
	})
	sort.Slice(inv.Files, func(i, j int) bool { return inv.Files[i].Path < inv.Files[j].Path })
	return inv, err
}

func BindInventory(m *Manifest, inv Inventory) {
	m.UnpackedBytes = 0
	for i, c := range m.Components {
		var lines strings.Builder
		for _, f := range inv.Files {
			if strings.HasPrefix(f.Path, c.PathPrefix) {
				fmt.Fprintf(&lines, "%s\t%d\t%s\n", f.Path, f.Bytes, f.SHA256)
			}
		}
		m.Components[i].SHA256 = Digest([]byte(lines.String()))
	}
	for _, f := range inv.Files {
		m.UnpackedBytes += f.Bytes
	}
}

// EncryptArchive writes one encrypted 7z stream, then immutable digest-named parts.
// The caller owns a fresh output directory; no existing file is replaced.
func EncryptArchive(ctx context.Context, tools Tools, tree, out, recipient string, partBytes int64) ([]Part, error) {
	if partBytes <= 0 || partBytes > 1900000000 {
		return nil, fmt.Errorf("part size must be 1..1900000000 bytes")
	}
	archive := filepath.Join(out, "payload.7z")
	encrypted := archive + ".age"
	defer os.Remove(archive)
	defer os.Remove(encrypted)
	if err := command(ctx, tree, tools.SevenZip, "a", "-t7z", "-mx=7", "-mmt=2", "-mtc=off", "-mta=off", "-mtm=off", archive, "."); err != nil {
		return nil, err
	}
	if err := command(ctx, "", tools.Age, "-r", recipient, "-o", encrypted, archive); err != nil {
		return nil, err
	}
	in, err := os.Open(encrypted)
	if err != nil {
		return nil, err
	}
	defer in.Close()
	var parts []Part
	for index := 1; ; index++ {
		tmp := filepath.Join(out, fmt.Sprintf("part-%04d.tmp", index))
		f, err := os.OpenFile(tmp, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
		if err != nil {
			return nil, err
		}
		n, err := io.CopyN(f, in, partBytes)
		ce := f.Close()
		if err != nil && err != io.EOF {
			return nil, err
		}
		if ce != nil {
			return nil, ce
		}
		if n == 0 {
			os.Remove(tmp)
			break
		}
		h, _, err := HashFile(tmp)
		if err != nil {
			return nil, err
		}
		name := fmt.Sprintf("bundle-%s-%04d.7z.age.part", h, index)
		if err := os.Rename(tmp, filepath.Join(out, name)); err != nil {
			return nil, err
		}
		parts = append(parts, Part{Name: name, Bytes: n, SHA256: h})
		if n < partBytes {
			break
		}
	}
	return parts, nil
}

func VerifyParts(dir string, m Manifest) error {
	for _, p := range m.Parts {
		if err := VerifyFile(filepath.Join(dir, p.Name), p.Bytes, p.SHA256); err != nil {
			return fmt.Errorf("archive part %s: %w", p.Name, err)
		}
	}
	// Check across part boundaries, including deliberately tiny multipart tests.
	// A mislabeled plaintext archive must never enter a public release or cache.
	const ageHeader = "age-encryption.org/v1\n"
	var header bytes.Buffer
	for _, p := range m.Parts {
		remaining := int64(len(ageHeader) - header.Len())
		if remaining == 0 {
			break
		}
		f, err := os.Open(filepath.Join(dir, p.Name))
		if err != nil {
			return err
		}
		_, err = io.CopyN(&header, f, remaining)
		f.Close()
		if err != nil && err != io.EOF {
			return err
		}
	}
	if header.String() != ageHeader {
		return fmt.Errorf("archive is not age-v1 ciphertext; refusing plaintext or unsupported encryption")
	}
	return nil
}

// ValidateListing checks 7-Zip's technical listing before any extraction. Unknown
// link metadata fails closed; file sizes and paths must exactly match inventory.
func ValidateListing(listing string, inv Inventory) error {
	want := map[string]File{}
	dirs := map[string]bool{}
	for _, f := range inv.Files {
		want[f.Path] = f
		for p := filepath.ToSlash(filepath.Dir(f.Path)); p != "."; p = filepath.ToSlash(filepath.Dir(p)) {
			dirs[p] = true
		}
	}
	seen := map[string]bool{}
	listing = strings.ReplaceAll(listing, "\r\n", "\n")
	for _, block := range strings.Split(strings.TrimSpace(listing), "\n\n") {
		fields := map[string]string{}
		for _, line := range strings.Split(block, "\n") {
			k, v, ok := strings.Cut(line, " = ")
			if ok {
				if _, exists := fields[k]; exists {
					return fmt.Errorf("duplicate archive metadata %s", k)
				}
				fields[k] = v
			}
		}
		p := strings.ReplaceAll(fields["Path"], "\\", "/")
		if p == "" {
			return fmt.Errorf("archive listing lacks path")
		}
		if err := SafePath(p); err != nil {
			return err
		}
		key := strings.ToLower(p)
		if seen[key] {
			return fmt.Errorf("duplicate archive entry %s", p)
		}
		seen[key] = true
		for k, v := range fields {
			if (strings.Contains(strings.ToLower(k), "link") || k == "Reparse") && v != "" {
				return fmt.Errorf("archive link %s", p)
			}
		}
		attrs := fields["Attributes"]
		if strings.Contains(attrs, "l") || strings.Contains(attrs, "L") {
			return fmt.Errorf("archive link attributes %s", p)
		}
		if fields["Folder"] == "+" || strings.HasPrefix(attrs, "D") {
			if !dirs[p] {
				return fmt.Errorf("unlisted archive directory %s", p)
			}
			continue
		}
		f, ok := want[p]
		n, err := strconv.ParseInt(fields["Size"], 10, 64)
		if !ok || err != nil || n != f.Bytes {
			return fmt.Errorf("unlisted or wrong-size archive entry %s", p)
		}
		delete(want, p)
	}
	if len(want) > 0 {
		return fmt.Errorf("archive listing missing %d files", len(want))
	}
	return nil
}

func Extract(ctx context.Context, tools Tools, partsDir, dest, identity string, m Manifest, inv Inventory) error {
	if err := VerifyParts(partsDir, m); err != nil {
		return err
	}
	if _, err := os.Lstat(dest); !os.IsNotExist(err) {
		return fmt.Errorf("extraction destination must not exist: %s", dest)
	}
	work, err := os.MkdirTemp(filepath.Dir(dest), "bundle-decrypt-")
	if err != nil {
		return err
	}
	defer os.RemoveAll(work)
	cipher := filepath.Join(work, "payload.age")
	out, err := os.OpenFile(cipher, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return err
	}
	for _, p := range m.Parts {
		in, err := os.Open(filepath.Join(partsDir, p.Name))
		if err != nil {
			out.Close()
			return err
		}
		_, err = io.Copy(out, in)
		in.Close()
		if err != nil {
			out.Close()
			return err
		}
	}
	if err := out.Close(); err != nil {
		return err
	}
	archive := filepath.Join(work, "payload.7z")
	if err := command(ctx, "", tools.Age, "-d", "-i", identity, "-o", archive, cipher); err != nil {
		return err
	}
	c := exec.CommandContext(ctx, tools.SevenZip, "l", "-slt", "-ba", "-sccUTF-8", archive)
	c.Env = PublicEnvironment()
	listing, err := c.Output()
	if err != nil {
		return fmt.Errorf("list decrypted archive: %w", err)
	}
	if err := ValidateListing(string(listing), inv); err != nil {
		return err
	}
	tree := filepath.Join(work, "tree")
	if err := command(ctx, "", tools.SevenZip, "x", "-y", "-o"+tree, archive); err != nil {
		return err
	}
	if err := VerifyTree(tree, inv); err != nil {
		return err
	}
	return os.Rename(tree, dest)
}
