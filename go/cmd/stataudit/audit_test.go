package main

import (
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

const outdoorsSample = `using Verse;

namespace RimWorld;

public class StatPart_Outdoors : StatPart
{
	private float factorIndoors = 1f;

	public override void TransformValue(StatRequest req, ref float val)
	{
		Room room = req.Thing.GetRoom();
		if (!req.Thing.Map.roofGrid.Roofed(req.Thing.Position)) { val *= 2f; }
	}
}
`

const curveSample = `namespace RimWorld;

public abstract class StatPart_Curve : StatPart
{
	protected SimpleCurve curve;
}
`

const difficultySample = `namespace RimWorld;

public class StatPart_Difficulty : StatPart_Curve
{
	public override void TransformValue(StatRequest req, ref float val)
	{
		val *= Find.Storyteller.difficulty.mineYieldFactor;
	}
}
`

const dataSample = `namespace RimWorld;

public class StatPart_Flat : StatPart
{
	private float factor = 1f;

	public override void TransformValue(StatRequest req, ref float val)
	{
		val *= factor;
	}
}
`

const workerSample = `namespace RimWorld;

public class StatWorker_Terror : StatWorker
{
	public override bool ShouldShowFor(StatRequest req)
	{
		return req.Thing is Pawn pawn && pawn.RaceProps.Humanlike;
	}
}
`

func writeSamples(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	for name, body := range map[string]string{
		"StatPart_Outdoors.cs":   outdoorsSample,
		"StatPart_Curve.cs":      curveSample,
		"StatPart_Difficulty.cs": difficultySample,
		"StatPart_Flat.cs":       dataSample,
		"StatWorker_Terror.cs":   workerSample,
		"StatPart.cs":            "public abstract class StatPart\n{\n}\n",
		"StatWorker.cs":          "public class StatWorker\n{\n}\n",
		"Unrelated.cs":           "public class Unrelated { }\n",
	} {
		if err := os.WriteFile(filepath.Join(dir, name), []byte(body), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	return dir
}

func TestClassifyAndInputs(t *testing.T) {
	for _, tt := range []struct {
		name, src, cat string
		inputs         []string
	}{
		{"state", outdoorsSample, CategoryState, []string{"map", "room", "thing"}},
		{"difficulty", difficultySample, CategoryDifficultyGear, []string{"difficulty"}},
		{"data", dataSample, CategoryData, nil},
		{"no source", "  \n", CategoryOther, nil},
	} {
		if got := Classify(tt.src); got != tt.cat {
			t.Errorf("%s: category %s, want %s", tt.name, got, tt.cat)
		}
		if got := Inputs(tt.src); !reflect.DeepEqual(got, tt.inputs) && (len(got) > 0 || len(tt.inputs) > 0) {
			t.Errorf("%s: inputs %v, want %v", tt.name, got, tt.inputs)
		}
	}
}

func TestBuildRowsListsConcreteClassesOnly(t *testing.T) {
	classes, err := scanClasses(writeSamples(t))
	if err != nil {
		t.Fatal(err)
	}
	use := statUse{}
	use.add("RimWorld.StatPart_Outdoors", "ResearchSpeedFactor")
	use.add("StatPart_Outdoors", "AssemblySpeedFactor")
	rows := BuildRows(classes, use)
	var names []string
	for _, r := range rows {
		names = append(names, r.Class)
	}
	// The abstract StatPart_Curve and the roots are not rows.
	if want := []string{"StatPart_Difficulty", "StatPart_Flat", "StatPart_Outdoors", "StatWorker_Terror"}; !reflect.DeepEqual(names, want) {
		t.Fatalf("classes = %v, want %v", names, want)
	}
	byName := map[string]Row{}
	for _, r := range rows {
		byName[r.Class] = r
	}
	if r := byName["StatPart_Outdoors"]; r.Defs != "AssemblySpeedFactor;ResearchSpeedFactor" || r.Category != CategoryState || r.Owner != Unowned || r.Kind != "part" {
		t.Errorf("outdoors row = %+v", r)
	}
	if r := byName["StatWorker_Terror"]; r.Kind != "worker" {
		t.Errorf("worker row = %+v", r)
	}
	if r := byName["StatPart_Flat"]; r.Inputs != "none" || r.Defs != "" {
		t.Errorf("flat row = %+v", r)
	}
}

func TestHashFollowsBaseChainAndIgnoresWhitespace(t *testing.T) {
	classes, err := scanClasses(writeSamples(t))
	if err != nil {
		t.Fatal(err)
	}
	before := Hash(chain(classes, "StatPart_Difficulty"))
	if again := Hash(strings.ReplaceAll(chain(classes, "StatPart_Difficulty"), "\t", "    ")); again != before {
		t.Errorf("hash depends on indentation")
	}
	c := classes["StatPart_Curve"]
	c.Src += "\n// edited\n"
	classes["StatPart_Curve"] = c
	if Hash(chain(classes, "StatPart_Difficulty")) == before {
		t.Errorf("a change in the base class did not change the hash")
	}
}

func TestCheckFailsOnEditedHashNewAndGoneClasses(t *testing.T) {
	classes, err := scanClasses(writeSamples(t))
	if err != nil {
		t.Fatal(err)
	}
	current := BuildRows(classes, statUse{})
	for i := range current {
		current[i].Owner = "stateval." + current[i].Class
	}
	table, err := Parse(Format(current))
	if err != nil {
		t.Fatal(err)
	}
	if problems := Check(table, current); len(problems) != 0 {
		t.Fatalf("matching table reported %v", problems)
	}
	// A class still unowned fails the check, even with a matching hash.
	table[0].Owner = Unowned
	if problems := Check(table, current); len(problems) != 1 || !strings.Contains(problems[0], table[0].Class+": unowned") {
		t.Fatalf("unowned class reported as %v", problems)
	}
	table[0].Owner = "stateval." + table[0].Class
	edited := table[0].Class
	table[0].Hash = "0000000000000000" // hand-edited hash
	table = append(table, Row{Class: "StatPart_Removed", Hash: "x"})
	problems := Check(table, current)
	joined := strings.Join(problems, "\n")
	for _, want := range []string{edited + ": decompiled body changed", "StatPart_Removed: in the table, gone"} {
		if !strings.Contains(joined, want) {
			t.Errorf("problems %q lack %q", joined, want)
		}
	}
	// A class absent from the table is new.
	if problems := Check(table[1:], current); !strings.Contains(strings.Join(problems, "\n"), edited+": new") {
		t.Errorf("new class not reported: %v", problems)
	}
}

func TestOwnersSurviveRegeneration(t *testing.T) {
	rows := []Row{{Class: "A", Owner: Unowned}, {Class: "B", Owner: Unowned}}
	KeepOwners(rows, []Row{{Class: "A", Owner: "EvaluateOutdoors"}, {Class: "Gone", Owner: "X"}})
	if rows[0].Owner != "EvaluateOutdoors" || rows[1].Owner != Unowned {
		t.Fatalf("rows = %+v", rows)
	}
}

func TestEmbeddedTableParsesAndCoversEveryClassOnce(t *testing.T) {
	rows, err := Parse(embeddedTable)
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 97 {
		t.Fatalf("%d rows, want 97 (26 workers, 71 parts)", len(rows))
	}
	seen := map[string]bool{}
	for _, r := range rows {
		if seen[r.Class] {
			t.Errorf("%s listed twice", r.Class)
		}
		seen[r.Class] = true
		switch r.Category {
		case CategoryData, CategoryState, CategoryDifficultyGear, CategoryOther:
		default:
			t.Errorf("%s: category %q", r.Class, r.Category)
		}
		if r.Hash == "" || r.Owner == "" {
			t.Errorf("%s: missing hash or owner", r.Class)
		}
		if r.Owner == Unowned {
			t.Errorf("%s is unowned: every class has a Go owner (#2639)", r.Class)
		}
	}
}
