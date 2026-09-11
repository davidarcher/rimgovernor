package main

import (
	"bytes"
	"os"
	"path/filepath"
	"testing"
)

const testSchema = `{"title":"Request","type":"object","additionalProperties":false,"required":["enabled"],"properties":{"enabled":{"type":"boolean"}}}`

func write(t *testing.T, root, name, data string) {
	t.Helper()
	if err := os.WriteFile(filepath.Join(root, name), []byte(data), 0600); err != nil {
		t.Fatal(err)
	}
}

func TestGenerateCheckAndDrift(t *testing.T) {
	root := t.TempDir()
	write(t, root, "schema.json", testSchema)
	write(t, root, "manifest.json", `{"version":1,"contracts":[{"schema":"schema.json","go_package":"wire","go_output":"new/request.go"}]}`)
	var out, errors bytes.Buffer
	if run([]string{"-root", root, "-check", "manifest.json"}, &out, &errors) != 1 {
		t.Fatal("missing output accepted")
	}
	if _, err := os.Stat(filepath.Join(root, "new")); !os.IsNotExist(err) {
		t.Fatal("check created output directory")
	}
	if got := run([]string{"-root", root, "manifest.json"}, &out, &errors); got != 0 {
		t.Fatalf("generate: %d %s", got, &errors)
	}
	if got := run([]string{"-root", root, "-check", "manifest.json"}, &out, &errors); got != 0 {
		t.Fatalf("check: %d %s", got, &errors)
	}
	write(t, root, "new/request.go", "changed")
	if run([]string{"-root", root, "-check", "manifest.json"}, &out, &errors) != 1 {
		t.Fatal("changed output accepted")
	}
	data, _ := os.ReadFile(filepath.Join(root, "new/request.go"))
	if string(data) != "changed" {
		t.Fatal("check rewrote output")
	}
}

func TestAllInputsValidateBeforePublication(t *testing.T) {
	root := t.TempDir()
	write(t, root, "schema.json", testSchema)
	write(t, root, "bad.json", `{"unsupported":true}`)
	write(t, root, "manifest.json", `{"version":1,"contracts":[{"schema":"schema.json","go_package":"wire","go_output":"new/request.go"},{"schema":"bad.json","go_package":"other","go_output":"other/request.go"}]}`)
	var out, errors bytes.Buffer
	if run([]string{"-root", root, "manifest.json"}, &out, &errors) != 1 {
		t.Fatal("invalid second schema accepted")
	}
	if _, err := os.Stat(filepath.Join(root, "new")); !os.IsNotExist(err) {
		t.Fatal("partial publication before validation")
	}
}

func TestPathEscapeRefused(t *testing.T) {
	root := t.TempDir()
	for _, name := range []string{"../outside.go", "/absolute.go", "x/../out.go", `x\out.go`, "C:/out.go"} {
		if _, err := containedPath(root, name); err == nil {
			t.Fatalf("accepted %s", name)
		}
	}
}

func TestSymlinkDestinationRefused(t *testing.T) {
	root, outside := t.TempDir(), t.TempDir()
	if err := os.Symlink(outside, filepath.Join(root, "link")); err != nil {
		t.Skipf("symlink creation unavailable: %v", err)
	}
	if _, err := containedPath(root, "link/request.go"); err == nil {
		t.Fatal("symlink destination accepted")
	}
}

func TestOutputSymbolCollisionRefusedBeforeWriting(t *testing.T) {
	root := t.TempDir()
	write(t, root, "schema.json", testSchema)
	write(t, root, "manifest.json", `{"version":1,"contracts":[{"schema":"schema.json","go_package":"wire","go_output":"new/request.go"},{"schema":"schema.json","go_package":"wire","go_output":"new/other.go"}]}`)
	var out, errors bytes.Buffer
	if run([]string{"-root", root, "manifest.json"}, &out, &errors) != 1 {
		t.Fatal("duplicate public types accepted")
	}
	if _, err := os.Stat(filepath.Join(root, "new")); !os.IsNotExist(err) {
		t.Fatal("collision caused partial write")
	}
}

func TestEveryLanguageOutputParticipatesInDriftCheck(t *testing.T) {
	root := t.TempDir()
	write(t, root, "schema.json", testSchema)
	write(t, root, "manifest.json", `{"version":1,"contracts":[{"schema":"schema.json","go_package":"wire","go_output":"new/request.go","python_output":"new/request.py","csharp_output":"new/Request.cs","csharp_namespace":"Example"}]}`)
	var out, errors bytes.Buffer
	for _, name := range []string{"new/request.go", "new/request.py", "new/Request.cs"} {
		if run([]string{"-root", root, "manifest.json"}, &out, &errors) != 0 {
			t.Fatal(errors.String())
		}
		write(t, root, name, "altered")
		if run([]string{"-root", root, "-check", "manifest.json"}, &out, &errors) != 1 {
			t.Fatalf("unchecked output %s", name)
		}
		actual, err := os.ReadFile(filepath.Join(root, name))
		if err != nil || string(actual) != "altered" {
			t.Fatalf("check changed %s", name)
		}
	}
}

func TestLanguageFailurePreventsAllPublication(t *testing.T) {
	root := t.TempDir()
	write(t, root, "schema.json", `{"title":"Decode","type":"boolean"}`)
	write(t, root, "manifest.json", `{"version":1,"contracts":[{"schema":"schema.json","go_package":"wire","go_output":"new/request.go","csharp_output":"new/Request.cs","csharp_namespace":"Example"}]}`)
	var out, errors bytes.Buffer
	if run([]string{"-root", root, "manifest.json"}, &out, &errors) != 1 {
		t.Fatal("invalid C# name accepted")
	}
	if _, err := os.Stat(filepath.Join(root, "new")); !os.IsNotExist(err) {
		t.Fatal("backend failure partially published outputs")
	}
}
