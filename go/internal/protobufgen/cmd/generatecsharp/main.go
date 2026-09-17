// Command generatecsharp generates canonical Protobuf C# with the pinned
// official protoc and optionally proves the net472 round trip.
//
// It orchestrates NuGet, protoc and dotnet; it does not interpret schemas or
// emit language source. Outputs and native runtime behavior belong to official
// Protobuf. The file is a self-contained stdlib program so the native build
// can bundle it beside the pinned tool project and run it with `go run`.
//
// It runs from the repository root, located by walking up from the working
// directory (override with -root); relative flag paths resolve there.
package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/xml"
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
	expectedProtoc  = "libprotoc 30.0"
	commandTimeout  = 10 * time.Minute
	projectRelative = "tools/protobuf/ProtobufProof.csproj"
	generatedCSharp = "contracts/generated/protobuf/csharp"
	protoRelative   = "contracts/proto"
)

type options struct {
	dotnet              string
	output              string
	check               bool
	proof               bool
	crossLanguageInputs string
}

func main() {
	var opts options
	root := flag.String("root", "", "repository root (default: nearest ancestor of the working directory containing contracts/proto)")
	flag.StringVar(&opts.dotnet, "dotnet", "dotnet", "dotnet executable")
	flag.StringVar(&opts.output, "output", "", "fresh private restore/build artifact directory (default .rimgovernor/protobuf-<random>)")
	flag.BoolVar(&opts.check, "check", false, "fail on generated drift without changing checked-in files")
	flag.BoolVar(&opts.proof, "proof", false, "compile and run net472 round trips; requires .NET Framework on Windows or Mono elsewhere")
	flag.StringVar(&opts.crossLanguageInputs, "cross-language-inputs", "", "directory containing go-request/go-reply .json and .bin proof artifacts")
	flag.Parse()
	if opts.crossLanguageInputs != "" && !opts.proof {
		fmt.Fprintln(os.Stderr, "-cross-language-inputs requires -proof")
		os.Exit(2)
	}
	if err := enterRoot(*root); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	if opts.output == "" {
		opts.output = filepath.Join(".rimgovernor", "protobuf-"+randomHex(16))
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

func randomHex(n int) string {
	b := make([]byte, n)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return hex.EncodeToString(b)
}

func run(opts options) error {
	output, err := filepath.Abs(opts.output)
	if err != nil {
		return err
	}
	if err := makeFresh(output); err != nil {
		return err
	}
	private := filepath.Join(output, "source")
	for _, relative := range []string{projectRelative, "tools/protobuf/Program.cs", "tools/protobuf/packages.lock.json"} {
		if err := copyFile(filepath.FromSlash(relative), filepath.Join(private, filepath.FromSlash(relative))); err != nil {
			return err
		}
	}
	project := filepath.Join(private, filepath.FromSlash(projectRelative))
	// Restore/build state stays in this run's private tree; the committed lock
	// captures both direct versions and the runtime dependency closure.
	if _, err := command(private, false, opts.dotnet, "restore", project, "--locked-mode", "--source", "https://api.nuget.org/v3/index.json"); err != nil {
		return err
	}
	packages, err := command(private, true, opts.dotnet, "msbuild", project, "-getProperty:NuGetPackageRoot")
	if err != nil {
		return err
	}
	grpcVersion, err := packageVersion(project, "Grpc.Tools")
	if err != nil {
		return err
	}
	grpc := filepath.Join(packages, "grpc.tools", grpcVersion)
	platform, err := protocPlatform(runtime.GOOS, runtime.GOARCH)
	if err != nil {
		return err
	}
	compiler := filepath.Join(grpc, "tools", platform, exeName("protoc"))
	version, err := command(private, true, compiler, "--version")
	if err != nil {
		return err
	}
	if version != expectedProtoc {
		return fmt.Errorf("unexpected official compiler: %s; expected %s", version, expectedProtoc)
	}
	generated := filepath.Join(private, filepath.FromSlash(generatedCSharp))
	if err := os.MkdirAll(generated, 0o755); err != nil {
		return err
	}
	proto := filepath.Join(private, filepath.FromSlash(protoRelative))
	if err := os.CopyFS(proto, os.DirFS(filepath.FromSlash(protoRelative))); err != nil {
		return err
	}
	schemas, err := relativeFiles(proto, ".proto")
	if err != nil {
		return err
	}
	if len(schemas) == 0 {
		return errors.New("no canonical .proto files")
	}
	args := []string{"--proto_path=" + proto, "--proto_path=" + filepath.Join(grpc, "build", "native", "include"),
		"--csharp_out=" + generated, "--descriptor_set_out=" + filepath.Join(output, "contracts.pb"), "--include_imports"}
	if _, err := command(private, false, compiler, append(args, schemas...)...); err != nil {
		return err
	}
	destination := filepath.FromSlash(generatedCSharp)
	current, err := filesByName(destination, ".cs")
	if err != nil {
		return err
	}
	expected, err := filesByName(generated, ".cs")
	if err != nil {
		return err
	}
	differences, err := differing(current, expected)
	if err != nil {
		return err
	}
	if opts.check && len(differences) > 0 {
		return errors.New("official generated C# drift: " + strings.Join(differences, ", "))
	}
	if !opts.check {
		if err := os.MkdirAll(destination, 0o755); err != nil {
			return err
		}
		for name, path := range expected {
			if err := copyFile(path, filepath.Join(destination, name)); err != nil {
				return err
			}
		}
		for name, path := range current {
			if _, keep := expected[name]; !keep {
				if err := os.Remove(path); err != nil {
					return err
				}
			}
		}
	}
	if opts.proof {
		if _, err := command(private, false, opts.dotnet, "build", project, "--no-restore", "-c", "Release"); err != nil {
			return err
		}
		executable := filepath.Join(filepath.Dir(project), "bin", "Release", "net472", "ProtobufProof.exe")
		runner := []string{executable}
		if runtime.GOOS != "windows" {
			runner = []string{"mono", executable}
		}
		runner = append(runner, filepath.Join(output, "roundtrip"))
		if opts.crossLanguageInputs != "" {
			inputs, err := filepath.Abs(opts.crossLanguageInputs)
			if err != nil {
				return err
			}
			runner = append(runner, inputs)
		}
		if _, err := command(private, false, runner[0], runner[1:]...); err != nil {
			return err
		}
	}
	mode := "generated"
	if opts.check {
		mode = "checked"
	}
	fmt.Printf("%s: %d schemas, %d C# files; %s. Artifacts: %s\n", version, len(schemas), len(expected), mode, output)
	return nil
}

// command runs one bounded subprocess. Its stderr streams through; stdout is
// returned trimmed when captured and streamed otherwise.
func command(cwd string, capture bool, name string, args ...string) (string, error) {
	ctx, cancel := context.WithTimeout(context.Background(), commandTimeout)
	defer cancel()
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Dir = cwd
	cmd.Stderr = os.Stderr
	var stdout bytes.Buffer
	if capture {
		cmd.Stdout = &stdout
	} else {
		cmd.Stdout = os.Stdout
	}
	if err := cmd.Run(); err != nil {
		return "", fmt.Errorf("%s %s: %w", name, strings.Join(args, " "), err)
	}
	return strings.TrimSpace(stdout.String()), nil
}

// packageVersion reads a PackageReference's pinned Version from the project,
// tolerating the exact-range form "[x.y.z]".
func packageVersion(project, name string) (string, error) {
	data, err := os.ReadFile(project)
	if err != nil {
		return "", err
	}
	return packageVersionFrom(data, name)
}

func packageVersionFrom(project []byte, name string) (string, error) {
	decoder := xml.NewDecoder(bytes.NewReader(project))
	for {
		token, err := decoder.Token()
		if err != nil {
			break
		}
		start, ok := token.(xml.StartElement)
		if !ok || start.Name.Local != "PackageReference" {
			continue
		}
		var include, version string
		for _, attr := range start.Attr {
			switch attr.Name.Local {
			case "Include":
				include = attr.Value
			case "Version":
				version = attr.Value
			}
		}
		if include == name {
			return strings.Trim(version, "[]"), nil
		}
	}
	return "", fmt.Errorf("pinned package missing: %s", name)
}

// protocPlatform names the Grpc.Tools tools/ subdirectory for this OS/CPU.
func protocPlatform(goos, goarch string) (string, error) {
	switch goos + "/" + goarch {
	case "windows/amd64":
		return "windows_x64", nil
	case "windows/386":
		return "windows_x86", nil
	case "darwin/amd64":
		return "macosx_x64", nil
	case "linux/arm64":
		return "linux_arm64", nil
	case "linux/amd64":
		return "linux_x64", nil
	}
	return "", fmt.Errorf("unsupported protoc platform: %s %s", goos, goarch)
}

func exeName(name string) string {
	if runtime.GOOS == "windows" {
		return name + ".exe"
	}
	return name
}

// makeFresh creates dir and fails if it already exists.
func makeFresh(dir string) error {
	if _, err := os.Stat(dir); err == nil {
		return fmt.Errorf("output directory already exists: %s", dir)
	}
	return os.MkdirAll(dir, 0o755)
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

// relativeFiles lists files under dir with the extension, as sorted
// slash-separated paths relative to dir.
func relativeFiles(dir, ext string) ([]string, error) {
	var found []string
	err := filepath.WalkDir(dir, func(path string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if !entry.IsDir() && filepath.Ext(path) == ext {
			relative, err := filepath.Rel(dir, path)
			if err != nil {
				return err
			}
			found = append(found, filepath.ToSlash(relative))
		}
		return nil
	})
	sort.Strings(found)
	return found, err
}

// filesByName maps the file names directly under dir with the extension to
// their paths. A missing dir is an empty set.
func filesByName(dir, ext string) (map[string]string, error) {
	entries, err := os.ReadDir(dir)
	if errors.Is(err, fs.ErrNotExist) {
		return map[string]string{}, nil
	}
	if err != nil {
		return nil, err
	}
	files := map[string]string{}
	for _, entry := range entries {
		if !entry.IsDir() && filepath.Ext(entry.Name()) == ext {
			files[entry.Name()] = filepath.Join(dir, entry.Name())
		}
	}
	return files, nil
}

// differing returns the sorted names present on one side only or whose text
// differs; line endings are normalized so checkout autocrlf is not drift.
func differing(current, expected map[string]string) ([]string, error) {
	var names []string
	for name := range current {
		if _, ok := expected[name]; !ok {
			names = append(names, name)
		}
	}
	for name, path := range expected {
		existing, ok := current[name]
		if !ok {
			names = append(names, name)
			continue
		}
		same, err := sameText(existing, path)
		if err != nil {
			return nil, err
		}
		if !same {
			names = append(names, name)
		}
	}
	sort.Strings(names)
	return names, nil
}

func sameText(a, b string) (bool, error) {
	left, err := os.ReadFile(a)
	if err != nil {
		return false, err
	}
	right, err := os.ReadFile(b)
	if err != nil {
		return false, err
	}
	return bytes.Equal(normalizeNewlines(left), normalizeNewlines(right)), nil
}

func normalizeNewlines(text []byte) []byte {
	return bytes.ReplaceAll(bytes.ReplaceAll(text, []byte("\r\n"), []byte("\n")), []byte("\r"), []byte("\n"))
}
