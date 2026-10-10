package main

import (
	"fmt"
	"os"
	"regexp"
	"strings"
)

// refreshCommand is the command a stale-recording failure names.
const refreshCommand = "go run ./cmd/recordcatalog -root <absolute worker root>"

var krafsVersion = regexp.MustCompile(`<PackageReference\s+Include="Krafs\.Rimworld\.Ref"\s+Version="([^"]+)"`)

// checkFresh reports why the sidecar no longer matches the pins the def
// mirror is built from: the Krafs.Rimworld.Ref version in the DefMirror
// project (the game version's first token) and defs.proto's source assembly
// line. It reads files only, so CI needs no game install.
func checkFresh(sidecar, csproj, defsProto string) error {
	text, err := os.ReadFile(sidecar)
	if err != nil {
		return err
	}
	s, err := parseStamp(string(text))
	if err != nil {
		return fmt.Errorf("%s: %w", sidecar, err)
	}
	project, err := os.ReadFile(csproj)
	if err != nil {
		return err
	}
	match := krafsVersion.FindSubmatch(project)
	if match == nil {
		return fmt.Errorf("%s has no Krafs.Rimworld.Ref PackageReference", csproj)
	}
	source, err := defsSource(defsProto)
	if err != nil {
		return err
	}
	var stale []string
	if pinned, _, _ := strings.Cut(s.GameVersion, " "); pinned != string(match[1]) {
		stale = append(stale, fmt.Sprintf("game_version %q vs Krafs.Rimworld.Ref %s in %s", s.GameVersion, match[1], csproj))
	}
	if s.DefsSource != source {
		stale = append(stale, fmt.Sprintf("defs_proto_source %q vs %q in %s", s.DefsSource, source, defsProto))
	}
	if len(stale) > 0 {
		return fmt.Errorf("the recorded catalog is stale: %s; refresh it from go/ with `%s` (see docs/developers/testing/recording-the-catalog.md)", strings.Join(stale, "; "), refreshCommand)
	}
	return nil
}
