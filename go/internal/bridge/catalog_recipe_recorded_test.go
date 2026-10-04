package bridge

import (
	"compress/gzip"
	"encoding/json"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

var recordedBenchNumber = regexp.MustCompile(`\d+$`)

// walkRecordedRecipes visits every recorded gear recipe (an object with a
// Definition and Ingredients) with the definition of the bench it sits under.
func walkRecordedRecipes(node any, bench string, visit func(bench string, recipe map[string]any)) {
	switch v := node.(type) {
	case map[string]any:
		if id, ok := v["ID"].(string); ok && strings.HasPrefix(id, "Thing_") {
			bench = recordedBenchNumber.ReplaceAllString(strings.TrimPrefix(id, "Thing_"), "")
		}
		if _, ok := v["Definition"]; ok {
			if _, ok := v["Ingredients"]; ok {
				visit(bench, v)
			}
		}
		for _, child := range v {
			walkRecordedRecipes(child, bench, visit)
		}
	case []any:
		for _, child := range v {
			walkRecordedRecipes(child, bench, visit)
		}
	}
}

// The gear census the native tools recorded (one Core run) agrees with the
// catalog's rows: every recorded alternative of every ingredient slot is in
// the derived slot with the same count (the recording lacks the DLC stuffs the
// full catalog adds), and the recorded work type and skill are the derived ones.
func TestRecipeViewAgreesWithTheRecordedNativeCensus(t *testing.T) {
	if testing.Short() {
		t.Skip("slow: runs under cmd/test -full and nightly")
	}
	catalog := fullCatalog(t)
	files, err := filepath.Glob("../buildingruntime/testdata/*.json.gz")
	if err != nil || len(files) == 0 {
		t.Fatal(files, err)
	}
	compared := 0
	seen := map[string]bool{}
	for _, file := range files {
		f, err := os.Open(file)
		if err != nil {
			t.Fatal(err)
		}
		zr, err := gzip.NewReader(f)
		if err != nil {
			t.Fatal(err)
		}
		data, err := io.ReadAll(zr)
		f.Close()
		if err != nil {
			t.Fatal(err)
		}
		var doc any
		if err := json.Unmarshal(data, &doc); err != nil {
			t.Fatal(err)
		}
		walkRecordedRecipes(doc, "", func(bench string, recipe map[string]any) {
			name := recipe["Definition"].(string)
			if seen[bench+"/"+name] {
				return
			}
			seen[bench+"/"+name] = true
			if _, err := catalog.Recipe(name); err != nil {
				return // a recorded product list, not a recipe
			}
			compared++
			var old [][]any
			if wrapped, ok := recipe["Ingredients"].(map[string]any); ok {
				if slots, ok := wrapped["v"].([]any); ok {
					for _, slot := range slots {
						old = append(old, slot.([]any))
					}
				}
			}
			got, err := catalog.RecipeIngredients(name)
			if err != nil {
				t.Fatalf("%s: %v", name, err)
			}
			slots, known := got.Value()
			if !known || len(slots) != len(old) {
				t.Fatalf("%s: %d recorded slots, derived %v %d", name, len(old), known, len(slots))
			}
			for i, slot := range slots {
				counts := map[string]int64{}
				for _, amount := range slot {
					counts[string(amount.Resource)] = amount.Count
				}
				for _, alternative := range old[i] {
					row := alternative.(map[string]any)
					if counts[row["Resource"].(string)] != int64(row["Count"].(float64)) {
						t.Errorf("%s slot %d: recorded %v, derived %d", name, i, row, counts[row["Resource"].(string)])
					}
				}
			}
			wrapped, ok := recipe["RequiredWork"].(map[string]any)
			if !ok || bench == "" {
				return
			}
			recorded, ok := wrapped["v"].([]any)
			if !ok || len(recorded) != 1 {
				return
			}
			row := recorded[0].(map[string]any)
			work, err := catalog.RecipeWork(name, bench)
			if err != nil {
				t.Fatalf("%s at %s: %v", name, bench, err)
			}
			derived, known := work.Value()
			minimum, _ := row["Minimum"].(float64) // a zero minimum is not recorded
			if !known || len(derived) != 1 || string(derived[0].Work) != row["Work"] || derived[0].Skill != row["Skill"] || float64(derived[0].Minimum) != minimum {
				t.Errorf("%s at %s: recorded work %v, derived %v", name, bench, row, derived)
			}
		})
	}
	if compared < 20 {
		t.Fatalf("only %d recorded recipes compared", compared)
	}
}
