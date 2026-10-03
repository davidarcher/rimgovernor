package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// CorpseOf is the domain class of a wire CorpseClass; empty (invalid) for
// UNSPECIFIED or an unknown value.
func CorpseOf(v c.CorpseClass) domain.CorpseOf {
	switch v {
	case c.CorpseClass_CORPSE_CLASS_COLONIST:
		return domain.CorpseColonist
	case c.CorpseClass_CORPSE_CLASS_STRANGER:
		return domain.CorpseStranger
	case c.CorpseClass_CORPSE_CLASS_ANIMAL:
		return domain.CorpseAnimal
	}
	return ""
}

// RotOf is the domain stage of a wire RotStage; empty for UNSPECIFIED or an
// unknown value.
func RotOf(v c.RotStage) domain.RotStage {
	switch v {
	case c.RotStage_ROT_STAGE_FRESH:
		return domain.RotFresh
	case c.RotStage_ROT_STAGE_ROTTING:
		return domain.RotRotting
	case c.RotStage_ROT_STAGE_DESSICATED:
		return domain.RotDessicated
	}
	return ""
}

// CorpseClass is the wire class of a domain CorpseOf.
func CorpseClass(v domain.CorpseOf) c.CorpseClass {
	switch v {
	case domain.CorpseColonist:
		return c.CorpseClass_CORPSE_CLASS_COLONIST
	case domain.CorpseStranger:
		return c.CorpseClass_CORPSE_CLASS_STRANGER
	case domain.CorpseAnimal:
		return c.CorpseClass_CORPSE_CLASS_ANIMAL
	}
	return c.CorpseClass_CORPSE_CLASS_UNSPECIFIED
}
