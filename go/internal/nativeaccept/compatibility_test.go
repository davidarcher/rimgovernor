package nativeaccept

import (
	"os"
	"path/filepath"
	"sort"
	"testing"
)

func TestPageNamesRefusesMalformedDiscoveryPages(t *testing.T) {
	pages := []map[string]any{
		{},
		{"tools": []any{"home/a"}},
		{"tools": []any{map[string]any{"name": "home/a"}}},
		{"tools": []any{}, "nextCursor": 7},
	}
	for i, page := range pages {
		if _, _, err := PageNames(page); err == nil {
			t.Fatalf("page %d: expected an error for a malformed discovery page", i)
		}
	}
}

func TestPageNamesAcceptsWellFormedPage(t *testing.T) {
	names, cursor, err := PageNames(map[string]any{"tools": []any{map[string]any{"gabpName": "home/a"}}, "nextCursor": "next"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(names) != 1 || names[0] != "home/a" || cursor != "next" {
		t.Fatalf("unexpected result: names=%v cursor=%q", names, cursor)
	}
	_, cursor, err = PageNames(map[string]any{"tools": []any{}, "nextCursor": nil})
	if err != nil {
		t.Fatalf("unexpected error for a null cursor: %v", err)
	}
	if cursor != "" {
		t.Fatalf("expected an empty cursor for a null nextCursor, got %q", cursor)
	}
}

func reloadFixture() (map[string]any, map[string]any, map[string]any) {
	before := map[string]any{"colonyId": "colony", "loadToken": "old", "mapId": 0, "tick": 100}
	after := copyCompatAny(before)
	after["loadToken"] = "new"
	clock := map[string]any{"ticksGame": 100, "paused": true}
	return before, after, clock
}

func copyCompatAny(m map[string]any) map[string]any {
	out := map[string]any{}
	for k, v := range m {
		out[k] = v
	}
	return out
}

func TestVerifyReloadChecksIdentityTokenTickAndPause(t *testing.T) {
	before, after, clock := reloadFixture()
	if err := VerifyReload(before, after, clock); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	advancedAfter := copyCompatAny(after)
	advancedAfter["tick"] = 101
	advancedClock := copyCompatAny(clock)
	advancedClock["ticksGame"] = 101
	if err := VerifyReload(before, advancedAfter, advancedClock); err != nil {
		t.Fatalf("unexpected error for a one-tick load boundary: %v", err)
	}

	cases := []struct {
		name   string
		mutate func(after, clock map[string]any)
	}{
		{"colony-identity-changed", func(after, clock map[string]any) { after["colonyId"] = "other" }},
		{"token-did-not-rotate", func(after, clock map[string]any) { after["loadToken"] = before["loadToken"] }},
		{"token-missing", func(after, clock map[string]any) { after["loadToken"] = nil }},
		{"tick-advanced-too-far", func(after, clock map[string]any) { after["tick"] = 102; clock["ticksGame"] = 102 }},
		{"clock-not-paused", func(after, clock map[string]any) { clock["paused"] = false }},
		{"map-changed", func(after, clock map[string]any) { after["mapId"] = 1 }},
		{"clock-ticks-mismatch", func(after, clock map[string]any) { clock["ticksGame"] = 101 }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			_, changedAfter, changedClock := reloadFixture()
			c.mutate(changedAfter, changedClock)
			if err := VerifyReload(before, changedAfter, changedClock); err == nil {
				t.Fatalf("expected an error for %s", c.name)
			}
		})
	}
}

func TestBinderObservationDoesNotMistakeLegacyDropForValidation(t *testing.T) {
	observed := BinderObservation(map[string]any{"success": true, "unknownArguments": []any{}}, false)
	if observed["strict_unknown_key_validation_proven"] != false {
		t.Fatal("must never claim strict validation is proven")
	}
	if observed["outcome"] != "unknown-key-not-reported-legacy-binder-may-drop-it" {
		t.Fatalf("unexpected outcome: %v", observed["outcome"])
	}

	reported := BinderObservation(map[string]any{"success": true, "unknownArguments": []any{UnknownKeyProbe}}, false)
	if reported["outcome"] != "unknown-key-reported" {
		t.Fatalf("unexpected outcome: %v", reported["outcome"])
	}
	if reported["strict_unknown_key_validation_proven"] != false {
		t.Fatal("must never claim strict validation is proven")
	}

	rejected := BinderObservation(map[string]any{}, true)
	if rejected["outcome"] != "request-refused-inspect-raw-error" {
		t.Fatalf("unexpected outcome: %v", rejected["outcome"])
	}
	if rejected["strict_unknown_key_validation_proven"] != false {
		t.Fatal("must never claim strict validation is proven")
	}
}

// writeSaveXML writes a save fixture with optional duplicate or missing components.
func writeSaveXML(t *testing.T, path string, duplicateGame, duplicateMap, missing bool) {
	t.Helper()
	games := make([]string, 0, len(GameComponents))
	for name := range GameComponents {
		games = append(games, name)
	}
	sort.Strings(games)
	if duplicateGame {
		games = append(games, games[0])
	}
	if missing {
		games = games[:len(games)-1]
	}
	game := ""
	for _, name := range games {
		game += `<li Class="` + name + `" />`
	}
	mapsBlock := `<li><components><li Class="` + MapComponent + `" /></components></li>`
	if duplicateMap {
		mapsBlock = `<li><components><li Class="` + MapComponent + `" /><li Class="` + MapComponent + `" /></components></li>`
	}
	content := `<savegame><game><components>` + game + `</components><maps>` + mapsBlock + mapsBlock + `</maps></game></savegame>`
	if err := os.WriteFile(path, []byte(content), 0644); err != nil {
		t.Fatalf("write save xml: %v", err)
	}
}

func TestComponentCensusScopesMapsAndRefusesMissingOrDuplicateNativeComponents(t *testing.T) {
	path := filepath.Join(t.TempDir(), "game.rws")
	writeSaveXML(t, path, false, false, false)
	census, err := ComponentCensus(path)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(census["game"]) != 7 {
		t.Fatalf("expected 7 game components, got %d", len(census["game"]))
	}
	want := map[string]int{MapComponent: 1}
	if !DeepEqual(census["map-0"], want) || !DeepEqual(census["map-1"], want) {
		t.Fatalf("unexpected map census: map-0=%v map-1=%v", census["map-0"], census["map-1"])
	}

	for _, c := range []struct {
		name                                 string
		duplicateGame, duplicateMap, missing bool
	}{
		{"duplicate-game", true, false, false},
		{"duplicate-map", false, true, false},
		{"missing", false, false, true},
	} {
		t.Run(c.name, func(t *testing.T) {
			writeSaveXML(t, path, c.duplicateGame, c.duplicateMap, c.missing)
			if _, err := ComponentCensus(path); err == nil {
				t.Fatalf("expected an error for fault %q", c.name)
			}
		})
	}
}
