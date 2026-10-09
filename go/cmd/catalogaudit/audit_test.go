package main

import (
	"strings"
	"testing"
)

const fixtureXML = `<?xml version="1.0" encoding="utf-8"?>
<Defs>
  <ThingDef Name="Base" Abstract="True"><defName>NotCounted</defName></ThingDef>
  <ThingDef ParentName="Base"><defName>Steel</defName></ThingDef>
  <ThingDef><defName>Wood</defName></ThingDef>
  <jobDef><defName>Wait</defName></jobDef>
  <CreepJoinerRejectionDef><defName>Nope</defName></CreepJoinerRejectionDef>
  <ThingSetMakerDef><defName>Same</defName></ThingSetMakerDef>
  <ThingSetMakerDef><defName>Same</defName></ThingSetMakerDef>
  <TerrainDef Name="NoName"/>
  <ResearchProjectDef><defName>Lost</defName></ResearchProjectDef>
  <GhostDef><defName>Boo</defName></GhostDef>
</Defs>`

func fixtureTable(t *testing.T) Table {
	t.Helper()
	classes := map[string]*xmlClass{}
	if err := readXMLDefs([]byte(fixtureXML), classes); err != nil {
		t.Fatal(err)
	}
	rows := map[string]int{"thingdef": 1, "jobdef": 1, "creepjoinerrejectiondef": 1, "thingsetmakerdef": 1, "terraindef": 0}
	messages := map[string]string{
		"thingdef": "ThingDef", "jobdef": "JobDef", "creepjoinerrejectiondef": "CreepjoinerRejectionDef",
		"thingsetmakerdef": "ThingSetMakerDef", "terraindef": "TerrainDef", "researchprojectdef": "ResearchProjectDef",
		"extradef": "ExtraDef",
	}
	return Coverage(classes, rows, messages)
}

func TestCoverageDiff(t *testing.T) {
	table := fixtureTable(t)
	got := map[string]Line{}
	for _, l := range table.Lines {
		got[l.Class] = l
	}
	if _, ok := got["TerrainDef"]; ok {
		t.Error("a def without a defName was counted")
	}
	if l := got["ThingDef"]; l.XML != 2 || !l.Gap() {
		t.Errorf("ThingDef counted %+v: abstract excluded, one row short is a gap", l)
	}
	if l := got["jobDef"]; l.Gap() || !l.HasMessage {
		t.Errorf("jobDef must match JobDef ignoring case: %+v", l)
	}
	if l := got["CreepJoinerRejectionDef"]; l.Gap() || !l.HasMessage {
		t.Errorf("CreepJoinerRejectionDef must match ignoring case: %+v", l)
	}
	if l := got["ThingSetMakerDef"]; l.Gap() || l.Duplicates != 1 {
		t.Errorf("a duplicate defName is not a gap: %+v", l)
	}
	if l := got["ResearchProjectDef"]; !l.Missing() {
		t.Errorf("a class with XML and no rows is missing: %+v", l)
	}
	if l := got["GhostDef"]; l.HasMessage || !l.Missing() {
		t.Errorf("a class with no message is missing: %+v", l)
	}
	if !table.Failed() {
		t.Error("missing classes must fail the audit")
	}
	if len(table.NoXML) != 2 || table.NoXML[0] != "ExtraDef" || table.NoXML[1] != "TerrainDef" {
		t.Errorf("NoXML = %v", table.NoXML)
	}
	text := FormatCoverage(table)
	for _, want := range []string{"GAP ThingDef: 2 XML defs, 1 rows", "duplicate defName ThingSetMakerDef", "NO MESSAGE GhostDef", "GAP ResearchProjectDef"} {
		if !strings.Contains(text, want) {
			t.Errorf("coverage lacks %q:\n%s", want, text)
		}
	}
}

func TestSkippedByReason(t *testing.T) {
	header := `// Header
// Fields skipped (runtime state, not def data): a type
//   A.b: UnityEngine.Texture2D (runtime state UnityEngine.Texture2D)
//   A.c: X.Y (runtime state: a lazily built instance of a class family)
//   A.d: X.Z (runtime state: a lazily built instance of a class family)

message Foo {}
//   Not.counted: after (runtime state: the body)
`
	got := SkippedByReason(header)
	if got["a runtime-state type"] != 1 || got["a lazily built instance of a class family"] != 2 || len(got) != 2 {
		t.Errorf("SkippedByReason = %v", got)
	}
}

func TestParseReport(t *testing.T) {
	m := ParseReport("const\tVerse.A\tX\nconst\tVerse.A\tY\ncurve\tVerse.B\tC\nunsaved\tVerse.Def\tindex\n")
	if m.count("const") != 2 || m.count("curve") != 1 || len(m["unsaved"]) != 1 {
		t.Errorf("ParseReport = %v", m)
	}
}
