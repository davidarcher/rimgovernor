package main

// The save-fixture factory:.github/workflows/fixture-factory.yml
// runs sustained/colony weekly on a Windows runner and uploads its
// checkpoint ring as the run artifact colony-checkpoints-<commit>.
// `-from latest-ci` (profile-capture) and `acceptance fetch-fixture`
// download the newest completed run's ring with `gh run download` into
// <root>/ci-fixtures/<run id>, so a fresh clone with no local run has a
// real colony to load.

import (
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// latestCI is the -from label that resolves to the factory's newest ring.
const latestCI = "latest-ci"

const (
	factoryWorkflow = "fixture-factory.yml"
	factoryArtifact = "colony-checkpoints-"
	factoryRepo     = "davidarcher/rimgovernor"
)

// ghRunner runs gh with args and returns its stdout; tests replace it.
var ghRunner = func(args ...string) ([]byte, error) {
	cmd := exec.Command("gh", args...)
	cmd.Stderr = os.Stderr
	return cmd.Output()
}

// factoryRun is the newest completed factory run on main; a failed case still
// publishes its bundle (the run stays red), so success is not required.
type factoryRun struct {
	ID      int64  `json:"databaseId"`
	HeadSHA string `json:"headSha"`
	// Conclusion is success or failure for a run that published a bundle.
	Conclusion string `json:"conclusion"`
}

func newestFactoryRun() (factoryRun, error) {
	out, err := ghRunner("run", "list", "-R", factoryRepo, "--workflow", factoryWorkflow, "--branch", "main",
		"--status", "completed", "--limit", "5", "--json", "databaseId,headSha,conclusion")
	if err != nil {
		return factoryRun{}, fmt.Errorf("gh run list %s: %w", factoryWorkflow, err)
	}
	var runs []factoryRun
	if err := json.Unmarshal(out, &runs); err != nil {
		return factoryRun{}, fmt.Errorf("gh run list %s: %w", factoryWorkflow, err)
	}
	for _, run := range runs {
		if run.ID != 0 && (run.Conclusion == "success" || run.Conclusion == "failure") {
			return run, nil
		}
	}
	return factoryRun{}, fmt.Errorf("no completed %s run on main: dispatch it once", factoryWorkflow)
}

// fetchCIRing downloads (once per run id) the newest factory run's
// checkpoint ring for caseName under root and returns its ring directory.
func fetchCIRing(root, caseName string) (string, factoryRun, error) {
	run, err := newestFactoryRun()
	if err != nil {
		return "", run, err
	}
	dir := filepath.Join(root, "ci-fixtures", strconv.FormatInt(run.ID, 10))
	if ring := findCIRing(dir, caseName); ring != "" {
		return ring, run, nil
	}
	partial := dir + ".partial"
	os.RemoveAll(partial)
	if _, err := ghRunner("run", "download", strconv.FormatInt(run.ID, 10), "-R", factoryRepo,
		"--pattern", factoryArtifact+"*", "--dir", partial); err != nil {
		return "", run, fmt.Errorf("gh run download %d: %w", run.ID, err)
	}
	os.RemoveAll(dir)
	if err := os.Rename(partial, dir); err != nil {
		return "", run, err
	}
	ring := findCIRing(dir, caseName)
	if ring == "" {
		return "", run, fmt.Errorf("run %d's artifact holds no %s checkpoint ring", run.ID, caseName)
	}
	return ring, run, nil
}

// findCIRing is the ring directory for caseName in a downloaded factory
// artifact (<dir>/<artifact>/checkpoints/<case>), "" when none holds an
// entry.
func findCIRing(dir, caseName string) string {
	matches, _ := filepath.Glob(filepath.Join(dir, factoryArtifact+"*", "checkpoints", filepath.FromSlash(caseName)))
	for _, m := range matches {
		if ring, _ := na.ReadRing(m); newestInRing(ring) != "" {
			return m
		}
	}
	return ""
}

// ciBundle is the newest bundle of the factory's ring for caseName.
func ciBundle(root, caseName string) (string, error) {
	dir, _, err := fetchCIRing(root, caseName)
	if err != nil {
		return "", err
	}
	ring, err := na.ReadRing(dir)
	if err != nil {
		return "", err
	}
	return newestInRing(ring), nil
}

const fetchFixtureUsage = `
  acceptance fetch-fixture -root <dir> [-case <area/case>]
    downloads the newest completed fixture-factory run's checkpoint ring (once per run) and prints its newest bundle directory`

func fetchFixture(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("fetch-fixture", flag.ContinueOnError)
	fs.SetOutput(stderr)
	root := fs.String("root", "", "absolute worker root the artifact is kept under (<root>/ci-fixtures)")
	caseName := fs.String("case", "sustained/colony", "case whose ring to return")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	if *root == "" || !filepath.IsAbs(*root) || len(fs.Args()) > 0 {
		fmt.Fprintln(stderr, errors.New("-root <absolute dir> is required"))
		return 2
	}
	dir, run, err := fetchCIRing(*root, *caseName)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	ring, err := na.ReadRing(dir)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	fmt.Fprintf(stderr, "fetch-fixture: run %d at %s, ring %s\n", run.ID, strings.TrimSpace(run.HeadSHA), dir)
	streams := ciStreams(dir, *caseName)
	for _, s := range streams {
		fmt.Fprintf(stderr, "fetch-fixture: stream %s\n", s)
	}
	if len(streams) > 0 {
		fmt.Fprintln(stderr, "fetch-fixture: load a review with snapshot.LoadReview(<stream>, <tick>, 0); list them with `go run ./internal/snapshot/cmd/trim -list <stream>`")
	}
	fmt.Fprintln(stdout, newestInRing(ring))
	return 0
}

// ciStreams are the colony snapshot streams the factory recorded for
// caseName beside its ring: <artifact>/snapshots/<case>/routine-stream-*.jsonl
// for the ring <artifact>/checkpoints/<case>.
func ciStreams(ring, caseName string) []string {
	artifact := ring
	for range strings.Split(caseName, "/") {
		artifact = filepath.Dir(artifact)
	}
	artifact = filepath.Dir(artifact)
	matches, _ := filepath.Glob(filepath.Join(artifact, "snapshots", filepath.FromSlash(caseName), "routine-stream-*.jsonl"))
	return matches
}
