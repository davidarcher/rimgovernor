package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/xml"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// Row is one concrete StatWorker or StatPart class. Owner is hand-kept and
// never generated: "unowned" until a Go function owns the class.
type Row struct {
	Class    string
	Kind     string // "worker" or "part"
	Category string // data, state, difficulty_gear or other
	Inputs   string // read tokens, ';'-joined and sorted
	Defs     string // StatDefs that use the class, ';'-joined and sorted
	Hash     string // digest of the decompiled class and its base chain
	Owner    string
}

// Unowned is the owner of a class no Go function evaluates yet.
const Unowned = "unowned"

// Categories.
const (
	CategoryData           = "data"            // reads only its own fields, which the mirror carries
	CategoryState          = "state"           // reads pawn, room, map or thing state
	CategoryDifficultyGear = "difficulty_gear" // reads difficulty, storyteller or gear
	CategoryOther          = "other"           // source unavailable
)

type statDefXML struct {
	Name     string `xml:"Name,attr"`
	Parent   string `xml:"ParentName,attr"`
	Abstract string `xml:"Abstract,attr"`
	DefName  string `xml:"defName"`
	Worker   string `xml:"workerClass"`
	Parts    struct {
		Li []struct {
			Class string `xml:"Class,attr"`
		} `xml:"li"`
	} `xml:"parts"`
}

type statDefFile struct {
	Defs []statDefXML `xml:"StatDef"`
}

// statUse maps a worker or part class to the StatDefs that use it.
type statUse map[string]map[string]bool

func (u statUse) add(class, def string) {
	class = strings.TrimSpace(class)
	if i := strings.LastIndex(class, "."); i >= 0 {
		class = class[i+1:]
	}
	if class == "" {
		return
	}
	if u[class] == nil {
		u[class] = map[string]bool{}
	}
	u[class][def] = true
}

// loadStatUses reads every non-abstract StatDef under dataDir
// (<dataDir>/<pack>/Defs/**.xml) and records the worker and parts of each,
// including those inherited through ParentName.
func loadStatUses(dataDir string) (statUse, error) {
	var all []statDefXML
	packs, err := os.ReadDir(dataDir)
	if err != nil {
		return nil, err
	}
	for _, pack := range packs {
		if !pack.IsDir() {
			continue
		}
		err := filepath.WalkDir(filepath.Join(dataDir, pack.Name(), "Defs"), func(path string, d fs.DirEntry, err error) error {
			if err != nil || d.IsDir() || !strings.HasSuffix(path, ".xml") {
				return nil
			}
			data, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			data = bytes.TrimPrefix(data, []byte("\xef\xbb\xbf"))
			if !bytes.Contains(data, []byte("<StatDef")) {
				return nil
			}
			var f statDefFile
			if err := xml.Unmarshal(data, &f); err != nil {
				return fmt.Errorf("%s: %w", path, err)
			}
			all = append(all, f.Defs...)
			return nil
		})
		if err != nil {
			return nil, err
		}
	}
	byName := map[string]statDefXML{}
	for _, d := range all {
		if d.Name != "" {
			byName[d.Name] = d
		}
	}
	use := statUse{}
	seen := map[string]bool{}
	for _, d := range all {
		if strings.EqualFold(d.Abstract, "true") || d.DefName == "" || seen[d.DefName] {
			continue
		}
		seen[d.DefName] = true
		worker := ""
		for cur, depth := d, 0; depth < 16; depth++ {
			if worker == "" {
				worker = strings.TrimSpace(cur.Worker)
			}
			for _, li := range cur.Parts.Li {
				use.add(li.Class, d.DefName)
			}
			parent, ok := byName[cur.Parent]
			if cur.Parent == "" || !ok {
				break
			}
			cur = parent
		}
		if worker == "" {
			worker = "StatWorker"
		}
		use.add(worker, d.DefName)
	}
	return use, nil
}

var (
	classDeclRE = regexp.MustCompile(`(?m)^public\s+((?:abstract\s+|sealed\s+)*)class\s+((?:StatWorker|StatPart)\w*)\s*(?::\s*(\w+))?`)
	spaceRE     = regexp.MustCompile(`[ \t\r]+`)
)

type classSrc struct {
	Src, Base string
	Abstract  bool
}

// scanClasses returns the source of every StatWorker* and StatPart* class
// in the decompiled tree (one top-level type per file), by class name.
func scanClasses(root string) (map[string]classSrc, error) {
	out := map[string]classSrc{}
	err := filepath.WalkDir(root, func(path string, d fs.DirEntry, err error) error {
		if err != nil || d.IsDir() || !strings.HasSuffix(path, ".cs") {
			return err
		}
		base := strings.TrimSuffix(d.Name(), ".cs")
		if !strings.HasPrefix(base, "StatWorker") && !strings.HasPrefix(base, "StatPart") {
			return nil
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		src := string(data)
		for _, m := range classDeclRE.FindAllStringSubmatch(src, -1) {
			if m[2] == base {
				out[base] = classSrc{Src: src, Base: m[3], Abstract: strings.Contains(m[1], "abstract")}
				break
			}
		}
		return nil
	})
	return out, err
}

// chain concatenates a class's source with its base classes up to, but not
// including, the root StatWorker or StatPart.
func chain(classes map[string]classSrc, class string) string {
	var b strings.Builder
	for depth := 0; depth < 8 && class != "" && class != "StatWorker" && class != "StatPart"; depth++ {
		c, ok := classes[class]
		if !ok {
			break
		}
		b.WriteString(c.Src)
		b.WriteString("\n")
		class = c.Base
	}
	return b.String()
}

// Hash is a stable digest of decompiled source, insensitive to indentation
// and line endings.
func Hash(src string) string {
	var lines []string
	for _, l := range strings.Split(src, "\n") {
		l = strings.TrimSpace(spaceRE.ReplaceAllString(l, " "))
		if l != "" {
			lines = append(lines, l)
		}
	}
	sum := sha256.Sum256([]byte(strings.Join(lines, "\n")))
	return hex.EncodeToString(sum[:])[:16]
}

type inputRule struct {
	token string
	gear  bool // difficulty or gear rather than world state
	re    *regexp.Regexp
}

func rule(token string, gear bool, pattern string) inputRule {
	return inputRule{token: token, gear: gear, re: regexp.MustCompile(pattern)}
}

var inputRules = []inputRule{
	rule("room", false, `GetRoom\(|\.Room\b|RoomStat|RoomRole|OutdoorsForWork|ContainedBeds`),
	rule("map", false, `\.Map\b|\bmap\.|MapParent|weatherManager|roofGrid|GlowGrid|Find\.CurrentMap`),
	rule("temperature", false, `Temperature`),
	rule("time", false, `GenLocalDate|GenDate|TicksGame|TicksAbs|DayPercent|HourFloat|Season`),
	rule("pawn_health", false, `\bhealth\.|hediffSet|capacities|Hediff`),
	rule("pawn_skills", false, `\bskills\.|SkillDef|GetSkill`),
	rule("pawn_needs", false, `\bneeds\.|NeedDef`),
	rule("pawn_story", false, `\bstory\.|[Tt]raits?\b|ageTracker|BiologicalAge|ChronologicalAge|\bgenes\b|\bGene\b|xenotype`),
	rule("pawn_other", false, `\bPawn\b|\bpawn\.|psychicEntropy|\bmechanitor|\bguest\b|Ideo|Precept|[Ff]action|relations|ownership|CurJob|\bDowned\b|\bDead\b|Inhumanized`),
	rule("thing", false, `req\.Thing|\bThing\b|stackCount|HitPoints|\bQuality|CompQuality|TryGetComp|\bStuff\b|Spawned|Corpse`),
	rule("apparel", true, `pparel`),
	rule("equipment", true, `[Ee]quipment|\bgear\b|Gear|Verb|[Ww]eapon`),
	rule("difficulty", true, `[Dd]ifficulty|[Ss]toryteller|Find\.Scenario|Find\.World|WorldComp`),
}

// Inputs lists the state tokens a decompiled body reads, sorted; empty when
// it reads none.
func Inputs(src string) []string {
	set := map[string]bool{}
	for _, r := range inputRules {
		if r.re.MatchString(src) {
			set[r.token] = true
		}
	}
	return sortedKeys(set)
}

// Classify returns the category of a decompiled body: difficulty_gear when
// it reads difficulty, storyteller or gear; state when it reads pawn, room,
// map or thing state; data when it reads none of those (only its own
// fields); other when there is no source.
func Classify(src string) string {
	if strings.TrimSpace(src) == "" {
		return CategoryOther
	}
	cat := CategoryData
	for _, r := range inputRules {
		if !r.re.MatchString(src) {
			continue
		}
		if r.gear {
			return CategoryDifficultyGear
		}
		cat = CategoryState
	}
	return cat
}

func sortedKeys(set map[string]bool) []string {
	out := make([]string, 0, len(set))
	for k := range set {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// BuildRows lists every concrete (non-abstract) StatWorker subclass and
// StatPart subclass. The root StatWorker, the default evaluator of defs
// with no workerClass, is not a row.
func BuildRows(classes map[string]classSrc, use statUse) []Row {
	var rows []Row
	for name, c := range classes {
		if c.Abstract || name == "StatWorker" || name == "StatPart" {
			continue
		}
		kind := "part"
		if strings.HasPrefix(name, "StatWorker") {
			kind = "worker"
		}
		src := chain(classes, name)
		inputs := Inputs(src)
		if len(inputs) == 0 {
			inputs = []string{"none"}
		}
		rows = append(rows, Row{
			Class:    name,
			Kind:     kind,
			Category: Classify(src),
			Inputs:   strings.Join(inputs, ";"),
			Defs:     strings.Join(sortedKeys(use[name]), ";"),
			Hash:     Hash(src),
			Owner:    Unowned,
		})
	}
	sort.Slice(rows, func(i, j int) bool {
		if rows[i].Kind != rows[j].Kind {
			return rows[i].Kind < rows[j].Kind
		}
		return rows[i].Class < rows[j].Class
	})
	return rows
}

const header = "class\tkind\tcategory\tinputs\tdefs\thash\towner"

// Format renders the table, one row per class.
func Format(rows []Row) string {
	var b strings.Builder
	b.WriteString(header + "\n")
	for _, r := range rows {
		fmt.Fprintf(&b, "%s\t%s\t%s\t%s\t%s\t%s\t%s\n", r.Class, r.Kind, r.Category, r.Inputs, r.Defs, r.Hash, r.Owner)
	}
	return b.String()
}

// Parse reads a table written by Format.
func Parse(table string) ([]Row, error) {
	var rows []Row
	for i, line := range strings.Split(table, "\n") {
		line = strings.TrimRight(line, "\r")
		if i == 0 || strings.TrimSpace(line) == "" {
			continue
		}
		f := strings.Split(line, "\t")
		if len(f) != 7 {
			return nil, fmt.Errorf("line %d: %d columns, want 7", i+1, len(f))
		}
		rows = append(rows, Row{Class: f[0], Kind: f[1], Category: f[2], Inputs: f[3], Defs: f[4], Hash: f[5], Owner: f[6]})
	}
	return rows, nil
}

// KeepOwners copies the owner of every class in old that remains in rows.
func KeepOwners(rows, old []Row) {
	owners := map[string]string{}
	for _, r := range old {
		if r.Owner != "" {
			owners[r.Class] = r.Owner
		}
	}
	for i := range rows {
		if o, ok := owners[rows[i].Class]; ok {
			rows[i].Owner = o
		}
	}
}

// Check compares the checked-in table with the rows built from the
// installed game and returns one message per class that is new, gone or
// whose decompiled hash changed.
func Check(table, current []Row) []string {
	have := map[string]Row{}
	for _, r := range table {
		have[r.Class] = r
	}
	var out []string
	seen := map[string]bool{}
	for _, r := range current {
		seen[r.Class] = true
		old, ok := have[r.Class]
		switch {
		case !ok:
			out = append(out, fmt.Sprintf("%s: new %s, not in the table", r.Class, r.Kind))
		case old.Hash != r.Hash:
			out = append(out, fmt.Sprintf("%s: decompiled body changed (table %s, game %s)", r.Class, old.Hash, r.Hash))
		}
	}
	for _, r := range table {
		if !seen[r.Class] {
			out = append(out, fmt.Sprintf("%s: in the table, gone from the game", r.Class))
		}
	}
	sort.Strings(out)
	return out
}
