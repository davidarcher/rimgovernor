package snapshot

import (
	"encoding/json"
	"fmt"
	"reflect"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Colony facts rows (#795 step 3). The review reads its colony facts
// through the mirror's colony sections (bridge.SplitColonyFacts), so a
// stream records the native rows. A review or step line leaves out every
// top-level field of its Projection and Facts that the decoder
// (observation.DecodeColony) reproduces from those rows exactly, and names
// the root section's version under "colony" in Mirror; replay rebuilds the
// ColonyFactsSnapshot from the rows, re-runs the decoder and puts the
// fields back. A field the decoder sets but the line holds zero is written
// as null, so replay leaves it zero. Recordings without colony sections
// (all committed testdata before this) carry their decoded fields whole.

// colonyDecoded is the decoder's projection and facts trees over the
// colony sections, cached on the root section it was built at.
type colonyDecoded struct {
	projection map[string]any
	facts      map[string]any
	err        error
	// at is the colony section versions it was built at.
	at []uint64
}

func decodeColonySections(sections map[string]*recSection) (*colonyDecoded, uint64, bool) {
	root := sections[bridge.ColonySection]
	if root == nil {
		return nil, 0, false
	}
	var at []uint64
	for _, name := range bridge.ColonySections() {
		if s := sections[name]; s != nil {
			at = append(at, s.version)
		}
	}
	if root.colony == nil || !slices.Equal(root.colony.at, at) {
		root.colony = buildColony(sections)
		root.colony.at = at
	}
	return root.colony, root.version, root.colony.err == nil
}

func buildColony(sections map[string]*recSection) *colonyDecoded {
	fail := func(err error) *colonyDecoded { return &colonyDecoded{err: err} }
	rows := map[string]map[string]bridge.ColonyRow{}
	for _, name := range bridge.ColonySections() {
		s := sections[name]
		if s == nil {
			return fail(fmt.Errorf("snapshot: no colony section %s", name))
		}
		rows[name] = make(map[string]bridge.ColonyRow, len(s.rows))
		for k, raw := range s.rows {
			var key string
			if err := json.Unmarshal(s.keys[k], &key); err != nil {
				return fail(err)
			}
			row, err := bridge.ParseColonyRow(name, key, raw)
			if err != nil {
				return fail(err)
			}
			rows[name][key] = row
		}
	}
	v, err := bridge.JoinColonyFacts(rows)
	if err != nil {
		return fail(err)
	}
	at := v.GetContext()
	identity := observation.Identity{Colony: domain.ColonyID(at.GetIdentity().GetColonyId()), Load: domain.LoadID(at.GetIdentity().GetLoadToken()), Map: domain.MapID(at.GetIdentity().GetMapId()), Tick: domain.Tick(at.GetTick())}
	if at.NativeGeneration != nil {
		identity.NativeGeneration = domain.Known(domain.NativeGeneration(at.GetNativeGeneration()))
	}
	projection, err := observation.DecodeColony(&o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: v}}, identity)
	if err != nil {
		return fail(err)
	}
	facts := projection.Facts
	var none observation.ColonyProjection
	projection.Facts, projection.Zones, projection.Window = none.Facts, none.Zones, none.Window
	out := &colonyDecoded{}
	if out.projection, err = treeObject(projection); err != nil {
		return fail(err)
	}
	if out.facts, err = treeObject(facts); err != nil {
		return fail(err)
	}
	return out
}

func treeObject(v any) (map[string]any, error) {
	data, err := Encode(v)
	if err != nil {
		return nil, err
	}
	tree, err := parseTree(data)
	if err != nil {
		return nil, err
	}
	m, _ := tree.(map[string]any)
	return m, nil
}

// colonyFields are the line fields the decoded colony facts rebuild: a
// review's Projection and Facts, a step's Projection and its Facts.
var colonyFields = []struct {
	path []string
	of   func(*colonyDecoded) map[string]any
}{
	{[]string{"Projection"}, func(d *colonyDecoded) map[string]any { return d.projection }},
	{[]string{"Facts"}, func(d *colonyDecoded) map[string]any { return d.facts }},
	{[]string{"Projection", "Facts"}, func(d *colonyDecoded) map[string]any { return d.facts }},
}

// elideColony drops from tree the fields the colony sections rebuild.
func elideColony(tree any, sections map[string]*recSection) (any, uint64, bool) {
	decoded, version, ok := decodeColonySections(sections)
	if _, isMap := tree.(map[string]any); !ok || !isMap {
		return tree, 0, false
	}
	elided := false
	for _, field := range colonyFields {
		at, _ := getPath(tree, field.path)
		have, ok := at.(map[string]any)
		if !ok {
			continue
		}
		out := make(map[string]any, len(have))
		for k, v := range have {
			out[k] = v
		}
		for k, dv := range field.of(decoded) {
			if hv, ok := have[k]; !ok {
				out[k] = nil
			} else if reflect.DeepEqual(hv, dv) {
				delete(out, k)
			}
		}
		tree = setPath(tree, field.path, out, false)
		elided = true
	}
	return tree, version, elided
}

// restoreColony puts back the fields elideColony dropped.
func restoreColony(tree any, version uint64, sections map[string]*recSection) (any, error) {
	decoded, held, ok := decodeColonySections(sections)
	if !ok || held != version {
		if decoded != nil && decoded.err != nil {
			return nil, decoded.err
		}
		return nil, fmt.Errorf("snapshot: line wants colony sections at version %d, stream holds %d", version, held)
	}
	for _, field := range colonyFields {
		at, _ := getPath(tree, field.path)
		have, ok := at.(map[string]any)
		if !ok {
			continue
		}
		out := make(map[string]any, len(have))
		for k, v := range have {
			out[k] = v
		}
		for k, dv := range field.of(decoded) {
			if hv, ok := have[k]; !ok {
				out[k] = dv
			} else if hv == nil {
				delete(out, k)
			}
		}
		tree = setPath(tree, field.path, out, false)
	}
	return tree, nil
}
