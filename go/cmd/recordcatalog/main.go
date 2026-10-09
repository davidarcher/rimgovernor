// Command recordcatalog captures the native DefinitionCatalog reply from the
// headless game (every installed expansion loaded) and refreshes the recording
// the Go tests read:
//
//	go run ./cmd/recordcatalog -root <abs worker root> [-game id] [-out dir] [-defs-proto file] [-timeout d]
//
// It writes internal/observation/testdata/full_catalog.pb.gz, rewrites
// planning_views.golden.gz (RG_UPDATE_GOLDEN=1 go test ./internal/observation
// -run TestPlanningViewsMatchTheRecordedGolden) and writes the sidecar
// full_catalog.version: the game version (Version.txt), the expansions
// loaded and the defs.proto source assembly line. The root is a worker root
// prepared by `acceptance setup`; run from go/. See
// docs/developers/testing/recording-the-catalog.md.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

func main() {
	root := flag.String("root", "", "absolute worker root prepared by `acceptance setup`")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	out := flag.String("out", filepath.FromSlash("internal/observation/testdata"), "directory holding the recording and its goldens")
	defsProto := flag.String("defs-proto", filepath.FromSlash("../contracts/proto/defs.proto"), "generated def mirror whose source assembly the sidecar records")
	timeout := flag.Duration("timeout", 30*time.Minute, "overall limit")
	flag.Parse()
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	if err := run(ctx, *root, *game, *out, *defsProto, os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, "recordcatalog:", err)
		os.Exit(1)
	}
}

func run(ctx context.Context, root, game, out, defsProto string, stdout io.Writer) error {
	if !filepath.IsAbs(root) {
		return errors.New("-root must be an absolute worker root")
	}
	if os.Getenv(na.ExpansionsEnv) != "" {
		return fmt.Errorf("%s is set: the recording needs every installed expansion loaded", na.ExpansionsEnv)
	}
	source, err := defsSource(defsProto)
	if err != nil {
		return err
	}
	cfg := &na.Config{Root: root, Output: filepath.Join(root, "acceptance", "recordcatalog"), Headless: true, GameID: game}
	report := na.NewReport("recordcatalog", true)
	session, err := na.OpenSession(ctx, cfg, report, na.DebugStart{}, na.QuietIfAvailable)
	if err != nil {
		return err
	}
	defer session.Close()
	stamped, err := stampOf(cfg, source)
	if err != nil {
		return err
	}
	id, err := identityOf(session.Identity)
	if err != nil {
		return err
	}
	wire, err := session.Harness.Client.RecordDefinitionCatalog(ctx, id)
	if err != nil {
		return fmt.Errorf("read the definition catalog: %w", err)
	}
	if wire.GetBiotech() == nil || wire.GetOdyssey() == nil || wire.GetAnomaly() == nil {
		return errors.New("the catalog lacks a Biotech, Odyssey or Anomaly section: not every expansion loaded")
	}
	zipped, err := encode(wire)
	if err != nil {
		return err
	}
	catalogPath := filepath.Join(out, catalogFile)
	previous, err := readRecording(catalogPath)
	if err != nil {
		return fmt.Errorf("read the previous recording: %w", err)
	}
	if err := writeFile(catalogPath, zipped); err != nil {
		return err
	}
	if err := writeFile(filepath.Join(out, versionFile), []byte(stamped.render())); err != nil {
		return err
	}
	switch {
	case previous == nil:
		fmt.Fprintln(stdout, "recorded a new catalog")
	case sameContent(previous, wire):
		fmt.Fprintln(stdout, "catalog content is unchanged from the previous recording")
	default:
		fmt.Fprintln(stdout, "catalog content differs from the previous recording")
	}
	fmt.Fprint(stdout, stamped.render())
	golden := exec.CommandContext(ctx, "go", "test", "./internal/observation", "-run", "TestPlanningViewsMatchTheRecordedGolden", "-count=1")
	golden.Env = append(os.Environ(), "RG_UPDATE_GOLDEN=1")
	golden.Stdout, golden.Stderr = stdout, os.Stderr
	if err := golden.Run(); err != nil {
		return fmt.Errorf("rewrite the planning goldens: %w", err)
	}
	return nil
}

// stampOf reads the sidecar's facts off the opened session's game install.
func stampOf(cfg *na.Config, source string) (stamp, error) {
	section, err := cfg.GameSection()
	if err != nil {
		return stamp{}, err
	}
	dir, _ := section["workingDir"].(string)
	if dir == "" {
		return stamp{}, errors.New("the game section has no workingDir")
	}
	version, err := gameVersion(dir)
	if err != nil {
		return stamp{}, err
	}
	dlcs, err := na.InstalledExpansions(dir)
	if err != nil {
		return stamp{}, err
	}
	return stamp{GameVersion: version, DLCs: dlcs, DefsSource: source}, nil
}

// identityOf converts the session's identity map to the wire identity.
func identityOf(identity map[string]any) (*c.Identity, error) {
	data, err := json.Marshal(identity)
	if err != nil {
		return nil, err
	}
	id := &c.Identity{}
	if err := protojson.Unmarshal(data, id); err != nil {
		return nil, err
	}
	return id, nil
}
