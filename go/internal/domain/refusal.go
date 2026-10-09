package domain

// RefusalClass is how RimWorld answers the same request again, as native
// reported it on a refusal. A refusal that names no class is RefusalUnknown.
type RefusalClass string

const (
	// RefusalPermanent: the game refuses the same request until the world
	// changes materially, or never.
	RefusalPermanent RefusalClass = "permanent"
	// RefusalTransient: the request may succeed once the world changes.
	RefusalTransient RefusalClass = "transient"
	// RefusalUnknown: native cannot say. Never silently banned.
	RefusalUnknown RefusalClass = "unknown"
)

// Valid reports whether c is one of the three classes.
func (c RefusalClass) Valid() bool {
	return c == RefusalPermanent || c == RefusalTransient || c == RefusalUnknown
}

// NativeRefusal is the game's own account of a refused write: the failure
// code name, the reason text native sent, and the class it assigned. It is
// journaled beside ReceiptRefused.
type NativeRefusal struct {
	Code   string
	Reason string
	Class  RefusalClass
}
