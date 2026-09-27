package nativeaccept

// UnknownKeyProbe is the sentinel argument name compatibility acceptance sends to
// prove (or fail to prove) that a legacy home/* binder actually rejects unknown
// arguments.
const UnknownKeyProbe = "n01UnknownArgumentProbe"

// GameComponents are the native save components a fresh colony's <game> scope must
// carry exactly once.
var GameComponents = map[string]bool{}

// MapComponent is the native save component a loaded map's scope must carry exactly
// once.
const MapComponent = "HomeBridge.BridgeTools.HomeCoverageState"

func init() {
	for _, name := range []string{
		"ColonyIdentity", "ConstructionLineageState", "HaulTrackingState",
		"MiningState", "RecoveryAreas", "WallRemovalState",
	} {
		GameComponents["HomeBridge.BridgeTools."+name] = true
	}
}
