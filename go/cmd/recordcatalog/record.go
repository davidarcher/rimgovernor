package main

import (
	"bytes"
	"compress/gzip"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Recording file names under the testdata directory.
const (
	catalogFile = "full_catalog.pb.gz"
	versionFile = "full_catalog.version"
)

// stamp is what the sidecar says about the game the recording came from.
type stamp struct {
	// GameVersion is the game's Version.txt, for example "1.6.4871 rev590".
	GameVersion string
	// DLCs are the casefolded expansion package IDs loaded, sorted.
	DLCs []string
	// DefsSource is the "Assembly-CSharp <version>" source line of
	// contracts/proto/defs.proto, the assembly the def mirror was generated from.
	DefsSource string
}

// render is the sidecar text: one key=value line each, in a fixed order.
func (s stamp) render() string {
	return fmt.Sprintf("game_version=%s\ndlcs=%s\ndefs_proto_source=%s\n", s.GameVersion, strings.Join(s.DLCs, ","), s.DefsSource)
}

// parseStamp reads render's output back.
func parseStamp(text string) (stamp, error) {
	var s stamp
	seen := map[string]bool{}
	for _, line := range strings.Split(strings.TrimRight(text, "\n"), "\n") {
		key, value, ok := strings.Cut(line, "=")
		if !ok || seen[key] {
			return s, fmt.Errorf("bad sidecar line %q", line)
		}
		seen[key] = true
		switch key {
		case "game_version":
			s.GameVersion = value
		case "dlcs":
			if value != "" {
				s.DLCs = strings.Split(value, ",")
			}
		case "defs_proto_source":
			s.DefsSource = value
		default:
			return s, fmt.Errorf("unknown sidecar key %q", key)
		}
	}
	if len(seen) != 3 {
		return s, fmt.Errorf("sidecar holds %d of 3 keys", len(seen))
	}
	return s, nil
}

// gameVersion is the trimmed text of the install's Version.txt.
func gameVersion(workingDir string) (string, error) {
	data, err := os.ReadFile(filepath.Join(workingDir, "Version.txt"))
	if err != nil {
		return "", err
	}
	version := strings.TrimSpace(string(data))
	if version == "" {
		return "", fmt.Errorf("%s is empty", filepath.Join(workingDir, "Version.txt"))
	}
	return version, nil
}

// defsSource is the "Assembly-CSharp <version>" part of defs.proto's
// "// Source:" header line.
func defsSource(defsProto string) (string, error) {
	data, err := os.ReadFile(defsProto)
	if err != nil {
		return "", err
	}
	for _, line := range strings.Split(string(data), "\n") {
		if source, ok := strings.CutPrefix(strings.TrimSpace(line), "// Source: Assembly-CSharp "); ok {
			return "Assembly-CSharp " + source, nil
		}
	}
	return "", fmt.Errorf("%s has no \"// Source: Assembly-CSharp\" line", defsProto)
}

// sameContent reports whether two catalog reads carry the same content. The
// context (colony and load identity, tick, native generation) names the read
// and differs on every run, so it is not content.
func sameContent(a, b *o.DefinitionCatalog) bool {
	a, b = proto.Clone(a).(*o.DefinitionCatalog), proto.Clone(b).(*o.DefinitionCatalog)
	a.Context, b.Context = nil, nil
	return proto.Equal(a, b)
}

// encode is the recording's bytes: the deterministic wire encoding, gzipped.
func encode(catalog *o.DefinitionCatalog) ([]byte, error) {
	wire, err := proto.MarshalOptions{Deterministic: true}.Marshal(catalog)
	if err != nil {
		return nil, err
	}
	var zipped bytes.Buffer
	z := gzip.NewWriter(&zipped)
	if _, err := z.Write(wire); err != nil {
		return nil, err
	}
	if err := z.Close(); err != nil {
		return nil, err
	}
	return zipped.Bytes(), nil
}

// readRecording decodes a recording file; a missing file is (nil, nil).
func readRecording(path string) (*o.DefinitionCatalog, error) {
	f, err := os.Open(path)
	if os.IsNotExist(err) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	defer f.Close()
	z, err := gzip.NewReader(f)
	if err != nil {
		return nil, err
	}
	wire, err := io.ReadAll(z)
	if err != nil {
		return nil, err
	}
	catalog := &o.DefinitionCatalog{}
	if err := proto.Unmarshal(wire, catalog); err != nil {
		return nil, err
	}
	return catalog, nil
}

// writeFile replaces path by writing a sibling and renaming it over.
func writeFile(path string, data []byte) error {
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, data, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}
