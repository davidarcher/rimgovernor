package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ApparelDefinition is one apparel definition the pawn can wear; CoversBody
// marks one covering the torso or legs.
type ApparelDefinition struct {
	Name                            string
	Armor, Child, Adult, CoversBody bool
}

// ApparelPolicyState is the pawn's outfit census: its short name (the
// outfit's label), current outfit (PolicyID, Current), the definitions it
// can wear, those its title, ideoligion role and apparel precepts require,
// and whether it goes nude.
type ApparelPolicyState struct {
	Token           string
	PawnName        string
	PolicyID        string
	Role            GearRoleInput
	Current         domain.ApparelPolicySpec
	ExcludesTainted bool
	Overrides       bool
	Definitions     []ApparelDefinition
	Required        []string
	Nude            bool
}

// Outfit bounds: the hit-point floor swaps a garment before the tattered
// thought (below 50%); the quality floor starts at Awful and rises with stock
// (apparelQualityFloor).
const (
	apparelMinHP           = .51
	qualityAwful     int32 = 0
	qualityNormal    int32 = 2
	qualityGood      int32 = 3
	qualityLegendary int32 = 6
)

// RoleApparelPolicy is the pawn's own outfit, labelled with its short
// name: the definitions its role permits (stage-compatible; combat armor for
// soldiers only; nothing covering torso or legs for a nude pawn) plus every
// definition its title, role or precepts require. All roles exclude tainted
// and tattered gear; pawns still choose by temperature inside the filter.
func RoleApparelPolicy(pawn PawnID, role GearRole, state ApparelPolicyState) (domain.ApparelPolicy, bool) {
	s := domain.ApparelPolicySpec{Pawn: domain.PawnID(pawn), Token: state.Token, Name: state.PawnName, Definitions: RoleApparelDefinitions(role, state), MinHP: apparelMinHP, MaxHP: 1, MinQuality: qualityAwful, MaxQuality: qualityLegendary}
	value, err := domain.NewApparelPolicy(s)
	if err != nil {
		return domain.ApparelPolicy{}, false
	}
	return value, true
}

// RoleApparelDefinitions is the filter RoleApparelPolicy allows.
func RoleApparelDefinitions(role GearRole, state ApparelPolicyState) []string {
	required := map[string]bool{}
	for _, d := range state.Required {
		required[d] = true
	}
	var r []string
	for _, d := range state.Definitions {
		stage := role == GearChild && d.Child || role != GearChild && d.Adult
		if required[d.Name] || stage && (!d.Armor || role == GearSoldier) && (!state.Nude || !d.CoversBody) {
			r = append(r, d.Name)
		}
	}
	return r
}

// apparelQualityFloor raises the outfit's minimum quality from Awful to
// Normal to Good as stock allows: the highest floor at which every slot the
// pawn now wears an allowed garment in still has an allowed, untainted,
// untattered garment worn or on hand (loose or stored) at that quality.
func apparelQualityFloor(model GearLoadoutInput, allowed []string) int32 {
	permits := func(o GearOption) bool {
		return slices.Contains(allowed, string(o.Definition)) && !o.Tainted && o.Condition >= apparelMinHP && o.Source != GearBillSource
	}
	slots := map[GearSlot]bool{}
	for _, w := range model.Worn {
		if permits(w) {
			slots[w.Slot] = true
		}
	}
	if len(slots) == 0 {
		return qualityAwful
	}
	held := append(append([]GearOption{}, model.Worn...), model.Options...)
	for _, floor := range []int32{qualityGood, qualityNormal} {
		covered := map[GearSlot]bool{}
		for _, o := range held {
			if permits(o) && int32(o.Quality) >= floor {
				covered[o.Slot] = true
			}
		}
		ok := true
		for slot := range slots {
			ok = ok && covered[slot]
		}
		if ok {
			return floor
		}
	}
	return qualityAwful
}

func DesiredApparelPolicy(p GearPawn) (domain.ApparelPolicy, bool) {
	state, known := p.Policy.Value()
	if !known || p.Blocked {
		return domain.ApparelPolicy{}, false
	}
	role := DeriveGearRole(state.Role)
	model, modelled := p.LoadoutModel.Value()
	if modelled {
		role = DeriveGearRole(model.Role)
	}
	desired, ok := RoleApparelPolicy(p.Pawn, role, state)
	if !ok {
		return domain.ApparelPolicy{}, false
	}
	spec := desired.Spec()
	if modelled {
		spec.MinQuality = apparelQualityFloor(model, spec.Definitions)
		var err error
		if desired, err = domain.NewApparelPolicy(spec); err != nil {
			return domain.ApparelPolicy{}, false
		}
	}
	current := state.Current
	defs := append([]string{}, current.Definitions...)
	slices.Sort(defs)
	if current.Name == spec.Name && slices.Equal(defs, spec.Definitions) && current.MinHP >= apparelMinHP-.00001 && current.MinHP <= apparelMinHP+.00001 && current.MaxHP == spec.MaxHP && current.MinQuality == spec.MinQuality && current.MaxQuality == spec.MaxQuality && state.ExcludesTainted && !state.Overrides {
		return domain.ApparelPolicy{}, false
	}
	return desired, true
}

// OutfitKeep is the outfit prune's keep set: every pawn's own outfit
// load id, known only once every pawn is observed, unblocked and already on
// its desired outfit, so the prune never deletes an outfit a pending write
// would reuse.
func OutfitKeep(pawns []GearPawn) (map[string]bool, bool) {
	keep := map[string]bool{}
	for _, p := range pawns {
		state, known := p.Policy.Value()
		if !known || p.Blocked || state.PolicyID == "" {
			return nil, false
		}
		if _, needed := DesiredApparelPolicy(p); needed {
			return nil, false
		}
		keep[state.PolicyID] = true
	}
	return keep, len(keep) > 0
}
