package domain

import (
	"errors"
	"sort"
)

// ResourceSpending is the per-resource spending restriction
// ModifyResourcePolicy names: normal allows routine spending, defense_only
// permits only defensive work, and stop prohibits all spending including
// defense. Native SetProductionPolicy has one stopped-definitions list and no
// third state between them, so both restricted values dispatch as "stopped"
// exactly as the production budget does.
// The distinction is still carried here rather than collapsed at the command
// boundary so the recorded player intent stays exactly what was asked for.
type ResourceSpending string

const (
	ResourceSpendingNormal      ResourceSpending = "normal"
	ResourceSpendingDefenseOnly ResourceSpending = "defense_only"
	ResourceSpendingStop        ResourceSpending = "stop"
)

// MaxResourceReserve is the largest reserve a player may protect, matching the
// floor bound NewProductionPolicy already enforces on the dispatched value.
const MaxResourceReserve = 10000

// ResourceDirective is one resource's whole player-declared production policy:
// the Go form of one entry of the resource policy, whose
// value is always the pair (reserve, spending). It is immutable
// and comparable like PopulationDirective.
//
// A directive is persistent player configuration, not an action. The native
// write it contributes to is a single whole-state SetProductionPolicy
// replacement over every resource at once (see ProductionPolicyAction), which
// is why ResourceProductionPolicy below folds a world's whole set rather than
// this one row.
type ResourceDirective struct {
	resource string
	reserve  int64
	spending ResourceSpending
}

// NewResourceDirective bounds a resource definition name the way every other
// identifier command does and range-checks the reserve against the same
// ceiling the dispatched floor carries. A zero reserve is valid and means no
// floor at all: zero removes the reserve.
func NewResourceDirective(resource string, reserve int64, spending ResourceSpending) (ResourceDirective, error) {
	if !validID(resource) {
		return ResourceDirective{}, errors.New("invalid resource definition")
	}
	if reserve < 0 || reserve > MaxResourceReserve {
		return ResourceDirective{}, errors.New("resource reserve out of supported range")
	}
	switch spending {
	case ResourceSpendingNormal, ResourceSpendingDefenseOnly, ResourceSpendingStop:
	default:
		return ResourceDirective{}, errors.New("invalid resource spending restriction")
	}
	return ResourceDirective{resource, reserve, spending}, nil
}

// DefaultResourceDirective is the entry installed the first
// time a resource is named: reserve 0, spending normal.
func DefaultResourceDirective(resource string) (ResourceDirective, error) {
	return NewResourceDirective(resource, 0, ResourceSpendingNormal)
}

func (d ResourceDirective) Resource() string           { return d.resource }
func (d ResourceDirective) Reserve() int64             { return d.reserve }
func (d ResourceDirective) Spending() ResourceSpending { return d.spending }

// Set reports whether a directive has been recorded. The zero value is not
// constructible through NewResourceDirective, so it unambiguously means the
// player has said nothing about this resource.
func (d ResourceDirective) Set() bool { return d != ResourceDirective{} }

// Restricted reports whether this resource belongs in the dispatched stopped
// list: any spending value other than normal, exactly as production_budgets
// decides it.
func (d ResourceDirective) Restricted() bool { return d.Set() && d.spending != ResourceSpendingNormal }

// ResourcePolicyPatch is one explicit player request to change part of one
// resource's policy, the shape both commands share: ModifyResourcePolicy
// sets spending alone and SetResourceReserve sets reserve alone, and each
// preserves the other half of the entry already in force. Absence is therefore
// meaningful and carried by Optional, exactly as ExpeditionPolicyPatch carries
// its own unset limits.
type ResourcePolicyPatch struct {
	Resource string
	Spending Optional[ResourceSpending]
	Reserve  Optional[int64]
}

// Empty reports whether the request would change nothing.
func (q ResourcePolicyPatch) Empty() bool { return q == ResourcePolicyPatch{} }

// Validate bounds the named resource and range-checks only the half the
// request actually supplies. Exactly one half must be supplied: the two
// commands are separate contracts and neither can set both, so a request
// naming both is a shape this controller never produces and is refused rather
// than silently accepted as a third command.
func (q ResourcePolicyPatch) Validate() error {
	if !validID(q.Resource) {
		return errors.New("invalid resource definition")
	}
	if q.Spending.Present() == q.Reserve.Present() {
		return errors.New("a resource policy request sets exactly one of spending or reserve")
	}
	if spending, ok := q.Spending.Get(); ok {
		switch spending {
		case ResourceSpendingNormal, ResourceSpendingDefenseOnly, ResourceSpendingStop:
		default:
			return errors.New("invalid resource spending restriction")
		}
	}
	if reserve, ok := q.Reserve.Get(); ok && (reserve < 0 || reserve > MaxResourceReserve) {
		return errors.New("resource reserve out of supported range")
	}
	return nil
}

// Apply merges this request over the resource's directive in force, or over
// the {reserve 0, spending normal} default when the player has never named it.
// A base recorded for a different resource is refused: merging one resource's
// request onto another's entry would silently rewrite a policy the player
// never mentioned.
func (q ResourcePolicyPatch) Apply(base ResourceDirective) (ResourceDirective, error) {
	if err := q.Validate(); err != nil {
		return ResourceDirective{}, err
	}
	if !base.Set() {
		var err error
		if base, err = DefaultResourceDirective(q.Resource); err != nil {
			return ResourceDirective{}, err
		}
	}
	if base.Resource() != q.Resource {
		return ResourceDirective{}, errors.New("resource policy request and base disagree")
	}
	reserve, spending := base.Reserve(), base.Spending()
	if value, ok := q.Reserve.Get(); ok {
		reserve = value
	}
	if value, ok := q.Spending.Get(); ok {
		spending = value
	}
	return NewResourceDirective(q.Resource, reserve, spending)
}

// ResourceProductionPolicy folds a world's whole set of player directives into
// the single ProductionPolicy value one native SetProductionPolicy write
// carries: floors are the strictly positive reserves (zeroes are dropped)
// and stopped is every resource whose spending is not normal.
//
// A second source of floors -- outstanding construction-bundle
// ingredient costs sent as commitments --
// is deliberately not folded in here, the same disclosed narrowing
// policy.ProductionFloors already carries: the staged-bundle admission model it
// depends on has no Go equivalent. The Commitments and Drills rows another
// system owns are read fresh immediately before dispatch and resent verbatim
// (see ProductionPolicyAction), so nothing this omits can be clobbered by it.
func ResourceProductionPolicy(directives []ResourceDirective) (ProductionPolicy, error) {
	if len(directives) > 256 {
		return ProductionPolicy{}, errors.New("resource policy set exceeds bound")
	}
	seen := make(map[string]bool, len(directives))
	floors := make([]ResourceFloor, 0, len(directives))
	stopped := make([]string, 0, len(directives))
	for _, d := range directives {
		if !d.Set() {
			return ProductionPolicy{}, errors.New("unset resource directive")
		}
		if seen[d.Resource()] {
			return ProductionPolicy{}, errors.New("duplicate resource directive")
		}
		seen[d.Resource()] = true
		if d.Reserve() > 0 {
			floors = append(floors, ResourceFloor{Resource: d.Resource(), Floor: d.Reserve()})
		}
		if d.Restricted() {
			stopped = append(stopped, d.Resource())
		}
	}
	sort.Slice(floors, func(i, j int) bool { return floors[i].Resource < floors[j].Resource })
	sort.Strings(stopped)
	return NewProductionPolicy(floors, stopped)
}
