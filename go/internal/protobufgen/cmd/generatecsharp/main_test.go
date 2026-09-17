package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestPackageVersionFrom(t *testing.T) {
	project := []byte(`<Project><ItemGroup>
  <PackageReference Include="Google.Protobuf" Version="3.31.1" />
  <PackageReference Include="Grpc.Tools" Version="[2.72.0]" PrivateAssets="all" />
</ItemGroup></Project>`)
	got, err := packageVersionFrom(project, "Grpc.Tools")
	if err != nil || got != "2.72.0" {
		t.Fatalf("Grpc.Tools = %q, %v", got, err)
	}
	if _, err := packageVersionFrom(project, "Missing"); err == nil {
		t.Fatal("expected missing package error")
	}
}

func TestProtocPlatform(t *testing.T) {
	for _, tc := range []struct{ goos, goarch, want string }{
		{"windows", "amd64", "windows_x64"}, {"windows", "386", "windows_x86"},
		{"darwin", "amd64", "macosx_x64"}, {"linux", "arm64", "linux_arm64"}, {"linux", "amd64", "linux_x64"},
	} {
		if got, err := protocPlatform(tc.goos, tc.goarch); err != nil || got != tc.want {
			t.Errorf("%s/%s = %q, %v; want %q", tc.goos, tc.goarch, got, err, tc.want)
		}
	}
	if _, err := protocPlatform("plan9", "mips"); err == nil {
		t.Error("expected unsupported platform error")
	}
}

func TestDifferingNormalizesLineEndings(t *testing.T) {
	dir := t.TempDir()
	write := func(name, text string) string {
		path := filepath.Join(dir, name)
		if err := os.WriteFile(path, []byte(text), 0o644); err != nil {
			t.Fatal(err)
		}
		return path
	}
	current := map[string]string{"A.cs": write("a1", "x\r\ny\r\n"), "B.cs": write("b1", "old\n"), "Gone.cs": write("g", "")}
	expected := map[string]string{"A.cs": write("a2", "x\ny\n"), "B.cs": write("b2", "new\n"), "New.cs": write("n", "")}
	got, err := differing(current, expected)
	if err != nil {
		t.Fatal(err)
	}
	want := []string{"B.cs", "Gone.cs", "New.cs"}
	if len(got) != len(want) {
		t.Fatalf("differing = %v, want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("differing = %v, want %v", got, want)
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
	if _, err := findRoot(t.TempDir()); err == nil {
		t.Fatal("expected no-root error")
	}
}
