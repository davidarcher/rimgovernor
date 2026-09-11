// Command contractgen publishes or checks manifest-owned generated contracts.
package main

import (
	"bytes"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/contractgen"
)

type output struct {
	path string
	data []byte
}

func run(args []string, out, errors io.Writer) int {
	flags := flag.NewFlagSet("contractgen", flag.ContinueOnError)
	flags.SetOutput(errors)
	rootFlag := flags.String("root", ".", "repository root; manifest paths are relative to this directory")
	check := flags.Bool("check", false, "compare generated files without writing")
	if err := flags.Parse(args); err != nil {
		return 2
	}
	if flags.NArg() != 1 {
		fmt.Fprintln(errors, "usage: contractgen [-root directory] [-check] <manifest.json>")
		return 2
	}
	root, err := filepath.Abs(*rootFlag)
	if err == nil {
		root, err = filepath.EvalSymlinks(root)
	}
	if err != nil {
		fmt.Fprintf(errors, "resolve repository root: %v\n", err)
		return 1
	}
	outputs, err := prepare(root, flags.Arg(0))
	if err != nil {
		fmt.Fprintln(errors, err)
		return 1
	}
	// Validate every schema and destination before any output directory is created.
	for _, entry := range outputs {
		if *check {
			current, err := os.ReadFile(entry.path)
			if err != nil || !bytes.Equal(current, entry.data) {
				fmt.Fprintf(errors, "generated contract drift: %s\n", entry.path)
				return 1
			}
		} else if err := publish(entry); err != nil {
			fmt.Fprintln(errors, err)
			return 1
		}
	}
	fmt.Fprintf(out, "verified %d generated contract files\n", len(outputs))
	return 0
}

func prepare(root, manifestName string) ([]output, error) {
	manifestPath, err := containedPath(root, manifestName)
	if err != nil {
		return nil, err
	}
	data, err := readInput(manifestPath)
	if err != nil {
		return nil, err
	}
	manifest, err := contractgen.ParseManifest(data)
	if err != nil {
		return nil, err
	}
	outputs := make([]output, 0, len(manifest.Contracts))
	packages := map[string]string{}
	symbols := map[string]map[string]bool{}
	for _, entry := range manifest.Contracts {
		path, err := containedPath(root, entry.GoOutput)
		if err != nil {
			return nil, err
		}
		if strings.EqualFold(path, manifestPath) {
			return nil, fmt.Errorf("output would overwrite manifest")
		}
		if info, err := os.Stat(path); err == nil && !info.Mode().IsRegular() {
			return nil, fmt.Errorf("output is not a regular file: %s", path)
		}
		schemaPath, err := containedPath(root, entry.Schema)
		if err != nil {
			return nil, err
		}
		data, err := readInput(schemaPath)
		if err != nil {
			return nil, err
		}
		schema, err := contractgen.ParseSchema(data)
		if err != nil {
			return nil, fmt.Errorf("%s: %w", entry.Schema, err)
		}
		directory := strings.ToLower(filepath.Dir(path))
		if prior, exists := packages[directory]; exists && prior != entry.GoPackage {
			return nil, fmt.Errorf("conflicting Go packages in output directory: %s", directory)
		}
		packages[directory] = entry.GoPackage
		if symbols[directory] == nil {
			symbols[directory] = map[string]bool{}
		}
		names := []string{schema.Title}
		for name := range schema.Definitions {
			names = append(names, name)
		}
		for _, name := range names {
			for _, symbol := range []string{name, "Decode" + name} {
				if symbols[directory][symbol] {
					return nil, fmt.Errorf("duplicate generated Go symbol: %s", symbol)
				}
				symbols[directory][symbol] = true
			}
		}
		generated, err := contractgen.GenerateGo(schema, contractgen.GoOptions{Package: entry.GoPackage, SchemaPath: entry.Schema})
		if err != nil {
			return nil, err
		}
		outputs = append(outputs, output{path: path, data: generated})
	}
	return outputs, nil
}

func readInput(path string) ([]byte, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	data, err := io.ReadAll(io.LimitReader(file, (1<<20)+1))
	if err == nil && len(data) > 1<<20 {
		return nil, fmt.Errorf("generator input exceeds 1 MiB: %s", path)
	}
	return data, err
}

// Refuse traversal and symlinks, including existing ancestors of new outputs.
func containedPath(root, name string) (string, error) {
	if !filepath.IsLocal(name) || strings.ContainsAny(name, "\\:\x00\r\n") {
		return "", fmt.Errorf("invalid relative path: %q", name)
	}
	path := root
	for _, part := range strings.Split(name, "/") {
		if part == "" || part == "." || part == ".." {
			return "", fmt.Errorf("invalid relative path: %q", name)
		}
		path = filepath.Join(path, part)
		info, err := os.Lstat(path)
		if err != nil && !os.IsNotExist(err) {
			return "", err
		}
		if err == nil && info.Mode()&os.ModeSymlink != 0 {
			return "", fmt.Errorf("symlink in contract path: %s", path)
		}
	}
	return path, nil
}

func publish(entry output) error {
	if err := os.MkdirAll(filepath.Dir(entry.path), 0755); err != nil {
		return err
	}
	file, err := os.CreateTemp(filepath.Dir(entry.path), ".contractgen-*")
	if err != nil {
		return err
	}
	temporary := file.Name()
	defer os.Remove(temporary)
	if _, err = file.Write(entry.data); err != nil {
		file.Close()
		return err
	}
	if err = file.Close(); err != nil {
		return err
	}
	return os.Rename(temporary, entry.path)
}

func main() { os.Exit(run(os.Args[1:], os.Stdout, os.Stderr)) }
