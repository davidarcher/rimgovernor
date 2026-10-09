package main

import (
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

func TestDependenciesClassifiesWorkerState(t *testing.T) {
	tests := []struct {
		name, src string
		want      []string
	}{
		{"light", `return p.Awake() && p.needs.mood.recentMemory.TicksSinceLastLight > 240;`, []string{"light"}},
		{"need", `if (p.needs.joy == null) { return p.needs.joy.CurCategory switch {} }`, []string{"need:joy"}},
		{"apparel", `List<Apparel> wornApparel = p.apparel.WornApparel;`, []string{"apparel"}},
		{"room", `p.GetRoom().GetStat(RoomStatDefOf.Impressiveness)`, []string{"room", "room_stat:Impressiveness"}},
		{"temperature", `GenTemperature.GetTemperatureForCell(p.Position, p.Map)`, []string{"temperature"}},
		{"hediff", `p.health.hediffSet.GetFirstHediffOfDef(def.hediff)`, []string{"hediff"}},
		{"other", `return p.story.traits.HasTrait(TraitDefOf.Ascetic);`, []string{"other"}},
	}
	for _, tt := range tests {
		if got := Dependencies(tt.src); !reflect.DeepEqual(got, tt.want) {
			t.Errorf("%s: %v, want %v", tt.name, got, tt.want)
		}
	}
}

const ingestFixture = `using Verse;

namespace RimWorld;

public static class Toils_Ingest
{
	public static Toil Other()
	{
		return null;
	}

	public static Toil FinalizeIngest(Pawn ingester)
	{
		toil.initAction = delegate
		{
			if (!cell.HasEatSurface(map))
			{
				ingester.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.AteWithoutTable);
			}
			Room room = ingester.GetRoom();
			ingester.needs.food.CurLevel;
		};
	}
}
`

func TestScanSourcesFindsGrantSiteAndWorkerBase(t *testing.T) {
	dir := t.TempDir()
	write := func(name, body string) {
		t.Helper()
		if err := os.WriteFile(filepath.Join(dir, name), []byte(body), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	write("Toils_Ingest.cs", ingestFixture)
	write("ThoughtWorker_Base.cs", "public class ThoughtWorker_Base : ThoughtWorker\n{\n\tprotected override ThoughtState CurrentStateInternal(Pawn p)\n\t{\n\t\treturn p.needs.food.CurLevel < 0.1f;\n\t}\n}\n")
	write("ThoughtWorker_Child.cs", "public class ThoughtWorker_Child : ThoughtWorker_Base\n{\n}\n")
	workers, sites, err := scanSources(dir, map[string]bool{"AteWithoutTable": true})
	if err != nil {
		t.Fatal(err)
	}
	got := sites["AteWithoutTable"]
	if len(got) != 1 || got[0].site != "Toils_Ingest.FinalizeIngest" || strings.Contains(got[0].body, "Other") {
		t.Fatalf("sites = %+v", got)
	}
	src, ok := workerSource(workers, "ThoughtWorker_Child")
	if !ok || !strings.Contains(src, "needs.food") {
		t.Fatalf("base class source not followed: %q", src)
	}
	rows := BuildRows([]thoughtDef{{Name: "AteWithoutTable"}, {Name: "Hungry", Worker: "ThoughtWorker_Child"}}, workers, sites, nil)
	if rows[0].Grant != "ingest Toils_Ingest.FinalizeIngest" || rows[0].Dependency != "need:food;room" {
		t.Errorf("memory row = %+v", rows[0])
	}
	if rows[1].Kind != "situational" || rows[1].Dependency != "need:food" {
		t.Errorf("situational row = %+v", rows[1])
	}
}

func TestTriggerCategory(t *testing.T) {
	for site, want := range map[string]string{
		"Toils_Ingest.FinalizeIngest":         "ingest",
		"Toils_LayDown.ApplyBedThoughts":      "sleep",
		"InteractionWorker_Insult.Interacted": "social",
		"RitualOutcomeEffectWorker.Apply":     "ritual",
		"Pawn_Restrict.Tick":                  "other",
	} {
		if got := TriggerCategory(site); got != want {
			t.Errorf("%s = %s, want %s", site, got, want)
		}
	}
}

func TestOwnersSurviveRegeneration(t *testing.T) {
	old := Format([]Row{{Def: "A", Kind: "memory", Grant: "g", Dependency: "d", Owner: "EnsureComfort"}, {Def: "B", Kind: "memory", Grant: "g", Dependency: "d"}})
	owners := ParseOwners(old)
	if len(owners) != 1 || owners["A"] != "EnsureComfort" {
		t.Fatalf("owners = %v", owners)
	}
}
