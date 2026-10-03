package bridge

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// odysseyPackageID is the package id of the mod that defines Odyssey's defs
// (Def.modContentPack.PackageId, a row's modPackageId), compared without case
// as the game compares package ids.
const odysseyPackageID = "ludeon.rimworld.odyssey"

// The quest tree fields that name a PlanetLayerDef (decompiled, #1717):
// QuestNode_Root_Gravcore.layer and QuestNode_Root_Asteroid.layerDef are one
// required layer; QuestScriptDef.layerWhitelist and the layerWhitelist of
// QuestNode_Root_Site, QuestNode_GetMap, QuestNode_GetNearbySettlement and
// QuestNode_RequirementsToAcceptPlanetLayer are the layers the quest may use.
// A layerBlacklist only narrows and needs no ship, so it is not read.
const (
	questLayerField     = "layer"
	questLayerDefField  = "layerDef"
	questWhitelistField = "layerWhitelist"
	questSubScriptNode  = protoreflect.Name("QuestNode_SubScript")
	questSubScriptField = "def"
)

// QuestClass classifies the quest script scriptDef for a colony that never
// builds or launches a gravship, from the catalog's QuestScriptDef rows:
//
//   - a script the Odyssey mod does not define is QuestScopeOther;
//   - an Odyssey script is QuestScopeShipOnly when its tree (following
//     QuestNode_SubScript) names one required layer (layer, layerDef) that is
//     a space layer (PlanetLayerDef.isSpace), or a layer whitelist of space
//     layers only, so the quest can occur or be finished only in orbit;
//   - every other Odyssey script is QuestScopeGround.
//
// A script with no row, or a layer reference that is no PlanetLayerDef row (an
// unresolved slate expression), is an error: unknown stays unknown.
func (catalog *DefinitionCatalog) QuestClass(scriptDef string) (policy.QuestClass, error) {
	root := DefRow[*d.QuestScriptDef](catalog, scriptDef)
	if root == nil {
		return policy.QuestClass{}, contract("catalog has no QuestScriptDef row %s", scriptDef)
	}
	if !strings.EqualFold(root.GetModPackageId(), odysseyPackageID) {
		return policy.QuestClass{Scope: policy.QuestScopeOther}, nil
	}
	walk := questWalk{catalog: catalog, seen: map[string]bool{}}
	if err := walk.script(root); err != nil {
		return policy.QuestClass{}, err
	}
	if walk.spaceLayer != "" {
		return policy.QuestClass{Scope: policy.QuestScopeShipOnly, SpaceLayer: walk.spaceLayer}, nil
	}
	return policy.QuestClass{Scope: policy.QuestScopeGround}, nil
}

// questWalk collects the space layers one quest script requires.
type questWalk struct {
	catalog    *DefinitionCatalog
	seen       map[string]bool
	spaceLayer string
}

func (w *questWalk) script(row *d.QuestScriptDef) error {
	if w.seen[row.GetDefName()] {
		return nil
	}
	w.seen[row.GetDefName()] = true
	if err := w.whitelist(row.GetDefName(), row.GetLayerWhitelist()); err != nil {
		return err
	}
	if row.GetRoot() == nil {
		return nil
	}
	return w.message(row.GetDefName(), row.GetRoot().ProtoReflect())
}

// message walks one node message and everything under it.
func (w *questWalk) message(script string, msg protoreflect.Message) error {
	var err error
	msg.Range(func(fd protoreflect.FieldDescriptor, value protoreflect.Value) bool {
		switch {
		case fd.Kind() == protoreflect.MessageKind && fd.IsList():
			list := value.List()
			for i := range list.Len() {
				if err = w.message(script, list.Get(i).Message()); err != nil {
					return false
				}
			}
		case fd.Kind() == protoreflect.MessageKind:
			err = w.message(script, value.Message())
		case fd.Kind() == protoreflect.StringKind && !fd.IsList():
			err = w.field(script, msg.Descriptor().Name(), fd.Name(), value.String())
		}
		return err == nil
	})
	return err
}

func (w *questWalk) field(script string, node, field protoreflect.Name, value string) error {
	switch {
	case field == questLayerField || field == questLayerDefField:
		return w.required(script, value)
	case field == questWhitelistField:
		return w.whitelist(script, layerTokens(value))
	case node == questSubScriptNode && field == questSubScriptField:
		sub := DefRow[*d.QuestScriptDef](w.catalog, value)
		if sub == nil {
			return contract("quest script %s runs sub-script %q, which has no QuestScriptDef row", script, value)
		}
		return w.script(sub)
	}
	return nil
}

// layerRow is the PlanetLayerDef row a layer reference names.
func (w *questWalk) layerRow(script, name string) (*d.PlanetLayerDef, error) {
	row := DefRow[*d.PlanetLayerDef](w.catalog, name)
	if row == nil {
		return nil, contract("quest script %s names planet layer %q, which has no PlanetLayerDef row", script, name)
	}
	return row, nil
}

// required notes a single required layer.
func (w *questWalk) required(script, name string) error {
	if strings.TrimSpace(name) == "" {
		return nil
	}
	row, err := w.layerRow(script, name)
	if err != nil {
		return err
	}
	if row.GetIsSpace() && w.spaceLayer == "" {
		w.spaceLayer = name
	}
	return nil
}

// whitelist notes a layer whitelist: it needs a ship when it holds space
// layers only.
func (w *questWalk) whitelist(script string, names []string) error {
	if len(names) == 0 {
		return nil
	}
	for _, name := range names {
		row, err := w.layerRow(script, name)
		if err != nil {
			return err
		}
		if !row.GetIsSpace() {
			return nil
		}
	}
	if w.spaceLayer == "" {
		w.spaceLayer = names[0]
	}
	return nil
}

// layerTokens splits a slate list value (`<li>Orbit</li><li>Surface</li>`)
// or a single def name into def names; an expression stays one token and so
// fails to resolve.
func layerTokens(value string) []string {
	value = strings.TrimSpace(value)
	if value == "" {
		return nil
	}
	if !strings.Contains(value, "<li>") {
		return []string{value}
	}
	var out []string
	for _, part := range strings.Split(value, "</li>") {
		if _, name, ok := strings.Cut(part, "<li>"); ok {
			out = append(out, strings.TrimSpace(name))
		} else if strings.TrimSpace(part) != "" {
			out = append(out, strings.TrimSpace(part))
		}
	}
	return out
}
