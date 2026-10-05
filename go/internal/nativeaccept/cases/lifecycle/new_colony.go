// lifecycle/new_colony is the native acceptance for the production new-colony
// start (#2019, #2021): rimgovernor/lifecycle_new_colony generates a colony
// from a spec on a fresh main menu and rimgovernor/lifecycle_read_new_colony
// reports its phases. The case runs on a game it launches itself (Owned),
// because it needs a fresh main menu, and twice: the second launch proves the
// same pinned spec gives the same tile, biome, colonist names and skills.
//
// Native stops at a live map (phase FINISHING); pause, naming and the save are
// #2022, so the case ends there and asserts no save.
package lifecycle

import (
	"context"
	"fmt"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const newColonyPhasePrefix = "NEW_COLONY_PHASE_"

// newColonyPhaseOrder is the wire order a start's phases must follow.
var newColonyPhaseOrder = []string{"GENERATING_WORLD", "CHOOSING_TILE", "ROLLING_COLONISTS", "GENERATING_MAP", "FINISHING"}

func init() {
	cases.Register(cases.Case{
		Name: "lifecycle/new_colony",
		Scope: "Production new-colony start (#2021): a small spec (150 map, 0.05 coverage) reaches a live map with the requested colonist count, biome and map size, " +
			"and the poll reports phases in order; the same pinned spec on a second launch gives the same tile, biome and colonist names and skills; " +
			"refusals name a bad scenario/biome/difficulty defName (listing the valid ones), an unsatisfiable temperature band, and a start over a loaded game. " +
			"Pause, naming and the save are #2022 and not asserted.",
		Start:  cases.Owned{},
		Reason: "the start needs a fresh main menu process and the determinism check needs a second launch; the case opens and retires its own games",
		NoKeep: true,
		Budget: 30 * time.Minute,
		Run:    runNewColony,
	})
}

func newColonySpec() map[string]any {
	return map[string]any{
		"scenario": "Crashlanded", "colonistCount": 3, "seed": "newcolony-accept",
		"biomes":     []any{"TemperateForest", "BorealForest", "AridShrubland"},
		"flatTile":   true,
		"difficulty": "Rough", "storyteller": "Cassandra",
		"mapSize": 150, "planetCoverage": 0.05, "saveName": "newcolony-accept",
	}
}

func newColonyRequest(id string, spec map[string]any) map[string]any {
	return map[string]any{"requestId": id, "spec": spec, "timeoutMs": 600000}
}

func runNewColony(ctx context.Context, s cases.Session) error {
	cfg, report := s.Config(), s.Report()
	first, err := newColonyLaunch(ctx, cfg, report, "first", true)
	if err != nil {
		return err
	}
	second, err := newColonyLaunch(ctx, cfg, report, "second", false)
	if err != nil {
		return err
	}
	if first.digest != second.digest {
		return fmt.Errorf("the same pinned spec gave different colonies:\nfirst:  %s\nsecond: %s", first.digest, second.digest)
	}
	report["case_same_spec_same_colony"] = first.digest
	return nil
}

type newColonyResult struct {
	digest string
}

// newColonyLaunch opens a game at the main menu, runs the checks and retires
// the game. withRefusals adds the refusal probes (first launch only).
func newColonyLaunch(ctx context.Context, cfg *na.Config, report na.Report, name string, withRefusals bool) (*newColonyResult, error) {
	reuse, err := na.OpenReusableGame(ctx, cfg)
	if err != nil {
		return nil, fmt.Errorf("%s: %w", name, err)
	}
	defer func() {
		retireCtx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
		defer cancel()
		_ = reuse.Retire(retireCtx, name+" launch complete")
	}()
	h, err := reuse.Session(ctx)
	if err != nil {
		return nil, err
	}
	names, err := h.Discovery(ctx)
	if err != nil {
		return nil, err
	}
	for _, tool := range []string{"rimgovernor/lifecycle_new_colony", "rimgovernor/lifecycle_read_new_colony"} {
		if !na.Contains(names, tool) {
			return nil, fmt.Errorf("missing %s in discovery", tool)
		}
	}
	row := map[string]any{}
	report["new_colony_"+name] = row

	if withRefusals {
		if err := newColonyRefusals(ctx, h, row); err != nil {
			return nil, err
		}
	}

	id := fmt.Sprintf("newcolony-%s-%d", name, time.Now().UnixNano())
	started := time.Now()
	phases, err := newColonyRun(ctx, h, id, newColonySpec(), "FINISHING", name)
	if err != nil {
		return nil, err
	}
	row["phases"] = phases
	row["live_map_ms"] = time.Since(started).Milliseconds()

	digest, err := newColonyRead(ctx, h, name, row)
	if err != nil {
		return nil, err
	}

	if withRefusals {
		// A second start over the live colony is refused.
		reply, err := h.Wire(ctx, "loaded-refusal", "lifecycle_new_colony", newColonyRequest(id+"-again", newColonySpec()))
		if err != nil {
			return nil, err
		}
		if err := wantNewColonyFailure(reply, "FAILURE_CODE_UNAVAILABLE", "main menu"); err != nil {
			return nil, fmt.Errorf("start over a loaded game: %w", err)
		}
		row["loaded_refusal"] = true
	}
	return &newColonyResult{digest: digest}, nil
}

// newColonyRun starts a colony and polls until the pending phase reaches
// target, returning the phases seen in order (each once). A terminal failure
// is an error; the phases must follow the wire order without going back.
func newColonyRun(ctx context.Context, h *na.Harness, id string, spec map[string]any, target, label string) ([]string, error) {
	reply, err := h.Wire(ctx, label+"-start", "lifecycle_new_colony", newColonyRequest(id, spec))
	if err != nil {
		return nil, err
	}
	var seen []string
	last := -1
	for attempt := 0; attempt < 6000; attempt++ {
		if code, ok := na.FailureCode(reply); ok {
			failure, _ := na.AsMap(reply["failure"])
			return seen, fmt.Errorf("%s: new colony failed: %s: %v", label, code, failure["detail"])
		}
		if _, completed, err := na.Outcome(reply, "completed"); err == nil {
			return seen, fmt.Errorf("%s: unexpected completed before #2022: %v", label, completed)
		}
		_, pending, err := na.Outcome(reply, "pending")
		if err != nil {
			return seen, fmt.Errorf("%s: unexpected reply shape: %v", label, reply)
		}
		phase := strings.TrimPrefix(na.AsString(pending["phase"]), newColonyPhasePrefix)
		index := indexOf(newColonyPhaseOrder, phase)
		if index < 0 {
			return seen, fmt.Errorf("%s: unknown phase %q", label, phase)
		}
		if index < last {
			return seen, fmt.Errorf("%s: phase went backwards to %s after %s", label, phase, seen[len(seen)-1])
		}
		if index != last {
			seen = append(seen, phase)
			last = index
		}
		if phase == target {
			if seen[0] != newColonyPhaseOrder[0] {
				return seen, fmt.Errorf("%s: the start reply was not %s: %v", label, newColonyPhaseOrder[0], seen)
			}
			return seen, nil
		}
		select {
		case <-ctx.Done():
			return seen, ctx.Err()
		case <-time.After(250 * time.Millisecond):
		}
		reply, err = h.Wire(ctx, fmt.Sprintf("%s-poll-%d", label, attempt), "lifecycle_read_new_colony", map[string]any{"requestId": id})
		if err != nil {
			return seen, err
		}
	}
	return seen, fmt.Errorf("%s: new colony did not reach %s within the polling budget (phases %v)", label, target, seen)
}

func indexOf(list []string, want string) int {
	for i, v := range list {
		if v == want {
			return i
		}
	}
	return -1
}

func wantNewColonyFailure(reply map[string]any, code, contains string) error {
	got, ok := na.FailureCode(reply)
	if !ok {
		return fmt.Errorf("want failure %s, got %v", code, reply)
	}
	failure, _ := na.AsMap(reply["failure"])
	detail := na.AsString(failure["detail"])
	if got != code || !strings.Contains(detail, contains) {
		return fmt.Errorf("want %s mentioning %q, got %s: %s", code, contains, got, detail)
	}
	return nil
}

// newColonyRefusals probes the refusals that leave the menu untouched, then
// one that fails during generation (an unsatisfiable temperature band) and
// must still leave the menu usable for the real start.
func newColonyRefusals(ctx context.Context, h *na.Harness, row map[string]any) error {
	refusals := map[string]any{}
	row["refusals"] = refusals
	for _, c := range []struct{ field, value, contains string }{
		{"scenario", "NoSuchScenario", "Crashlanded"},
		{"difficulty", "NoSuchDifficulty", "Rough"},
		{"storyteller", "NoSuchStoryteller", "Cassandra"},
	} {
		spec := newColonySpec()
		spec[c.field] = c.value
		reply, err := h.Wire(ctx, "refuse-"+c.field, "lifecycle_new_colony", newColonyRequest("newcolony-bad-"+c.field, spec))
		if err != nil {
			return err
		}
		// The failure lists the valid names and names the bad one's field.
		if err := wantNewColonyFailure(reply, "FAILURE_CODE_INVALID_REQUEST", c.contains); err != nil {
			return fmt.Errorf("bad %s: %w", c.field, err)
		}
		refusals[c.field] = true
	}
	spec := newColonySpec()
	spec["biomes"] = []any{"NoSuchBiome"}
	reply, err := h.Wire(ctx, "refuse-biome", "lifecycle_new_colony", newColonyRequest("newcolony-bad-biome", spec))
	if err != nil {
		return err
	}
	if err := wantNewColonyFailure(reply, "FAILURE_CODE_INVALID_REQUEST", "TemperateForest"); err != nil {
		return fmt.Errorf("bad biome: %w", err)
	}
	refusals["biome"] = true

	// Unsatisfiable temperature: passes validation, fails once the tiles are known.
	spec = newColonySpec()
	spec["minTemperature"], spec["maxTemperature"] = 90, 100
	_, err = newColonyRun(ctx, h, "newcolony-hot", spec, "FINISHING", "unsatisfiable")
	if err == nil || !strings.Contains(err.Error(), "temperature") {
		return fmt.Errorf("unsatisfiable temperature band: want a failure naming the temperature, got %v", err)
	}
	refusals["temperature"] = err.Error()
	return nil
}

// newColonyRead asserts the live map matches the spec and returns a digest of
// what a pinned seed must reproduce: the tile, biome and each colonist's name
// and skill levels.
func newColonyRead(ctx context.Context, h *na.Harness, label string, row map[string]any) (string, error) {
	spec := newColonySpec()
	identity, err := na.ReadIdentity(ctx, h, label+"-identity")
	if err != nil {
		return "", err
	}
	scope := map[string]any{"expectedIdentity": identity}

	factsReply, err := h.Wire(ctx, label+"-facts", "observations_read_colony_facts", map[string]any{"scope": scope})
	if err != nil {
		return "", err
	}
	_, facts, err := na.Outcome(factsReply, "observed")
	if err != nil {
		return "", fmt.Errorf("colony facts: %w", err)
	}
	if got := int(na.AsNumber(facts["colonistCount"])); got != 3 {
		return "", fmt.Errorf("colony has %d colonists, want 3", got)
	}
	size, _ := na.AsMap(facts["mapSize"])
	if int(na.AsNumber(size["width"])) != 150 || int(na.AsNumber(size["height"])) != 150 {
		return "", fmt.Errorf("map size %v, want 150x150", size)
	}
	biome := na.AsString(facts["biome"])
	if indexOf([]string{"TemperateForest", "BorealForest", "AridShrubland"}, biome) < 0 {
		return "", fmt.Errorf("biome %q is none of the requested %v", biome, spec["biomes"])
	}

	worldReply, err := h.Wire(ctx, label+"-world", "observations_read_world", map[string]any{"scope": scope})
	if err != nil {
		return "", err
	}
	_, world, err := na.Outcome(worldReply, "observed")
	if err != nil {
		return "", fmt.Errorf("world: %w", err)
	}
	tile, _ := na.AsMap(world["tile"])
	tileID := int(na.AsNumber(tile["tile"]))
	hilliness := na.AsString(tile["hilliness"])

	pawnsReply, err := h.Wire(ctx, label+"-pawns", "observations_list_pawns", map[string]any{
		"scope": scope, "filter": map[string]any{"colonist": true},
		"details": map[string]any{"biography": true},
	})
	if err != nil {
		return "", err
	}
	_, observed, err := na.Outcome(pawnsReply, "observed")
	if err != nil {
		return "", fmt.Errorf("pawns: %w", err)
	}
	var colonists []string
	for _, raw := range na.AsSlice(observed["pawns"]) {
		pawn, _ := na.AsMap(raw)
		ref, _ := na.AsMap(pawn["pawn"])
		bio, _ := na.AsMap(pawn["biography"])
		var skills []string
		for _, rawSkill := range na.AsSlice(bio["skills"]) {
			skill, _ := na.AsMap(rawSkill)
			skills = append(skills, fmt.Sprintf("%s=%d/%s", na.AsString(skill["defName"]), int(na.AsNumber(skill["level"])), na.AsString(skill["passion"])))
		}
		sort.Strings(skills)
		colonists = append(colonists, fmt.Sprintf("%s[%s]", na.AsString(ref["label"]), strings.Join(skills, ",")))
	}
	sort.Strings(colonists)
	if len(colonists) != 3 {
		return "", fmt.Errorf("pawn list has %d colonists, want 3", len(colonists))
	}
	row["biome"], row["tile"], row["hilliness"], row["colonists"] = biome, tileID, hilliness, colonists
	row["threat_difficulty_scale"] = threatScale(facts)
	return fmt.Sprintf("tile=%d biome=%s hilliness=%s colonists=%s", tileID, biome, hilliness, strings.Join(colonists, ";")), nil
}

// threatScale is the facts' difficulty threat scale when the threat section
// is present; reported, not asserted (no difficulty read exists).
func threatScale(facts map[string]any) any {
	section, _ := na.AsMap(facts["threat"])
	_, threat, err := na.Outcome(section, "observed")
	if err != nil {
		return nil
	}
	return threat["difficultyThreatScale"]
}
