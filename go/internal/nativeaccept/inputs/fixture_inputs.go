package inputs

import (
	"bytes"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// FixtureRoot holds the test fixture sources (scripts/fixtures/<Name>Fixture.cs),
// their build files and the committed checkpoint saves (saves/). Which of
// them a case depends on follows from the case's Go sources (#170): a
// fixture source feeds a case when the case names one of its
// [Tool("test/...")] ops, a save when the case names it, while the build
// files (the .csproj, Taskfile, lock file) feed every case.
const FixtureRoot = "scripts/fixtures"

// fixtureSavesDir is where committed checkpoint saves live under FixtureRoot.
const fixtureSavesDir = "saves"

var (
	// goStringLiteral is an interpreted Go string literal without escapes:
	// tool names and save names are plain identifiers, so this is enough
	// to find them.
	goStringLiteral = regexp.MustCompile(`"([^"\\\n]*)"`)
	// fixtureToolAttribute is a fixture op's registration in C#.
	fixtureToolAttribute = regexp.MustCompile(`\[Tool\(\s*"([^"]+)"`)
	// fixtureCompileItem is a fixture source's Compile item in the native
	// project, conditioned on the build flag that admits it.
	fixtureCompileItem = regexp.MustCompile(`scripts/fixtures/([A-Za-z0-9_]+)\.cs"\s+Condition="'\$\(([A-Za-z0-9_]+)\)'\s*==\s*'true'"`)
)

// NativeProject is the native mod's project, repo-relative: the fixture
// sources it compiles are each conditioned on a build flag, and several
// sources share one flag (ComfortFixture.cs under UpkeepFixture).
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

// FixtureRefs are the names a set of Go sources mention as string
// literals; FixtureInputs matches them against the fixture ops and saves.
type FixtureRefs map[string]bool

// ScanFixtureRefs collects the string literals of every non-test Go file
// in dirs (absolute package directories).
func ScanFixtureRefs(dirs []string) (FixtureRefs, error) {
	refs := FixtureRefs{}
	for _, dir := range dirs {
		entries, err := os.ReadDir(dir)
		if err != nil {
			return nil, err
		}
		for _, entry := range entries {
			name := entry.Name()
			if entry.IsDir() || !strings.HasSuffix(name, ".go") || strings.HasSuffix(name, "_test.go") {
				continue
			}
			src, err := os.ReadFile(filepath.Join(dir, name))
			if err != nil {
				return nil, err
			}
			for _, m := range goStringLiteral.FindAllSubmatch(src, -1) {
				refs[string(m[1])] = true
			}
		}
	}
	return refs, nil
}

// FixtureInputs lists, repo-relative with forward slashes and sorted, the
// files under FixtureRoot that Go sources with the given refs depend on:
// every build file, each fixture source registering a tool the refs name
// (and, transitively, the fixture sources it mentions by class name), and
// each committed save whose name (up to its first dot) the refs name.
func FixtureInputs(repo string, refs FixtureRefs) ([]string, error) {
	root := filepath.Join(repo, filepath.FromSlash(FixtureRoot))
	entries, err := os.ReadDir(root)
	if err != nil {
		return nil, fmt.Errorf("harness input %s: %w", FixtureRoot, err)
	}
	var files []string
	sources := map[string][]byte{} // class name (file base) -> source
	for _, entry := range entries {
		name := entry.Name()
		switch {
		case entry.IsDir():
			continue
		case strings.HasSuffix(name, ".cs"):
			src, err := os.ReadFile(filepath.Join(root, name))
			if err != nil {
				return nil, err
			}
			sources[strings.TrimSuffix(name, ".cs")] = src
		default:
			// The .csproj, Taskfile and lock file: every build reads them.
			files = append(files, FixtureRoot+"/"+name)
		}
	}
	// A fixture source is needed when a ref names one of its tools; then the
	// sources it mentions by class name are needed too.
	needed := map[string]bool{}
	var queue []string
	for class, src := range sources {
		for _, m := range fixtureToolAttribute.FindAllSubmatch(src, -1) {
			if refs[string(m[1])] {
				needed[class] = true
				queue = append(queue, class)
				break
			}
		}
	}
	for len(queue) > 0 {
		class := queue[0]
		queue = queue[1:]
		src := sources[class]
		for other := range sources {
			if other == class || needed[other] {
				continue
			}
			if mentionsClass(src, other) {
				needed[other] = true
				queue = append(queue, other)
			}
		}
	}
	for class := range needed {
		files = append(files, FixtureRoot+"/"+class+".cs")
	}
	saves, err := os.ReadDir(filepath.Join(root, fixtureSavesDir))
	if err != nil && !os.IsNotExist(err) {
		return nil, err
	}
	for _, entry := range saves {
		name := entry.Name()
		if entry.IsDir() {
			continue
		}
		save, _, _ := strings.Cut(name, ".")
		if refs[save] {
			files = append(files, FixtureRoot+"/"+fixtureSavesDir+"/"+name)
		}
	}
	sort.Strings(files)
	return files, nil
}

// mentionsClass reports whether src uses class as an identifier.
func mentionsClass(src []byte, class string) bool {
	for i := 0; ; {
		j := bytes.Index(src[i:], []byte(class))
		if j < 0 {
			return false
		}
		start, end := i+j, i+j+len(class)
		before := start == 0 || !isIdentByte(src[start-1])
		after := end == len(src) || !isIdentByte(src[end])
		if before && after {
			return true
		}
		i = start + 1
	}
}

func isIdentByte(b byte) bool {
	return b == '_' || b >= '0' && b <= '9' || b >= 'a' && b <= 'z' || b >= 'A' && b <= 'Z'
}

// FixtureFile reports whether a repo-relative file lies under FixtureRoot.
func FixtureFile(file string) bool {
	return strings.HasPrefix(file, FixtureRoot+"/")
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
