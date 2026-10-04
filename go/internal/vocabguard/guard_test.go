// Package vocabguard keeps the retired governor words (epic #1964) from
// returning. The test scans declared names and string literals in non-test Go,
// and lines of native C# and docs, against an explicit allowlist. Comments and
// local variable names are out of scope for Go.
package vocabguard

import (
	"bufio"
	"go/ast"
	"go/parser"
	"go/token"
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"testing"
)

// goNames are the retired words in declared Go names (types, funcs, consts,
// vars, struct fields, interface methods) and in string literals (JSON keys,
// struct tags, log keys, report keys, error text).
var goNames = []*regexp.Regexp{
	regexp.MustCompile(`(?i)goal`),        // Goal row, GoalID, GoalMethod, goal_methods
	regexp.MustCompile(`(?i)need_?state`), // NeedState: Finding / Situation
	regexp.MustCompile(`goal_methods`),    // methods
}

// goDeclOnly are retired words checked only in declared names, where a
// substring is unambiguous; as prose in a string they are ordinary English.
var goDeclOnly = []*regexp.Regexp{
	regexp.MustCompile(`Routine`),    // Rounds
	regexp.MustCompile(`Rule`),       // Safeguard (a veto)
	regexp.MustCompile(`OwnerEpoch`), // OwnerEpisode (a Standard's recurrence count)
	regexp.MustCompile(`^Epoch$`),    // Episode, outside the clock packages
}

// typeOnly are retired words checked in type names of the governor packages.
var typeOnly = regexp.MustCompile(`Response`) // Incident (a Type)

var typePaths = []string{"internal/domain/", "internal/policy/", "internal/store/", "internal/buildingruntime/"}

var (
	declRes      = append(append([]*regexp.Regexp(nil), goNames...), goDeclOnly...)
	declResClock = append(append([]*regexp.Regexp(nil), goNames...), goDeclOnly[:3]...)
)

// textWords are the retired words on lines of native C# and docs.
var textWords = []*regexp.Regexp{
	regexp.MustCompile(`(?i)goal`),
	regexp.MustCompile(`(?i)need_?state`),
	regexp.MustCompile(`\bRoutine[A-Z]\w*`),
}

// allow is one accepted use of a retired word. Every entry names where it
// applies, the text it covers and why it stays. An entry that matches nothing
// fails the test, so the list cannot rot.
type allow struct {
	path   string // slash path suffix of the file
	text   string // the matched text (exact), "" for any text in the file
	reason string
	used   bool
}

var goAllow = []*allow{
	{path: "internal/policy/rock_step.go", text: "RoofRule", reason: "RoofRule is RimWorld's roof definition (RoofDef), not the Safeguard veto."},
	{path: "internal/policy/rock_step.go", text: "RoofRules", reason: "plural of RoofRule: the RoofDef catalog."},
	{path: "internal/bridge/catalog_roof.go", text: "RoofRules", reason: "the RoofDef catalog read."},
	{path: "internal/bridge/catalog_roof.go", text: "ErrRoofRules", reason: "the RoofDef catalog read."},
	{path: "internal/buildingruntime/rounds_plan_dig.go", text: "roofRulesFor", reason: "the RoofDef catalog."},
	{path: "internal/domain/pawn_settings.go", text: "HostilityResponse", reason: "RimWorld's pawn hostility-response setting (Ignore, Attack, Flee)."},
	{path: "internal/buildingruntime/break_dispatch.go", text: "BreakResponseSource", reason: "reads the mental-break response of a pawn; not the Incident concept."},
	{path: "internal/nativeaccept/metrics.go", text: "DriftRule", reason: "a metric drift threshold, not a veto."},
	{path: "internal/nativeaccept/metrics.go", text: "DriftRules", reason: "a metric drift threshold, not a veto."},
}

// epochPaths are the packages where Epoch names the clock epoch (the transport
// lease generation), not a Standard's Episode.
var epochPaths = []string{"internal/bridge/", "internal/store/clock/", "internal/store/clock_"}

// textAllow is empty: the only exempt text is the old-to-new glossary sections.
var textAllow []*allow

func repoRoot(t *testing.T) string {
	t.Helper()
	wd, err := os.Getwd() // the package directory: <root>/go/internal/vocabguard
	if err != nil {
		t.Fatal(err)
	}
	file := filepath.Join(wd, "guard_test.go")
	return filepath.Clean(filepath.Join(filepath.Dir(file), "..", "..", ".."))
}

func skipDir(rel string) bool {
	switch {
	case rel == "go/cmd/launcher":
		return true // owned by the launcher (#1983)
	}
	base := filepath.Base(rel)
	return base == "testdata" || base == "vendor" || base == "node_modules" || base == ".git" ||
		base == ".rimgovernor" || base == "dashboard" || base == "bin" || base == "obj"
}

func (a *allow) covers(rel, text string) bool {
	if !strings.HasSuffix(rel, a.path) {
		return false
	}
	if a.text != "" && a.text != text {
		return false
	}
	a.used = true
	return true
}

func allowed(list []*allow, rel, text string) bool {
	for _, a := range list {
		if a.covers(rel, text) {
			return true
		}
	}
	return false
}

func TestGoNamesAndStringsUseTheCurrentVocabulary(t *testing.T) {
	root := repoRoot(t)
	var failures []string
	fset := token.NewFileSet()
	check := func(rel string, pos token.Pos, kind, text string, res []*regexp.Regexp) {
		for _, re := range res {
			loc := re.FindString(text)
			if loc == "" {
				continue
			}
			if allowed(goAllow, rel, text) || allowed(goAllow, rel, loc) {
				continue
			}
			failures = append(failures, rel+":"+strconv.Itoa(fset.Position(pos).Line)+": "+kind+" "+strconv.Quote(text)+" uses retired word "+strconv.Quote(loc))
			return
		}
	}
	err := filepath.WalkDir(filepath.Join(root, "go"), func(path string, d fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		rel, _ := filepath.Rel(root, path)
		rel = filepath.ToSlash(rel)
		if d.IsDir() {
			if skipDir(rel) {
				return filepath.SkipDir
			}
			return nil
		}
		if !strings.HasSuffix(path, ".go") || strings.HasSuffix(path, "_test.go") {
			return nil
		}
		src, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		if strings.Contains(string(src[:min(len(src), 400)]), "Code generated") {
			return nil
		}
		file, err := parser.ParseFile(fset, path, src, 0)
		if err != nil {
			return err
		}
		inClock := false
		for _, p := range epochPaths {
			inClock = inClock || strings.Contains(rel, p)
		}
		decl := func(id *ast.Ident) {
			if id == nil || id.Name == "_" {
				return
			}
			res := declRes
			if inClock {
				res = declResClock
			}
			check(rel, id.Pos(), "name", id.Name, res)
		}
		for _, d := range file.Decls {
			switch d := d.(type) {
			case *ast.FuncDecl:
				decl(d.Name)
			case *ast.GenDecl:
				for _, s := range d.Specs {
					switch s := s.(type) {
					case *ast.TypeSpec:
						decl(s.Name)
						for _, p := range typePaths {
							if strings.Contains(rel, p) {
								check(rel, s.Name.Pos(), "type", s.Name.Name, []*regexp.Regexp{typeOnly})
							}
						}
					case *ast.ValueSpec:
						for _, n := range s.Names {
							decl(n)
						}
					}
				}
			}
		}
		ast.Inspect(file, func(n ast.Node) bool {
			switch n := n.(type) {
			case *ast.StructType:
				for _, f := range n.Fields.List {
					for _, name := range f.Names {
						decl(name)
					}
				}
			case *ast.InterfaceType:
				for _, f := range n.Methods.List {
					for _, name := range f.Names {
						decl(name)
					}
				}
			case *ast.BasicLit:
				if n.Kind == token.STRING {
					if s, err := strconv.Unquote(n.Value); err == nil {
						check(rel, n.Pos(), "string", s, goNames)
					}
				}
			}
			return true
		})
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	for _, a := range goAllow {
		if !a.used {
			failures = append(failures, "unused Go allowlist entry "+a.path+" "+a.text+": "+a.reason)
		}
	}
	report(t, failures)
}

// sectionAllow exempts the lines of one doc section that names the retired
// words on purpose: the rename map that lets a reader find the new word.
var sectionAllow = []struct{ path, heading, reason string }{
	{"docs/developers/agent-runbook.md", "## Vocabulary glossary", "the old-to-new table is the lookup for the retired words."},
	{"AGENTS.md", "## Vocabulary", "the old-to-new table is the lookup for the retired words."},
}

func TestNativeAndDocsUseTheCurrentVocabulary(t *testing.T) {
	root := repoRoot(t)
	var failures []string
	targets := []string{"docs", "integrations", "AGENTS.md", "README.md"}
	for _, target := range targets {
		err := filepath.WalkDir(filepath.Join(root, target), func(path string, d fs.DirEntry, err error) error {
			if err != nil {
				if os.IsNotExist(err) {
					return nil
				}
				return err
			}
			rel, _ := filepath.Rel(root, path)
			rel = filepath.ToSlash(rel)
			if d.IsDir() {
				if skipDir(rel) {
					return filepath.SkipDir
				}
				return nil
			}
			switch strings.ToLower(filepath.Ext(path)) {
			case ".md", ".cs", ".xml":
			default:
				return nil
			}
			failures = append(failures, scanText(path, rel)...)
			return nil
		})
		if err != nil {
			t.Fatal(err)
		}
	}
	for _, a := range textAllow {
		if !a.used {
			failures = append(failures, "unused text allowlist entry "+a.path+" "+a.text+": "+a.reason)
		}
	}
	report(t, failures)
}

func scanText(path, rel string) []string {
	f, err := os.Open(path)
	if err != nil {
		return []string{rel + ": " + err.Error()}
	}
	defer f.Close()
	var out []string
	exempt := false
	scanner := bufio.NewScanner(f)
	scanner.Buffer(make([]byte, 1<<20), 16<<20)
	for n := 1; scanner.Scan(); n++ {
		line := scanner.Text()
		if strings.HasPrefix(line, "## ") || strings.HasPrefix(line, "# ") {
			exempt = false
			for _, s := range sectionAllow {
				if strings.HasSuffix(rel, s.path) && strings.HasPrefix(line, s.heading) {
					exempt = true
				}
			}
		}
		if exempt {
			continue
		}
		for _, re := range textWords {
			for _, m := range re.FindAllString(line, -1) {
				if allowed(textAllow, rel, m) {
					continue
				}
				out = append(out, rel+":"+strconv.Itoa(n)+": retired word "+strconv.Quote(m)+": "+strings.TrimSpace(line))
			}
		}
	}
	return out
}

func report(t *testing.T, failures []string) {
	t.Helper()
	if len(failures) == 0 {
		return
	}
	sort.Strings(failures)
	shown := failures
	if len(shown) > 200 {
		shown = shown[:200]
	}
	t.Errorf("%d retired-word uses (epic #1964 vocabulary); rename them or add an allowlist entry with a reason:\n%s", len(failures), strings.Join(shown, "\n"))
}
