package domain

import "testing"

// The corpse dump keeps humanlike corpses only; animal and insect corpses
// go to the freezer while fresh and to the rotten dump, the crematorium's
// feed, once rotting (#1812).
func TestDumpFiltersSplitCorpsesByKind(t *testing.T) {
	has := func(f StockpileFilter, def string) bool {
		for _, s := range f.Allow() {
			if s == CategoryDef(def) {
				return true
			}
		}
		return false
	}
	corpse, rotten := CorpseDumpFilter(), RottenDumpFilter()
	for _, def := range []string{"CorpsesAnimal", "CorpsesInsect"} {
		if has(corpse, def) || !has(rotten, def) {
			t.Fatalf("%s: corpse dump %v, rotten dump %v", def, has(corpse, def), has(rotten, def))
		}
	}
	if !has(corpse, "CorpsesHumanlike") || has(rotten, "CorpsesHumanlike") {
		t.Fatal("humanlike corpses belong to the corpse dump only")
	}
}
