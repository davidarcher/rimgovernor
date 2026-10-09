package policy

import (
	"os"
	"strings"
	"testing"
)

// TestThoughtTriggerTableCoversProvisionOwners checks the generated
// thought-to-trigger table (go run ./cmd/thoughtaudit) has the thoughts
// moodProvisionOwners maps, with the state those owners assume and the
// hand-kept owner. HighExpectations, SkyHighExpectations and AteAwfulMeal
// are not ThoughtDefs in the installed game, so the table has no rows for
// them.
func TestThoughtTriggerTableCoversProvisionOwners(t *testing.T) {
	data, err := os.ReadFile("thought_triggers.tsv")
	if err != nil {
		t.Fatal(err)
	}
	rows := map[string][]string{}
	for _, line := range strings.Split(string(data), "\n")[1:] {
		if f := strings.Split(line, "\t"); len(f) == 5 {
			rows[f[0]] = f
		}
	}
	notDefs := map[string]bool{"HighExpectations": true, "SkyHighExpectations": true, "AteAwfulMeal": true}
	deps := map[string]string{
		"AteWithoutTable": "room_stat:Impressiveness",
		"NeedJoy":         "need:joy",
		"SleptInBarracks": "room_role:Barracks",
		"SleptOnGround":   "room_role:Bedroom",
		"SleptOutside":    "room_role:Bedroom",
		"EnvironmentDark": "light",
		"EnvironmentCold": "temperature",
		"EnvironmentHot":  "temperature",
		"NeedBeauty":      "need:beauty",
		"NeedRoomSize":    "room",
		"ApparelDamaged":  "apparel",
	}
	all := map[string]bool{"SleptInBarracks": true, "ApparelDamaged": true}
	for def := range moodProvisionOwners {
		all[def] = true
	}
	for def := range all {
		row, ok := rows[def]
		if notDefs[def] {
			if ok {
				t.Errorf("%s became a ThoughtDef; give it a dependency check", def)
			}
			continue
		}
		if !ok {
			t.Errorf("table has no row for %s", def)
			continue
		}
		if want := deps[def]; want != "" && !strings.Contains(row[3], want) {
			t.Errorf("%s dependency %q lacks %q", def, row[3], want)
		}
		for _, owner := range moodProvisionOwners[def] {
			if !strings.Contains(row[4], string(owner)) {
				t.Errorf("%s owner %q lacks %s", def, row[4], owner)
			}
		}
	}
}
