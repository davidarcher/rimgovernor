package nativeaccept

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// writeFile writes content at repo-relative path, creating directories.
func writeFile(t *testing.T, root, relative, content string) {
	t.Helper()
	path := filepath.Join(root, filepath.FromSlash(relative))
	if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(content), 0644); err != nil {
		t.Fatal(err)
	}
}

// writeNativeInputs lays down the smallest worktree SourceTreeHash accepts:
// every input present, with build outputs and binaries that must be ignored.
func writeNativeInputs(t *testing.T, repo string) {
	t.Helper()
	for relative, content := range map[string]string{
		"integrations/rimgovernor-native/src/Bridge/Tool.cs":             "class Tool {}",
		"integrations/rimgovernor-native/src/Bridge/obj/Tool.cs":         "ignored build output",
		"integrations/rimgovernor-native/src/Bridge/bin/Release/x.dll":   "ignored binary",
		"integrations/rimgovernor-native/src/Assemblies/RimGovernor.dll": "ignored installed assembly",
		"integrations/rimgovernor-native/About/About.xml":                "<ModMetaData/>",
		"integrations/rimgovernor-native/Notices/headless/LICENSE":       "MIT",
		"integrations/rimgovernor-native/README.md":                      "readme",
		"contracts/proto/clock.proto":                                    "syntax = \"proto3\";",
		"contracts/generated/protobuf/csharp/Clock.cs":                   "generated",
		"contracts/generated/protobuf/csharp/obj/Clock.cs":               "ignored",
		"tools/protobuf/go/main.go":                                      "package main",
		"tools/protobuf/protoc.exe":                                      "ignored binary",
		"scripts/build_native_mod.ps1":                                   "param()",
		"go/internal/protobufgen/cmd/generatecsharp/main.go":             "package main // generator",
		"scripts/fixtures/QuietStorytellerFixture.cs":                    "class Quiet {}",
		"scripts/fixtures/Fixtures.csproj":                               "<Project/>",
		"scripts/fixtures/saves/RimGovernor-tribal8-baseline.rws":        "ignored save",
		"scripts/fixtures/obj/QuietStorytellerFixture.cs":                "ignored",
		"THIRD_PARTY.md": "notices",
		"integrations/rimgovernor-native/src/Bridge/BridgeTools/x/tool.dll": "ignored",
	} {
		writeFile(t, repo, relative, content)
	}
}

func sha(content string) string {
	sum := sha256.Sum256([]byte(content))
	return hex.EncodeToString(sum[:])
}

func TestSourceTreeHashMatchesTheBuildScriptsFormula(t *testing.T) {
	repo := t.TempDir()
	writeNativeInputs(t, repo)
	got, err := SourceTreeHash(repo)
	if err != nil {
		t.Fatal(err)
	}
	// The copy-relative names, sorted ordinally, each with its content hash:
	// exactly what build_native_mod.ps1 hashes over build/source.
	lines := []string{
		"THIRD_PARTY.md\t" + sha("notices"),
		"contracts/generated/protobuf/csharp/Clock.cs\t" + sha("generated"),
		"contracts/proto/clock.proto\t" + sha("syntax = \"proto3\";"),
		"integrations/rimgovernor-native/About/About.xml\t" + sha("<ModMetaData/>"),
		"integrations/rimgovernor-native/Notices/headless/LICENSE\t" + sha("MIT"),
		"integrations/rimgovernor-native/README.md\t" + sha("readme"),
		"integrations/rimgovernor-native/src/Bridge/Tool.cs\t" + sha("class Tool {}"),
		"scripts/build_native_mod.ps1\t" + sha("param()"),
		"scripts/fixtures/Fixtures.csproj\t" + sha("<Project/>"),
		"scripts/fixtures/QuietStorytellerFixture.cs\t" + sha("class Quiet {}"),
		"scripts/generate_protobuf.go\t" + sha("package main // generator"),
		"tools/protobuf/go/main.go\t" + sha("package main"),
	}
	want := sha(strings.Join(lines, "\n") + "\n")
	if got != want {
		t.Fatalf("SourceTreeHash = %s, want %s", got, want)
	}
	// A source edit changes it; an edit to an ignored build output does not.
	writeFile(t, repo, "integrations/rimgovernor-native/src/Bridge/obj/Tool.cs", "rebuilt")
	if again, _ := SourceTreeHash(repo); again != got {
		t.Fatal("an obj/ change moved the hash")
	}
	writeFile(t, repo, "scripts/fixtures/QuietStorytellerFixture.cs", "class Quiet { int x; }")
	if again, _ := SourceTreeHash(repo); again == got {
		t.Fatal("a fixture source change did not move the hash")
	}
}

func TestSourceTreeHashRequiresEveryInput(t *testing.T) {
	repo := t.TempDir()
	writeNativeInputs(t, repo)
	if err := os.RemoveAll(filepath.Join(repo, "contracts", "proto")); err != nil {
		t.Fatal(err)
	}
	if _, err := SourceTreeHash(repo); err == nil || !strings.Contains(err.Error(), "contracts/proto") {
		t.Fatalf("expected a missing-input error naming contracts/proto, got %v", err)
	}
}

// inRepo runs fn with the working directory inside a fake checkout at repo.
func inRepo(t *testing.T, repo string, fn func()) {
	t.Helper()
	if err := os.MkdirAll(filepath.Join(repo, ".git"), 0755); err != nil {
		t.Fatal(err)
	}
	inner := filepath.Join(repo, "go", "internal", "nativeaccept", "cmd", "x")
	if err := os.MkdirAll(inner, 0755); err != nil {
		t.Fatal(err)
	}
	wd, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Chdir(inner); err != nil {
		t.Fatal(err)
	}
	defer os.Chdir(wd)
	fn()
}

func writeManifest(t *testing.T, pkg, body string) {
	t.Helper()
	writeFile(t, pkg, PackageManifestName, body)
}

func TestRequireCurrentPackageAcceptsAMatchingSourceTree(t *testing.T) {
	t.Setenv(AllowStaleModEnv, "")
	repo := t.TempDir()
	writeNativeInputs(t, repo)
	current, err := SourceTreeHash(repo)
	if err != nil {
		t.Fatal(err)
	}
	pkg := filepath.Join(t.TempDir(), "RimGovernor")
	writeManifest(t, pkg, `{"packageId":"davidarcher.rimgovernor.native","role":"fixture","fixtures":["QuietStorytellerFixture"],"sourceRevision":"abc","sourceDirty":false,"sourceTree":"`+current+`"}`)
	inRepo(t, repo, func() {
		summary, err := RequireCurrentPackage(pkg)
		if err != nil {
			t.Fatalf("matching build refused: %v", err)
		}
		if summary["checked"] != true || summary["method"] != "source_tree" {
			t.Fatalf("summary %v", summary)
		}
	})
}

func TestRequireCurrentPackageRefusesAStaleSourceTree(t *testing.T) {
	t.Setenv(AllowStaleModEnv, "")
	repo := t.TempDir()
	writeNativeInputs(t, repo)
	pkg := filepath.Join(t.TempDir(), "RimGovernor")
	writeManifest(t, pkg, `{"packageId":"davidarcher.rimgovernor.native","role":"fixture","fixtures":["MedicalManagementFixture","QuietStorytellerFixture"],"sourceRevision":"0123456789abcdef","sourceTree":"not-this-tree"}`)
	inRepo(t, repo, func() {
		summary, err := RequireCurrentPackage(pkg)
		if !errors.Is(err, ErrStalePackage) {
			t.Fatalf("expected ErrStalePackage, got %v", err)
		}
		for _, want := range []string{"0123456789ab", "-Fixture MedicalManagementFixture,QuietStorytellerFixture", AllowStaleModEnv, pkg} {
			if !strings.Contains(err.Error(), want) {
				t.Errorf("error lacks %q: %v", want, err)
			}
		}
		if summary["checked"] != true {
			t.Fatalf("summary %v", summary)
		}
	})
	// The override runs anyway and says so.
	t.Setenv(AllowStaleModEnv, "1")
	inRepo(t, repo, func() {
		summary, err := RequireCurrentPackage(pkg)
		if err != nil {
			t.Fatal(err)
		}
		if summary["checked"] != false || !strings.Contains(summary["skipped"].(string), AllowStaleModEnv) {
			t.Fatalf("summary %v", summary)
		}
	})
}

func TestRequireCurrentPackageSkipsWithoutManifestOrCheckout(t *testing.T) {
	t.Setenv(AllowStaleModEnv, "")
	pkg := filepath.Join(t.TempDir(), "RimGovernor")
	if err := os.MkdirAll(pkg, 0755); err != nil {
		t.Fatal(err)
	}
	summary, err := RequireCurrentPackage(pkg)
	if err != nil || summary["checked"] != false || !strings.Contains(summary["skipped"].(string), "no manifest") {
		t.Fatalf("no manifest: %v %v", summary, err)
	}
	// A manifest but no enclosing checkout (a temp dir has no .git above it
	// unless the system temp lives inside one).
	writeManifest(t, pkg, `{"sourceTree":"x"}`)
	outside := t.TempDir()
	if _, found := FindRepo(outside); found {
		t.Skip("temp dir lies inside a checkout")
	}
	wd, _ := os.Getwd()
	if err := os.Chdir(outside); err != nil {
		t.Fatal(err)
	}
	defer os.Chdir(wd)
	summary, err = RequireCurrentPackage(pkg)
	if err != nil || summary["checked"] != false || !strings.Contains(summary["skipped"].(string), "no enclosing checkout") {
		t.Fatalf("no checkout: %v %v", summary, err)
	}
}

func TestFindRepoAcceptsAWorktreeGitFile(t *testing.T) {
	root := t.TempDir()
	writeFile(t, root, ".git", "gitdir: ../main/.git/worktrees/x")
	inner := filepath.Join(root, "go", "internal")
	if err := os.MkdirAll(inner, 0755); err != nil {
		t.Fatal(err)
	}
	repo, ok := FindRepo(inner)
	if !ok || repo != root {
		t.Fatalf("FindRepo = %q, %v", repo, ok)
	}
}

// The rebuild hint lists the case's own fixtures beside the installed
// build's: following the installed list alone left farm/select-hydroponics
// without FarmEnvironmentFixture (#208).
func TestRequireCurrentPackageHintNamesTheRunsFixtures(t *testing.T) {
	t.Setenv(AllowStaleModEnv, "")
	repo := t.TempDir()
	writeNativeInputs(t, repo)
	writeFile(t, repo, "scripts/fixtures/FarmEnvironmentFixture.cs", `[Tool("test/farm_environment_prepare")] class Farm {}`)
	writeFile(t, repo, "scripts/fixtures/PowerFixture.cs", `[Tool("test/power_prepare")] class Power {}`)
	pkg := filepath.Join(t.TempDir(), "RimGovernor")
	writeManifest(t, pkg, `{"packageId":"davidarcher.rimgovernor.native","role":"fixture","fixtures":["QuietStorytellerFixture","PowerFixture"],"sourceRevision":"0123456789abcdef","sourceTree":"not-this-tree"}`)
	inRepo(t, repo, func() {
		_, err := RequireCurrentPackage(pkg, "test/farm_environment_prepare", "test/power_prepare", "test/unregistered")
		if !errors.Is(err, ErrStalePackage) {
			t.Fatalf("expected ErrStalePackage, got %v", err)
		}
		if want := "-Fixture FarmEnvironmentFixture,PowerFixture,QuietStorytellerFixture "; !strings.Contains(err.Error(), want) {
			t.Fatalf("error lacks %q: %v", want, err)
		}
		if hint := FixtureBuildHint("test/farm_environment_prepare"); hint != " -Fixture FarmEnvironmentFixture" {
			t.Fatalf("FixtureBuildHint = %q", hint)
		}
		if hint := FixtureBuildHint("test/unregistered"); hint != "" {
			t.Fatalf("FixtureBuildHint for an unregistered op = %q", hint)
		}
	})
}
