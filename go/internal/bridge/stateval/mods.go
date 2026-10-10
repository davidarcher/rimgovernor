package stateval

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"google.golang.org/protobuf/proto"
)

// ModsOf are the package ids (lower case) of the mods that contributed a def
// row to catalog: Env.ActiveMods for a load whose mod list the caller does not
// hold. A mod that adds no def is not seen, which only matters to a stat that
// names such a mod in showIfModsLoaded.
func ModsOf(catalog *bridge.DefinitionCatalog) map[string]bool {
	mods := map[string]bool{}
	add := func(row proto.Message) {
		msg := row.ProtoReflect()
		field := msg.Descriptor().Fields().ByName("modPackageId")
		if field != nil && msg.Has(field) {
			mods[strings.ToLower(msg.Get(field).String())] = true
		}
	}
	for _, row := range catalog.ThingDefs {
		add(row)
	}
	for _, row := range catalog.TerrainDefs {
		add(row)
	}
	for _, rows := range catalog.Defs {
		for _, row := range rows {
			add(row)
		}
	}
	return mods
}
