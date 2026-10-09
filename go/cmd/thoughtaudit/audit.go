package main

import (
	"bytes"
	"encoding/xml"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"unicode"
)

// Row is one vanilla ThoughtDef: how the game grants it and the game state
// the grant depends on. Owner is hand-kept and never generated.
type Row struct {
	Def        string
	Kind       string // "situational" (has a workerClass) or "memory"
	Grant      string // worker class, or the memory trigger categories and sites
	Dependency string // state tokens, ';'-joined and sorted
	Owner      string
}

// thoughtXML is the part of a ThoughtDef the audit reads.
type thoughtXML struct {
	Name     string `xml:"Name,attr"`
	Parent   string `xml:"ParentName,attr"`
	Abstract string `xml:"Abstract,attr"`
	DefName  string `xml:"defName"`
	Worker   string `xml:"workerClass"`
	Hediff   string `xml:"hediff"`
}

type thoughtFile struct {
	Thoughts []thoughtXML `xml:"ThoughtDef"`
}

type thoughtDef struct {
	Name, Worker, Hediff string
}

var (
	xmlTokenRE = regexp.MustCompile(`>\s*([A-Za-z0-9_]+)\s*<`)
	defNameRE  = regexp.MustCompile(`<defName>[^<]*</defName>`)

	defOfRE    = regexp.MustCompile(`\bThoughtDefOf\.(\w+)`)
	namedRE    = regexp.MustCompile(`\b(?:GetNamed|GetNamedSilentFail|Named)\("(\w+)"`)
	classRE    = regexp.MustCompile(`\bclass\s+(\w+)\s*(?::\s*(\w+))?`)
	methodRE   = regexp.MustCompile(`^\t[A-Za-z_].*\(`)
	statRoomRE = regexp.MustCompile(`\bRoomStatDefOf\.(\w+)`)
	roomRoleRE = regexp.MustCompile(`\bRoomRoleDefOf\.(\w+)`)
	needFldRE  = regexp.MustCompile(`\bneeds\.(\w+)`)
	needDefRE  = regexp.MustCompile(`\bNeedDefOf\.(\w+)`)
	hediffRE   = regexp.MustCompile(`\bHediffDefOf\.(\w+)`)
)

// loadThoughtDefs reads every non-abstract ThoughtDef under dataDir
// (<dataDir>/<pack>/Defs/**.xml), resolving workerClass and hediff through
// ParentName. It also returns, per def name, the other def files that
// mention it as a text token (memory thoughts granted from XML).
func loadThoughtDefs(dataDir string) ([]thoughtDef, map[string][]string, error) {
	var all []thoughtXML
	tokens := map[string]map[string]bool{}
	packs, err := os.ReadDir(dataDir)
	if err != nil {
		return nil, nil, err
	}
	for _, pack := range packs {
		if !pack.IsDir() {
			continue
		}
		defs := filepath.Join(dataDir, pack.Name(), "Defs")
		err := filepath.WalkDir(defs, func(path string, d fs.DirEntry, err error) error {
			if err != nil || d.IsDir() || !strings.HasSuffix(path, ".xml") {
				return nil
			}
			data, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			data = bytes.TrimPrefix(data, []byte("\xef\xbb\xbf"))
			if bytes.Contains(data, []byte("<ThoughtDef")) {
				var f thoughtFile
				if err := xml.Unmarshal(data, &f); err != nil {
					return fmt.Errorf("%s: %w", path, err)
				}
				all = append(all, f.Thoughts...)
				return nil
			}
			rel, _ := filepath.Rel(dataDir, path)
			rel = filepath.ToSlash(rel)
			for _, m := range xmlTokenRE.FindAllSubmatch(defNameRE.ReplaceAll(data, nil), -1) {
				t := string(m[1])
				if tokens[t] == nil {
					tokens[t] = map[string]bool{}
				}
				tokens[t][rel] = true
			}
			return nil
		})
		if err != nil {
			return nil, nil, err
		}
	}
	byName := map[string]thoughtXML{}
	for _, t := range all {
		if t.Name != "" {
			byName[t.Name] = t
		}
	}
	resolve := func(t thoughtXML) thoughtDef {
		d := thoughtDef{Name: t.DefName}
		for cur, depth := t, 0; depth < 16; depth++ {
			if d.Worker == "" {
				d.Worker = strings.TrimSpace(cur.Worker)
			}
			if d.Hediff == "" {
				d.Hediff = strings.TrimSpace(cur.Hediff)
			}
			parent, ok := byName[cur.Parent]
			if cur.Parent == "" || !ok {
				break
			}
			cur = parent
		}
		return d
	}
	var out []thoughtDef
	seen := map[string]bool{}
	for _, t := range all {
		if strings.EqualFold(t.Abstract, "true") || t.DefName == "" || seen[t.DefName] {
			continue
		}
		seen[t.DefName] = true
		out = append(out, resolve(t))
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	files := map[string][]string{}
	for tok, set := range tokens {
		if seen[tok] {
			files[tok] = sortedKeys(set)
		}
	}
	return out, files, nil
}

// Dependencies lists the game state a decompiled C# body reads, as sorted
// tokens: room_stat:<Stat>, room, room_role:<Role>, need:<need>, apparel,
// temperature, hediff, hediff:<Def>, light; "other" when none match.
func Dependencies(src string) []string {
	set := map[string]bool{}
	for _, m := range statRoomRE.FindAllStringSubmatch(src, -1) {
		set["room_stat:"+m[1]] = true
	}
	for _, m := range roomRoleRE.FindAllStringSubmatch(src, -1) {
		set["room_role:"+m[1]] = true
	}
	if len(set) > 0 || strings.Contains(src, "GetRoom(") || strings.Contains(src, ".Room") {
		set["room"] = true
	}
	for _, m := range needFldRE.FindAllStringSubmatch(src, -1) {
		switch m[1] {
		case "mood", "AllNeeds", "TryGetNeed", "AddOrRemoveNeedsAsAppropriate":
		default:
			set["need:"+strings.ToLower(m[1])] = true
		}
	}
	for _, m := range needDefRE.FindAllStringSubmatch(src, -1) {
		set["need:"+strings.ToLower(m[1])] = true
	}
	if strings.Contains(src, "pparel") {
		set["apparel"] = true
	}
	if strings.Contains(src, "Temperature") {
		set["temperature"] = true
	}
	if strings.Contains(src, "hediffSet") || strings.Contains(src, "def.hediff") || hediffRE.MatchString(src) {
		set["hediff"] = true
	}
	for _, m := range hediffRE.FindAllStringSubmatch(src, -1) {
		set["hediff:"+m[1]] = true
	}
	for _, k := range []string{"TicksSinceLastLight", "GlowGrid", "GetGlowFor", "PsychGlow", "GlowLevel"} {
		if strings.Contains(src, k) {
			set["light"] = true
		}
	}
	if len(set) == 0 {
		return []string{"other"}
	}
	return sortedKeys(set)
}

func sortedKeys(set map[string]bool) []string {
	out := make([]string, 0, len(set))
	for k := range set {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// words splits CamelCase, snake_case and dotted names into lower-case words.
func words(s string) map[string]bool {
	out := map[string]bool{}
	var cur []rune
	flush := func() {
		if len(cur) > 0 {
			out[strings.ToLower(string(cur))] = true
			cur = cur[:0]
		}
	}
	rs := []rune(s)
	for i, r := range rs {
		switch {
		case !unicode.IsLetter(r) && !unicode.IsDigit(r):
			flush()
		case unicode.IsUpper(r) && len(cur) > 0 && (!unicode.IsUpper(rs[i-1]) || (i+1 < len(rs) && unicode.IsLower(rs[i+1]))):
			flush()
			cur = append(cur, r)
		default:
			cur = append(cur, r)
		}
	}
	flush()
	return out
}

var triggerWords = []struct {
	category string
	words    []string
}{
	{"ingest", []string{"ingest", "ingested", "eat", "eating", "food", "meal", "nutrition", "ingestible"}},
	{"sleep", []string{"sleep", "sleeping", "bed", "lay", "wake", "rest", "slept"}},
	{"social", []string{"social", "interaction", "interactions", "relation", "relations", "romance", "marriage", "opinion", "insult", "chitchat", "lovin", "breakup", "proposal"}},
	{"ritual", []string{"ritual", "rituals", "gathering", "party", "precept", "precepts", "ideo", "ideology", "outcome", "speech"}},
}

// TriggerCategory classifies a grant site ("Type.Method" or an XML path)
// as ingest, sleep, social, ritual or other.
func TriggerCategory(site string) string {
	w := words(site)
	for _, t := range triggerWords {
		for _, k := range t.words {
			if w[k] {
				return t.category
			}
		}
	}
	return "other"
}

func isMethodLine(line string) bool {
	return methodRE.MatchString(line) && (!strings.HasSuffix(strings.TrimSpace(line), ";") || strings.Contains(line, "=>"))
}

// enclosingMethod returns the name of the member at or before line index
// at (members sit one tab deep in the decompiler's file-scoped output) and
// its text up to the next member.
func enclosingMethod(lines []string, at int) (string, string) {
	start := -1
	for i := at; i >= 0; i-- {
		if isMethodLine(lines[i]) {
			start = i
			break
		}
	}
	if start < 0 {
		return "", ""
	}
	end := len(lines)
	for i := start + 1; i < len(lines); i++ {
		if isMethodLine(lines[i]) {
			end = i
			break
		}
	}
	sig := lines[start]
	head := strings.Fields(sig[:strings.Index(sig, "(")])
	name := ""
	if len(head) > 0 {
		name = head[len(head)-1]
	}
	return name, strings.Join(lines[start:end], "\n")
}

type memorySite struct {
	site string // Type.Method
	body string
}

type workerSrc struct {
	Src, Base string
}

// scanSources walks the decompiled tree: it returns each ThoughtWorker*
// class's source by class name and every grant site by def name.
func scanSources(root string, names map[string]bool) (map[string]workerSrc, map[string][]memorySite, error) {
	workers := map[string]workerSrc{}
	sites := map[string][]memorySite{}
	err := filepath.WalkDir(root, func(path string, d fs.DirEntry, err error) error {
		if err != nil || d.IsDir() || !strings.HasSuffix(path, ".cs") {
			return err
		}
		base := strings.TrimSuffix(d.Name(), ".cs")
		if base == "ThoughtDefOf" {
			return nil
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		src := string(data)
		if strings.HasPrefix(base, "ThoughtWorker") {
			ws := workerSrc{Src: src}
			for _, m := range classRE.FindAllStringSubmatch(src, -1) {
				if m[1] == base {
					ws.Base = m[2]
					break
				}
			}
			workers[base] = ws
		}
		if !strings.Contains(src, "ThoughtDefOf.") && !strings.Contains(src, "Named(\"") && !strings.Contains(src, "NamedSilentFail(\"") {
			return nil
		}
		lines := strings.Split(src, "\n")
		seen := map[string]bool{}
		for i, line := range lines {
			var hit []string
			for _, m := range defOfRE.FindAllStringSubmatch(line, -1) {
				hit = append(hit, m[1])
			}
			for _, m := range namedRE.FindAllStringSubmatch(line, -1) {
				hit = append(hit, m[1])
			}
			for _, name := range hit {
				if !names[name] {
					continue
				}
				method, body := enclosingMethod(lines, i)
				site := base
				if method != "" {
					site += "." + method
				}
				if key := name + "|" + site; !seen[key] {
					seen[key] = true
					sites[name] = append(sites[name], memorySite{site: site, body: body})
				}
			}
		}
		return nil
	})
	return workers, sites, err
}

// workerSource concatenates a worker class's source with its
// ThoughtWorker_* base classes (the plain ThoughtWorker is not read).
func workerSource(workers map[string]workerSrc, class string) (string, bool) {
	var b strings.Builder
	found := false
	for depth := 0; depth < 8 && class != "" && class != "ThoughtWorker"; depth++ {
		w, ok := workers[class]
		if !ok {
			break
		}
		found = true
		b.WriteString(w.Src)
		class = w.Base
	}
	return b.String(), found
}

// BuildRows classifies every def. xmlFiles lists the non-thought XML files
// that mention a def name.
func BuildRows(defs []thoughtDef, workers map[string]workerSrc, sites map[string][]memorySite, xmlFiles map[string][]string) []Row {
	rows := make([]Row, 0, len(defs))
	for _, d := range defs {
		r := Row{Def: d.Name}
		deps := map[string]bool{}
		if d.Worker != "" {
			r.Kind, r.Grant = "situational", d.Worker
			if src, ok := workerSource(workers, d.Worker); ok {
				for _, t := range Dependencies(src) {
					deps[t] = true
				}
			} else {
				deps["other"] = true
			}
			if d.Hediff != "" {
				deps["hediff"] = true
				deps["hediff:"+d.Hediff] = true
			}
		} else {
			r.Kind = "memory"
			cats := map[string]bool{}
			var grants []string
			for _, s := range sites[d.Name] {
				cats[TriggerCategory(s.site)] = true
				grants = append(grants, s.site)
				for _, t := range Dependencies(s.body) {
					deps[t] = true
				}
			}
			for _, f := range xmlFiles[d.Name] {
				cats[TriggerCategory(f)] = true
				grants = append(grants, "xml:"+f)
			}
			if len(cats) == 0 {
				cats["other"] = true
				grants = append(grants, "unreferenced")
			}
			sort.Strings(grants)
			r.Grant = strings.Join(sortedKeys(cats), "+") + " " + strings.Join(grants, ",")
		}
		if len(deps) > 1 {
			delete(deps, "other")
		}
		if len(deps) == 0 {
			deps["other"] = true
		}
		r.Dependency = strings.Join(sortedKeys(deps), ";")
		rows = append(rows, r)
	}
	return rows
}

const header = "def\tkind\tgrant\tdependency\towner"

// Format renders the table, one row per def.
func Format(rows []Row) string {
	var b strings.Builder
	b.WriteString(header + "\n")
	for _, r := range rows {
		fmt.Fprintf(&b, "%s\t%s\t%s\t%s\t%s\n", r.Def, r.Kind, r.Grant, r.Dependency, r.Owner)
	}
	return b.String()
}

// ParseOwners reads the owner column of an existing table by def name.
func ParseOwners(table string) map[string]string {
	owners := map[string]string{}
	for i, line := range strings.Split(table, "\n") {
		if i == 0 || strings.TrimSpace(line) == "" {
			continue
		}
		f := strings.Split(strings.TrimRight(line, "\r"), "\t")
		if len(f) >= 5 && f[4] != "" {
			owners[f[0]] = f[4]
		}
	}
	return owners
}
