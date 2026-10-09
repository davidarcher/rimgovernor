package main

import (
	"bytes"
	"compress/gzip"
	"encoding/xml"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// xmlClass is one def class as the game's XML spells it.
type xmlClass struct {
	Name     string // first spelling seen
	Elements int    // concrete <Def> elements with a defName
	Names    map[string]bool
}

// loadXMLDefs reads every non-abstract def with a defName under
// <dataDir>/<pack>/Defs/**.xml, keyed by the lower-cased element name.
func loadXMLDefs(dataDir string) (map[string]*xmlClass, error) {
	classes := map[string]*xmlClass{}
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
			return readXMLDefs(data, classes)
		})
		if err != nil {
			return nil, err
		}
	}
	return classes, nil
}

// readXMLDefs adds the concrete defs of one Defs file to classes.
func readXMLDefs(data []byte, classes map[string]*xmlClass) error {
	dec := xml.NewDecoder(bytes.NewReader(data))
	dec.Strict = false
	depth := 0
	var class string
	var abstract bool
	var name string
	var inName bool
	for {
		tok, err := dec.Token()
		if err == io.EOF {
			return nil
		}
		if err != nil {
			return fmt.Errorf("xml: %w", err)
		}
		switch t := tok.(type) {
		case xml.StartElement:
			depth++
			switch {
			case depth == 2:
				class, abstract, name = t.Name.Local, false, ""
				for _, a := range t.Attr {
					if a.Name.Local == "Abstract" && strings.EqualFold(a.Value, "true") {
						abstract = true
					}
				}
			case depth == 3 && t.Name.Local == "defName":
				inName = true
			}
		case xml.CharData:
			if inName {
				name += string(t)
			}
		case xml.EndElement:
			switch {
			case depth == 3 && t.Name.Local == "defName":
				inName = false
			case depth == 2:
				if name = strings.TrimSpace(name); name != "" && !abstract {
					key := strings.ToLower(class)
					c := classes[key]
					if c == nil {
						c = &xmlClass{Name: class, Names: map[string]bool{}}
						classes[key] = c
					}
					c.Elements++
					c.Names[name] = true
				}
			}
			depth--
		}
	}
}

// recordedRows counts the rows of each def class in the recorded catalog,
// keyed by the lower-cased message name, and lists every def class that has a
// message (a field of the catalog or of DefSets), even with no rows.
func recordedRows(path string) (rows map[string]int, messages map[string]string, err error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, nil, err
	}
	defer f.Close()
	zr, err := gzip.NewReader(f)
	if err != nil {
		return nil, nil, err
	}
	data, err := io.ReadAll(zr)
	if err != nil {
		return nil, nil, err
	}
	catalog := &o.DefinitionCatalog{}
	if err := proto.Unmarshal(data, catalog); err != nil {
		return nil, nil, err
	}
	rows, messages = map[string]int{}, map[string]string{}
	count := func(m protoreflect.Message) {
		fields := m.Descriptor().Fields()
		for i := 0; i < fields.Len(); i++ {
			fd := fields.Get(i)
			if fd.IsList() && fd.Message() != nil && fd.Message().ParentFile().Package() == "rimgovernor.defs.v1" {
				name := string(fd.Message().Name())
				messages[strings.ToLower(name)] = name
				rows[strings.ToLower(name)] += m.Get(fd).List().Len()
			}
		}
	}
	count(catalog.ProtoReflect())
	count(catalog.GetDefs().ProtoReflect())
	return rows, messages, nil
}

// Line is one def class of the coverage table.
type Line struct {
	Class      string
	XML        int // distinct defNames in XML
	Duplicates int // XML elements beyond the distinct defNames
	Rows       int
	HasMessage bool
}

// Gap says whether the class has XML defs the recording lacks.
func (l Line) Gap() bool { return l.XML > l.Rows }

// Missing says whether the class has XML defs but no rows at all.
func (l Line) Missing() bool { return l.XML > 0 && l.Rows == 0 }

// Table is the coverage of every def class, sorted by name.
type Table struct {
	Lines   []Line
	Covered int // classes with a message whose rows reach the XML count
	NoXML   []string
}

// Failed is true when a class has XML defs but no rows.
func (t Table) Failed() bool {
	for _, l := range t.Lines {
		if l.Missing() {
			return true
		}
	}
	return false
}

// Coverage diffs the XML defs against the recorded rows, ignoring tag case.
// messages maps a lower-cased class to its message name.
func Coverage(xmlDefs map[string]*xmlClass, rows map[string]int, messages map[string]string) Table {
	var t Table
	for key, c := range xmlDefs {
		t.Lines = append(t.Lines, Line{
			Class:      c.Name,
			XML:        len(c.Names),
			Duplicates: c.Elements - len(c.Names),
			Rows:       rows[key],
			HasMessage: messages[key] != "",
		})
	}
	for key, name := range messages {
		if xmlDefs[key] == nil {
			t.NoXML = append(t.NoXML, name)
		}
	}
	sort.Slice(t.Lines, func(i, j int) bool { return t.Lines[i].Class < t.Lines[j].Class })
	sort.Strings(t.NoXML)
	for _, l := range t.Lines {
		if !l.Gap() && l.HasMessage {
			t.Covered++
		}
	}
	return t
}

// FormatCoverage renders the coverage section.
func FormatCoverage(t Table) string {
	var b strings.Builder
	fmt.Fprintf(&b, "== def coverage: %d XML def classes, %d covered by the recording\n", len(t.Lines), t.Covered)
	for _, l := range t.Lines {
		switch {
		case !l.HasMessage:
			fmt.Fprintf(&b, "  NO MESSAGE %s: %d XML defs\n", l.Class, l.XML)
		case l.Gap():
			fmt.Fprintf(&b, "  GAP %s: %d XML defs, %d rows\n", l.Class, l.XML, l.Rows)
		case l.Duplicates > 0:
			fmt.Fprintf(&b, "  duplicate defName %s: %d XML elements share %d defNames (%d rows)\n", l.Class, l.Duplicates+l.XML, l.XML, l.Rows)
		}
	}
	if len(t.NoXML) > 0 {
		fmt.Fprintf(&b, "  classes with a message and no XML defs: %s\n", strings.Join(t.NoXML, ", "))
	}
	return b.String()
}

var (
	skippedRE = regexp.MustCompile(`^//   (\S+): (.*)$`)
	reasonRE  = regexp.MustCompile(`\((runtime state[^)]*|[^()]*)\)$`)
)

// SkippedByReason counts the "Fields skipped" entries of the defs.proto
// header by reason. The reason is the parenthesised text, with the type
// dropped from a plain "runtime state <Type>".
func SkippedByReason(header string) map[string]int {
	counts := map[string]int{}
	in := false
	for _, line := range strings.Split(strings.ReplaceAll(header, "\r\n", "\n"), "\n") {
		if !strings.HasPrefix(line, "//") {
			if in {
				break
			}
			continue
		}
		if strings.HasPrefix(line, "// Fields skipped") {
			in = true
			continue
		}
		m := skippedRE.FindStringSubmatch(line)
		if !in || m == nil {
			continue
		}
		reason := "unspecified"
		if r := reasonRE.FindStringSubmatch(m[2]); r != nil {
			reason = r[1]
			if rest, ok := strings.CutPrefix(reason, "runtime state: "); ok {
				reason = rest
			} else if strings.HasPrefix(reason, "runtime state ") {
				reason = "a runtime-state type"
			}
		}
		counts[reason]++
	}
	return counts
}

// FormatSkipped renders the skipped-fields section.
func FormatSkipped(counts map[string]int) string {
	total := 0
	for _, n := range counts {
		total += n
	}
	var b strings.Builder
	fmt.Fprintf(&b, "== skipped runtime-state fields: %d\n", total)
	for _, r := range sortedKeys(counts) {
		fmt.Fprintf(&b, "  %4d  %s\n", counts[r], r)
	}
	return b.String()
}

// Members is the parsed defmirror --report: game members by kind and class.
type Members map[string]map[string][]string

// ParseReport reads the report's "kind<TAB>class<TAB>member" lines.
func ParseReport(text string) Members {
	m := Members{}
	for _, line := range strings.Split(text, "\n") {
		f := strings.Split(strings.TrimRight(line, "\r"), "\t")
		if len(f) != 3 {
			continue
		}
		if m[f[0]] == nil {
			m[f[0]] = map[string][]string{}
		}
		m[f[0]][f[1]] = append(m[f[0]][f[1]], f[2])
	}
	return m
}

func (m Members) count(kind string) int {
	n := 0
	for _, members := range m[kind] {
		n += len(members)
	}
	return n
}

// catalogConstantFields counts the fields CatalogConstants carries.
func catalogConstantFields() int {
	return (&o.CatalogConstants{}).ProtoReflect().Descriptor().Fields().Len()
}

// FormatConstants renders the constants section.
func FormatConstants(m Members, carried int) string {
	var b strings.Builder
	consts, curves := m.count("const"), m.count("curve")
	fmt.Fprintf(&b, "== game constants: %d const/static readonly scalars in %d classes, %d static SimpleCurves in %d classes\n",
		consts, len(m["const"]), curves, len(m["curve"]))
	fmt.Fprintf(&b, "  the catalog carries %d CatalogConstants fields\n", carried)
	return b.String()
}

// FormatUnsaved renders the [Unsaved] data fields per Def class.
func FormatUnsaved(m Members) string {
	var b strings.Builder
	fmt.Fprintf(&b, "== [Unsaved] data fields: %d in %d Def classes\n", m.count("unsaved"), len(m["unsaved"]))
	for _, class := range sortedKeys(m["unsaved"]) {
		fmt.Fprintf(&b, "  %s: %s\n", class, strings.Join(m["unsaved"][class], ", "))
	}
	return b.String()
}

func sortedKeys[V any](m map[string]V) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}
