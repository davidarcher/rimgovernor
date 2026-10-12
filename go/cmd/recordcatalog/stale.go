package main

import (
	"fmt"
	"os"
)

// refreshCommand is the command a stale-recording failure names.
const refreshCommand = "go run ./cmd/recordcatalog -root <absolute worker root>"

// checkFresh reports why the sidecar no longer matches defs.proto's source
// assembly line, which `task defmirror:generate` writes from the installed
// game's Assembly-CSharp version. It reads files only, so CI needs no game
// install.
func checkFresh(sidecar, defsProto string) error {
	text, err := os.ReadFile(sidecar)
	if err != nil {
		return err
	}
	s, err := parseStamp(string(text))
	if err != nil {
		return fmt.Errorf("%s: %w", sidecar, err)
	}
	source, err := defsSource(defsProto)
	if err != nil {
		return err
	}
	if s.DefsSource != source {
		return fmt.Errorf("the recorded catalog is stale: defs_proto_source %q vs %q in %s; refresh it from go/ with `%s` (see docs/developers/testing/recording-the-catalog.md)", s.DefsSource, source, defsProto, refreshCommand)
	}
	return nil
}
