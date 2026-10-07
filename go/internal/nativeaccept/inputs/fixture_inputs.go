package inputs

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// FixtureRoot holds the test fixture sources (scripts/fixtures/<Name>Fixture.cs)
// (no saves are committed; the harness generates them). Which fixture
// source a case depends on follows from the case's Go sources (#170): it
// feeds a case when the case names one of its [Tool("test/...")] ops, while
// any other file directly under it feeds every case.
const FixtureRoot = "scripts/fixtures"

var (
	// fixtureToolAttribute is a fixture op's registration in C#.
	fixtureToolAttribute = regexp.MustCompile(`\[Tool\(\s*"([^"]+)"`)
	// fixtureCompileItem is a fixture source's Compile item in the native
	// project, conditioned on the build flag that admits it.
	fixtureCompileItem = regexp.MustCompile(`scripts/fixtures/([A-Za-z0-9_]+)\.cs"\s+Condition="'\$\(([A-Za-z0-9_]+)\)'\s*==\s*'true'"`)
)

// NativeProject is the native mod's project, repo-relative: the fixture
// sources it compiles are each conditioned on a build flag, and several
// sources share one flag (SleepingFixture.cs under UpkeepFixture).
const NativeProject = "integrations/rimgovernor-native/src/Bridge/RimGovernor.Bridge.csproj"

// fixtureBuildFlags maps each fixture source (file base) NativeProject
// compiles to the build flag admitting it; a missing project maps nothing,
// and a source the project does not list is taken as its own flag.
func fixtureBuildFlags(repo string) map[string]string {
	flags := map[string]string{}
	src, err := os.ReadFile(filepath.Join(repo, filepath.FromSlash(NativeProject)))
	if err != nil {
		return flags
	}
	for _, m := range fixtureCompileItem.FindAllSubmatch(src, -1) {
		flags[string(m[1])] = string(m[2])
	}
	return flags
}

// FixtureNames is every -Fixture name the checkout still builds: each
// class under FixtureRoot plus the build flag the native project maps it
// to. An unreadable fixture root is an error.
func FixtureNames(repo string) (map[string]bool, error) {
	entries, err := os.ReadDir(filepath.Join(repo, filepath.FromSlash(FixtureRoot)))
	if err != nil {
		return nil, fmt.Errorf("fixture sources %s: %w", FixtureRoot, err)
	}
	flags := fixtureBuildFlags(repo)
	names := map[string]bool{}
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".cs") {
			continue
		}
		class := strings.TrimSuffix(entry.Name(), ".cs")
		names[class] = true
		if flag, ok := flags[class]; ok {
			names[flag] = true
		}
	}
	return names, nil
}

// FixtureClasses lists, sorted, the fixture build flags (the names
// build_native_mod.ps1 -Fixture takes and a package manifest records)
// admitting the sources under FixtureRoot that register any of ops as a
// [Tool("test/...")]: what a build must include for a case whose Start
// calls those ops. A source is its own flag unless NativeProject
// conditions it on another one. An op no fixture registers is left out; a
// missing FixtureRoot is an error.
func FixtureClasses(repo string, ops []string) ([]string, error) {
	if len(ops) == 0 {
		return nil, nil
	}
	wanted := map[string]bool{}
	for _, op := range ops {
		wanted[op] = true
	}
	root := filepath.Join(repo, filepath.FromSlash(FixtureRoot))
	entries, err := os.ReadDir(root)
	if err != nil {
		return nil, fmt.Errorf("fixture sources %s: %w", FixtureRoot, err)
	}
	flags := fixtureBuildFlags(repo)
	seen := map[string]bool{}
	var classes []string
	for _, entry := range entries {
		name := entry.Name()
		if entry.IsDir() || !strings.HasSuffix(name, ".cs") {
			continue
		}
		src, err := os.ReadFile(filepath.Join(root, name))
		if err != nil {
			return nil, err
		}
		for _, m := range fixtureToolAttribute.FindAllSubmatch(src, -1) {
			if !wanted[string(m[1])] {
				continue
			}
			class := strings.TrimSuffix(name, ".cs")
			if flag, ok := flags[class]; ok {
				class = flag
			}
			if !seen[class] {
				seen[class] = true
				classes = append(classes, class)
			}
			break
		}
	}
	sort.Strings(classes)
	return classes, nil
}
