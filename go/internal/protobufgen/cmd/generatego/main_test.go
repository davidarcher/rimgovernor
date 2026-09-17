package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestDifferingNormalizesLineEndings(t *testing.T) {
	current, expected := t.TempDir(), t.TempDir()
	write := func(dir, name, text string) {
		path := filepath.Join(dir, filepath.FromSlash(name))
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(text), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	write(current, "apb/a.pb.go", "x\r\ny\r\n")
	write(current, "bpb/b.pb.go", "old\n")
	write(current, "gone/g.pb.go", "")
	write(current, "bpb/notes.txt", "ignored")
	write(expected, "apb/a.pb.go", "x\ny\n")
	write(expected, "bpb/b.pb.go", "new\n")
	write(expected, "newpb/n.pb.go", "")
	currentFiles, err := relativeFiles(current, ".pb.go")
	if err != nil {
		t.Fatal(err)
	}
	expectedFiles, err := relativeFiles(expected, ".pb.go")
	if err != nil {
		t.Fatal(err)
	}
	got, err := differing(current, currentFiles, expected, expectedFiles)
	if err != nil {
		t.Fatal(err)
	}
	if want := "bpb/b.pb.go gone/g.pb.go newpb/n.pb.go"; strings.Join(got, " ") != want {
		t.Fatalf("differing = %v, want %v", got, want)
	}
}

func TestRelativeFilesMissingDirIsEmpty(t *testing.T) {
	got, err := relativeFiles(filepath.Join(t.TempDir(), "missing"), ".pb.go")
	if err != nil || len(got) != 0 {
		t.Fatalf("relativeFiles = %v, %v", got, err)
	}
}

func TestPrivateEnvOverridesInheritedCaches(t *testing.T) {
	t.Setenv("GOCACHE", "/inherited")
	t.Setenv("GOTOOLCHAIN", "")
	env := privateEnv(filepath.FromSlash("/run"), filepath.FromSlash("/run/modcache"))
	seen := map[string]int{}
	for _, entry := range env {
		key, value, _ := strings.Cut(entry, "=")
		seen[key]++
		switch key {
		case "GOCACHE":
			if value != filepath.FromSlash("/run/cache") {
				t.Errorf("GOCACHE = %q", value)
			}
		case "GOTOOLCHAIN":
			if value != "local" {
				t.Errorf("GOTOOLCHAIN = %q", value)
			}
		case "GOWORK":
			if value != "off" {
				t.Errorf("GOWORK = %q", value)
			}
		}
	}
	for _, key := range []string{"GOCACHE", "GOMODCACHE", "GOBIN", "GOWORK", "GOTOOLCHAIN"} {
		if seen[key] != 1 {
			t.Errorf("%s appears %d times", key, seen[key])
		}
	}
}

func TestFindRoot(t *testing.T) {
	root := t.TempDir()
	nested := filepath.Join(root, "go", "internal")
	if err := os.MkdirAll(filepath.Join(root, "contracts", "proto"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(nested, 0o755); err != nil {
		t.Fatal(err)
	}
	if got, err := findRoot(nested); err != nil || got != root {
		t.Fatalf("findRoot = %q, %v; want %q", got, err, root)
	}
}
