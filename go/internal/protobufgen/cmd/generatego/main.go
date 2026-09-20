// Command generatego runs the pinned official Go Protobuf generator against
// the canonical schemas and checks its owned *.pb.go outputs.
//
// It orchestrates protoc, protoc-gen-go and the go tool inside a fresh private
// artifact directory and records every command, input hash and output hash in
// result.json. It does not interpret schemas or emit Go source.
//
// It runs from the repository root, located by walking up from the working
// directory (override with -root); relative flag paths resolve there.
package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"time"
)

const (
	pluginVersion  = "v1.36.11"
	wireModule     = "github.com/davidarcher/RimGovernor/go/internal/wire"
	expectedProtoc = "libprotoc 30.0"
	commandTimeout = 10 * time.Minute
	protoRelative  = "contracts/proto"
	generatedGo    = "contracts/generated/protobuf/go"
)

type options struct {
	protoc    string
	protoRoot string
	output    string
	check     bool
	goTool    string
	modCache  string
}

// evidence is result.json; keys appear in the order the run produces them.
type evidence struct {
	PluginVersion string            `json:"plugin_version"`
	Commands      []commandRecord   `json:"commands"`
	ProtocSHA256  string            `json:"protoc_sha256,omitempty"`
	GoVersion     string            `json:"go_version,omitempty"`
	PluginSHA256  string            `json:"plugin_sha256,omitempty"`
	Inputs        map[string]string `json:"inputs,omitempty"`
	Outputs       map[string]string `json:"outputs,omitempty"`
	Differences   *[]string         `json:"differences,omitempty"`
}

type commandRecord struct {
	Args   []string `json:"args"`
	Cwd    string   `json:"cwd"`
	Exit   int      `json:"exit"`
	Stdout string   `json:"stdout"`
	Stderr string   `json:"stderr"`
}

func main() {
	var opts options
	root := flag.String("root", "", "repository root (default: nearest ancestor of the working directory containing contracts/proto)")
	flag.StringVar(&opts.protoc, "protoc", "", "official Grpc.Tools 2.72.0 compiler for this OS/CPU (required)")
	flag.StringVar(&opts.protoRoot, "proto-root", protoRelative, "canonical schema directory")
	flag.StringVar(&opts.output, "output", "", "fresh private artifact directory (required)")
	flag.BoolVar(&opts.check, "check", false, "fail on generated drift without changing checked-in files")
	flag.StringVar(&opts.goTool, "go", "go", "pinned repository Go executable")
	flag.StringVar(&opts.modCache, "modcache", "", "reusable GOMODCACHE for the plugin install (default: private to this run)")
	flag.Parse()
	if opts.protoc == "" || opts.output == "" {
		fmt.Fprintln(os.Stderr, "-protoc and -output are required")
		os.Exit(2)
	}
	if err := enterRoot(*root); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	if err := run(opts); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

// enterRoot changes into the repository root: the explicit one, or the
// nearest ancestor of the working directory that holds the canonical schemas.
func enterRoot(explicit string) error {
	root := explicit
	if root == "" {
		dir, err := os.Getwd()
		if err != nil {
			return err
		}
		root, err = findRoot(dir)
		if err != nil {
			return err
		}
	}
	return os.Chdir(root)
}

func findRoot(start string) (string, error) {
	for dir := start; ; dir = filepath.Dir(dir) {
		if info, err := os.Stat(filepath.Join(dir, protoRelative)); err == nil && info.IsDir() {
			return dir, nil
		}
		if filepath.Dir(dir) == dir {
			return "", fmt.Errorf("no %s directory above %s; pass -root", protoRelative, start)
		}
	}
}

type runner struct {
	env      []string
	evidence *evidence
}

// command runs one bounded subprocess with captured output and records it.
func (r *runner) command(cwd string, name string, args ...string) (string, error) {
	ctx, cancel := context.WithTimeout(context.Background(), commandTimeout)
	defer cancel()
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Dir = cwd
	cmd.Env = r.env
	var stdout, stderr bytes.Buffer
	cmd.Stdout, cmd.Stderr = &stdout, &stderr
	err := cmd.Run()
	exit := 0
	var exitErr *exec.ExitError
	if errors.As(err, &exitErr) {
		exit = exitErr.ExitCode()
	} else if err != nil {
		exit = -1
	}
	r.evidence.Commands = append(r.evidence.Commands, commandRecord{
		Args: append([]string{name}, args...), Cwd: cwd, Exit: exit, Stdout: stdout.String(), Stderr: stderr.String()})
	if err != nil {
		return "", fmt.Errorf("command failed (%d): %s %s\n%s", exit, name, strings.Join(args, " "), stderr.String())
	}
	return strings.TrimSpace(stdout.String()), nil
}

func run(opts options) (err error) {
	protoc, err := filepath.Abs(opts.protoc)
	if err != nil {
		return err
	}
	protoRoot, err := filepath.Abs(opts.protoRoot)
	if err != nil {
		return err
	}
	output, err := filepath.Abs(opts.output)
	if err != nil {
		return err
	}
	if _, err := os.Stat(output); err == nil {
		return fmt.Errorf("output directory already exists: %s", output)
	}
	if err := os.MkdirAll(output, 0o755); err != nil {
		return err
	}
	modCache := opts.modCache
	if modCache == "" {
		modCache = filepath.Join(output, "modcache")
	} else if modCache, err = filepath.Abs(modCache); err != nil {
		return err
	}
	r := &runner{env: privateEnv(output, modCache), evidence: &evidence{PluginVersion: pluginVersion, Commands: []commandRecord{}}}
	defer func() {
		if writeErr := writeEvidence(filepath.Join(output, "result.json"), r.evidence); writeErr != nil && err == nil {
			err = writeErr
		}
	}()
	return r.generate(protoc, protoRoot, output, opts)
}

// privateEnv keeps the plugin install inside the run and preserves the shared
// Go build cache. The module cache is private unless the caller supplies a
// reusable one (go.sum still verifies its contents).
func privateEnv(output, modCache string) []string {
	env := os.Environ()
	set := func(key, value string) {
		prefix := key + "="
		for i, entry := range env {
			if strings.HasPrefix(entry, prefix) {
				env[i] = prefix + value
				return
			}
		}
		env = append(env, prefix+value)
	}
	if os.Getenv("GOTOOLCHAIN") == "" {
		set("GOTOOLCHAIN", "local")
	}
	set("GOWORK", "off")
	set("GOBIN", filepath.Join(output, "bin"))
	set("GOMODCACHE", modCache)
	return env
}

func (r *runner) generate(protoc, protoRoot, output string, opts options) error {
	ev := r.evidence
	sum, err := fileSHA256(protoc)
	if err != nil {
		return err
	}
	ev.ProtocSHA256 = sum
	version, err := r.command(output, protoc, "--version")
	if err != nil {
		return err
	}
	if version != expectedProtoc {
		return errors.New("expected official Grpc.Tools 2.72.0 protoc (" + expectedProtoc + ")")
	}
	if ev.GoVersion, err = r.command(output, opts.goTool, "version"); err != nil {
		return err
	}
	if _, err := r.command(output, opts.goTool, "install", "google.golang.org/protobuf/cmd/protoc-gen-go@"+pluginVersion); err != nil {
		return err
	}
	plugin := filepath.Join(output, "bin", "protoc-gen-go")
	if runtime.GOOS == "windows" {
		plugin += ".exe"
	}
	pluginVersionOut, err := r.command(output, plugin, "--version")
	if err != nil {
		return err
	}
	if fields := strings.Fields(pluginVersionOut); len(fields) == 0 || fields[len(fields)-1] != pluginVersion {
		return errors.New("unexpected protoc-gen-go version")
	}
	if ev.PluginSHA256, err = fileSHA256(plugin); err != nil {
		return err
	}
	schemas, err := relativeFiles(protoRoot, ".proto")
	if err != nil {
		return err
	}
	if len(schemas) == 0 {
		return errors.New("no canonical proto inputs")
	}
	inputs := filepath.Join(output, "proto")
	hashes := map[string]string{}
	for _, relative := range schemas {
		target := filepath.Join(inputs, filepath.FromSlash(relative))
		if err := copyFile(filepath.Join(protoRoot, filepath.FromSlash(relative)), target); err != nil {
			return err
		}
		if hashes[relative], err = fileSHA256(target); err != nil {
			return err
		}
	}
	ev.Inputs = hashes
	generated := filepath.Join(output, "generated")
	if err := os.MkdirAll(generated, 0o755); err != nil {
		return err
	}
	args := []string{"--proto_path=" + inputs, "--plugin=protoc-gen-go=" + plugin, "--go_out=" + generated, "--go_opt=module=" + wireModule}
	if _, err := r.command(output, protoc, append(args, schemas...)...); err != nil {
		return err
	}
	destination, err := filepath.Abs(filepath.FromSlash(generatedGo))
	if err != nil {
		return err
	}
	for _, name := range []string{"go.mod", "go.sum"} {
		if err := copyFile(filepath.Join(destination, name), filepath.Join(generated, name)); err != nil {
			return err
		}
	}
	runtimeVersion, err := r.command(generated, opts.goTool, "list", "-m", "-mod=readonly", "-f", "{{.Version}}", "google.golang.org/protobuf")
	if err != nil {
		return err
	}
	if runtimeVersion != pluginVersion {
		return errors.New("generated wire runtime must match pinned protobuf version")
	}
	for _, check := range [][]string{{"test", "-mod=readonly", "./..."}, {"vet", "-mod=readonly", "./..."}, {"mod", "verify"}} {
		if _, err := r.command(generated, opts.goTool, check...); err != nil {
			return err
		}
	}
	current, err := relativeFiles(destination, ".pb.go")
	if err != nil {
		return err
	}
	expected, err := relativeFiles(generated, ".pb.go")
	if err != nil {
		return err
	}
	ev.Outputs = map[string]string{}
	for _, relative := range expected {
		if ev.Outputs[relative], err = fileSHA256(filepath.Join(generated, filepath.FromSlash(relative))); err != nil {
			return err
		}
	}
	changed, err := differing(destination, current, generated, expected)
	if err != nil {
		return err
	}
	ev.Differences = &changed
	if opts.check && len(changed) > 0 {
		return errors.New("official generated Go drift: " + strings.Join(changed, ", "))
	}
	if !opts.check {
		expectedSet := map[string]bool{}
		for _, relative := range expected {
			expectedSet[relative] = true
			if err := copyFile(filepath.Join(generated, filepath.FromSlash(relative)), filepath.Join(destination, filepath.FromSlash(relative))); err != nil {
				return err
			}
		}
		for _, relative := range current {
			if !expectedSet[relative] {
				if err := os.Remove(filepath.Join(destination, filepath.FromSlash(relative))); err != nil {
					return err
				}
			}
		}
	}
	mode := "generated"
	if opts.check {
		mode = "checked"
	}
	fmt.Printf("Official protobuf Go %s: %d files %s; %s\n", pluginVersion, len(expected), mode, output)
	return nil
}

func writeEvidence(path string, ev *evidence) error {
	var buf bytes.Buffer
	encoder := json.NewEncoder(&buf)
	encoder.SetEscapeHTML(false)
	encoder.SetIndent("", "  ")
	if err := encoder.Encode(ev); err != nil {
		return err
	}
	return os.WriteFile(path, buf.Bytes(), 0o644)
}

func fileSHA256(path string) (string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:]), nil
}

func copyFile(source, target string) error {
	data, err := os.ReadFile(source)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
		return err
	}
	return os.WriteFile(target, data, 0o644)
}

// relativeFiles lists files under dir whose names end with suffix, as sorted
// slash-separated paths relative to dir. A missing dir is an empty set.
func relativeFiles(dir, suffix string) ([]string, error) {
	var found []string
	err := filepath.WalkDir(dir, func(path string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if !entry.IsDir() && strings.HasSuffix(entry.Name(), suffix) {
			relative, err := filepath.Rel(dir, path)
			if err != nil {
				return err
			}
			found = append(found, filepath.ToSlash(relative))
		}
		return nil
	})
	if errors.Is(err, fs.ErrNotExist) {
		err = nil
	}
	sort.Strings(found)
	return found, err
}

// differing returns the sorted relative paths present on one side only or
// whose text differs; line endings are normalized so checkout autocrlf is
// not drift.
func differing(currentDir string, current []string, expectedDir string, expected []string) ([]string, error) {
	changed := []string{}
	expectedSet := map[string]bool{}
	for _, relative := range expected {
		expectedSet[relative] = true
	}
	currentSet := map[string]bool{}
	for _, relative := range current {
		currentSet[relative] = true
		if !expectedSet[relative] {
			changed = append(changed, relative)
		}
	}
	for _, relative := range expected {
		if !currentSet[relative] {
			changed = append(changed, relative)
			continue
		}
		left, err := os.ReadFile(filepath.Join(currentDir, filepath.FromSlash(relative)))
		if err != nil {
			return nil, err
		}
		right, err := os.ReadFile(filepath.Join(expectedDir, filepath.FromSlash(relative)))
		if err != nil {
			return nil, err
		}
		if !bytes.Equal(normalizeNewlines(left), normalizeNewlines(right)) {
			changed = append(changed, relative)
		}
	}
	sort.Strings(changed)
	return changed, nil
}

func normalizeNewlines(text []byte) []byte {
	return bytes.ReplaceAll(bytes.ReplaceAll(text, []byte("\r\n"), []byte("\n")), []byte("\r"), []byte("\n"))
}
