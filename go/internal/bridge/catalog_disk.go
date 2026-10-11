package bridge

import (
	"bytes"
	"compress/gzip"
	"crypto/sha256"
	"encoding/hex"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protodesc"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// The static part of the definition catalog on disk, shared by every process of
// this user: a game with the same fingerprint (game version, language, mods) has
// the same defs, so a fresh Client, worktree or acceptance run reads the file
// instead of asking the game for 8 MB of rows. The file is untrusted input: it
// is decoded and validated like a game reply, and any failure is a miss.

const (
	catalogFileSuffix = ".pb.gz"
	// catalogFilesKept is how many fingerprints stay on disk, newest first.
	catalogFilesKept = 4
)

// catalogSchema names the wire schema the files are written under: a hash of
// the observations and defs file descriptors, so a regenerated schema never
// decodes an older file.
var catalogSchema = sync.OnceValue(func() string {
	sum := sha256.New()
	for _, file := range []protoreflect.FileDescriptor{o.File_observations_proto, d.File_defs_proto} {
		raw, err := proto.MarshalOptions{Deterministic: true}.Marshal(protodesc.ToFileDescriptorProto(file))
		if err != nil {
			return ""
		}
		sum.Write(raw)
	}
	return hex.EncodeToString(sum.Sum(nil))[:16]
})

func (caller *Client) catalogFile(fingerprint string) string {
	return filepath.Join(caller.catalogDir, fingerprint+"-"+catalogSchema()+catalogFileSuffix)
}

// diskCatalog is the newest static catalog on disk under the current schema, nil
// when the Client has no cache directory or no file decodes. A file that does not
// decode is deleted.
func (caller *Client) diskCatalog() *DefinitionCatalog {
	if caller.catalogDir == "" || catalogSchema() == "" {
		return nil
	}
	entries, err := os.ReadDir(caller.catalogDir)
	if err != nil {
		return nil
	}
	type file struct {
		path string
		at   time.Time
	}
	var files []file
	for _, entry := range entries {
		name := entry.Name()
		info, err := entry.Info()
		if err != nil || !strings.HasSuffix(name, "-"+catalogSchema()+catalogFileSuffix) {
			continue
		}
		files = append(files, file{filepath.Join(caller.catalogDir, name), info.ModTime()})
	}
	sort.Slice(files, func(i, j int) bool { return files[i].at.After(files[j].at) })
	for _, f := range files {
		catalog, err := readCatalogFile(f.path)
		if err != nil {
			_ = os.Remove(f.path)
			continue
		}
		now := time.Now()
		_ = os.Chtimes(f.path, now, now)
		return catalog
	}
	return nil
}

func readCatalogFile(path string) (*DefinitionCatalog, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	zr, err := gzip.NewReader(file)
	if err != nil {
		return nil, err
	}
	raw, err := io.ReadAll(zr)
	if err != nil {
		return nil, err
	}
	wire := &o.DefinitionCatalog{}
	if err := proto.Unmarshal(raw, wire); err != nil {
		return nil, err
	}
	if wire.GetFingerprint() == "" || !strings.HasPrefix(filepath.Base(path), wire.GetFingerprint()+"-") {
		return nil, contract("cached definition catalog names another fingerprint")
	}
	return decodeStatic(wire)
}

// saveCatalog writes the static part of wire for its fingerprint, then drops all
// but the newest files. Best effort: a failure is logged and the next process
// reads the game.
func (caller *Client) saveCatalog(wire *o.DefinitionCatalog) {
	if caller.catalogDir == "" || wire.GetFingerprint() == "" || catalogSchema() == "" {
		return
	}
	path := caller.catalogFile(wire.GetFingerprint())
	if _, err := os.Stat(path); err == nil {
		return
	}
	if writeCatalogFile(path, wire) != nil {
		return
	}
	caller.pruneCatalogFiles()
}

func writeCatalogFile(path string, wire *o.DefinitionCatalog) error {
	// The static fields only: the context and stat environment are the load's.
	static := &o.DefinitionCatalog{
		Biotech: wire.Biotech, ThingDefs: wire.ThingDefs, TerrainDefs: wire.TerrainDefs, Derived: wire.Derived,
		Defs: wire.Defs, ClassChains: wire.ClassChains, ThingFacts: wire.ThingFacts, GameConstants: wire.GameConstants,
		Fingerprint: wire.Fingerprint,
	}
	raw, err := proto.MarshalOptions{Deterministic: true}.Marshal(static)
	if err != nil {
		return err
	}
	var buf bytes.Buffer
	zw := gzip.NewWriter(&buf)
	if _, err := zw.Write(raw); err != nil {
		return err
	}
	if err := zw.Close(); err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(filepath.Dir(path), "catalog-*.tmp")
	if err != nil {
		return err
	}
	_, werr := tmp.Write(buf.Bytes())
	cerr := tmp.Close()
	if werr != nil || cerr != nil {
		_ = os.Remove(tmp.Name())
		if werr != nil {
			return werr
		}
		return cerr
	}
	if err := os.Rename(tmp.Name(), path); err != nil {
		_ = os.Remove(tmp.Name())
		return err
	}
	return nil
}

func (caller *Client) pruneCatalogFiles() {
	entries, err := os.ReadDir(caller.catalogDir)
	if err != nil {
		return
	}
	type file struct {
		path string
		at   time.Time
	}
	var files []file
	for _, entry := range entries {
		info, err := entry.Info()
		if err != nil || !strings.HasSuffix(entry.Name(), catalogFileSuffix) {
			continue
		}
		files = append(files, file{filepath.Join(caller.catalogDir, entry.Name()), info.ModTime()})
	}
	sort.Slice(files, func(i, j int) bool { return files[i].at.After(files[j].at) })
	for i := catalogFilesKept; i < len(files); i++ {
		_ = os.Remove(files[i].path)
	}
}
