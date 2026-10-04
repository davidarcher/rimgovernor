package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// The Ideology facts (#1654): static defs are rows of the definition catalog
// (catalog_ideology.go), the primary ideoligion is the frame's ideology section. A row that is not
// valid fails the whole decode: nothing is skipped or defaulted.

// validIDs reports whether every id is valid; unique also refuses a repeat.
func validIDs(ids []string, unique bool) bool {
	seen := map[string]bool{}
	for _, id := range ids {
		if validID(id) != nil || unique && seen[id] {
			return false
		}
		seen[id] = true
	}
	return true
}

func optionalID(id *string) bool { return id == nil || validID(*id) == nil }

func finite(v float64) bool { return !math.IsNaN(v) && !math.IsInf(v, 0) }

// DecodeIdeology validates the frame's ideology section under identity and
// resolves every def name in it against the catalog's Ideology rows: a name
// the catalog lacks, or a section whose catalog has no Ideology defs, is a
// contract failure.
func DecodeIdeology(v *o.IdeologySnapshot, identity *c.Identity, catalog *DefinitionCatalog) (*policy.Ideoligion, error) {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return nil, contract("invalid ideology context")
	}
	defs, err := catalog.IdeologyDefs()
	if err != nil {
		return nil, err
	}
	if defs == nil {
		return nil, contract("ideology section without catalog ideology defs")
	}
	if validID(v.GetIdeoId()) != nil || v.ObligationsActive == nil || v.Believers == nil || v.GetBelievers() < 0 || v.MinBelieversForObligations == nil || v.GetMinBelieversForObligations() < 0 {
		return nil, contract("invalid ideology section")
	}
	facts := policy.IdeoligionFacts{IdeoID: v.GetIdeoId(), Memes: v.Memes, ObligationsActive: v.GetObligationsActive(), Believers: int(v.GetBelievers()), MinBelievers: int(v.GetMinBelieversForObligations())}
	for _, meme := range v.Memes {
		if DefRow[*d.MemeDef](catalog, meme) == nil {
			return nil, contract("ideology meme %q is not in the catalog", meme)
		}
	}
	ids := map[string]bool{}
	claim := func(id string) error {
		if validID(id) != nil || ids[id] {
			return contract("invalid or duplicate ideology precept id %q", id)
		}
		ids[id] = true
		return nil
	}
	precept := func(id, def string) error {
		if err := claim(id); err != nil {
			return err
		}
		if _, ok := defs.Precepts[def]; !ok {
			return contract("ideology precept %q is not in the catalog", def)
		}
		return nil
	}
	for _, row := range v.Precepts {
		if err := precept(row.GetId(), row.GetDefName()); err != nil {
			return nil, err
		}
		facts.Precepts = append(facts.Precepts, policy.HeldPrecept{ID: row.GetId(), Def: row.GetDefName()})
	}
	for _, row := range v.Roles {
		if err := claim(row.GetId()); err != nil {
			return nil, err
		}
		if _, ok := defs.Roles[row.GetDefName()]; !ok || row.Active == nil {
			return nil, contract("ideology role %q is not in the catalog or lacks its state", row.GetDefName())
		}
		held := policy.HeldRole{ID: row.GetId(), Def: row.GetDefName(), Active: row.GetActive()}
		seen := map[string]bool{}
		for _, ref := range row.Pawns {
			if !validRef(ref) || seen[ref.GetId()] {
				return nil, contract("invalid or duplicate ideology role pawn")
			}
			seen[ref.GetId()] = true
			held.Pawns = append(held.Pawns, domain.PawnID(ref.GetId()))
		}
		facts.Roles = append(facts.Roles, held)
	}
	for _, row := range v.Rituals {
		if err := precept(row.GetId(), row.GetDefName()); err != nil {
			return nil, err
		}
		if !optionalID(row.Pattern) || row.LastFinishedTick == nil || row.ActiveObligations == nil || row.GetActiveObligations() < 0 || row.RepeatPenaltyActive == nil || row.Running == nil {
			return nil, contract("invalid ideology ritual %q", row.GetId())
		}
		if _, ok := defs.Rituals[row.GetPattern()]; row.Pattern != nil && !ok {
			return nil, contract("ideology ritual pattern %q is not in the catalog", row.GetPattern())
		}
		facts.Rituals = append(facts.Rituals, policy.HeldRitual{ID: row.GetId(), Def: row.GetDefName(), Pattern: row.GetPattern(), LastFinishedTick: int64(row.GetLastFinishedTick()),
			ActiveObligations: int(row.GetActiveObligations()), RepeatPenaltyActive: row.GetRepeatPenaltyActive(), Running: row.GetRunning()})
	}
	for _, row := range v.Buildings {
		if err := precept(row.GetId(), row.GetDefName()); err != nil {
			return nil, err
		}
		if !optionalID(row.Building) {
			return nil, contract("invalid ideology building %q", row.GetId())
		}
		facts.Buildings = append(facts.Buildings, policy.HeldBuilding{ID: row.GetId(), Def: row.GetDefName(), Building: row.GetBuilding()})
	}
	return &policy.Ideoligion{Defs: *defs, Facts: facts}, nil
}
