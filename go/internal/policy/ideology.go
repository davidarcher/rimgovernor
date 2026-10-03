package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The Ideology facts (#1654). Static defs come from the definition catalog
// (IdeologyDefs, fixed for a load); the primary ideoligion's current state
// comes from the frame's ideology section (IdeoligionFacts). Both are read
// from the game's defs: no def name is listed here. Unknown stays unknown:
// Facts.Ideology is unknown when the frame carries no ideology section
// (no Ideology, no primary ideoligion, or the section failed to read).

// PreceptEffectKind is what a precept comp does, as the native computed it.
type PreceptEffectKind string

const (
	EffectSelfTookAction    PreceptEffectKind = "self_took_action"
	EffectWitnessedAction   PreceptEffectKind = "witnessed_action"
	EffectSituational       PreceptEffectKind = "situational"
	EffectBed               PreceptEffectKind = "bed"
	EffectUnwilling         PreceptEffectKind = "unwilling"
	EffectApparel           PreceptEffectKind = "apparel"
	EffectMentalBreak       PreceptEffectKind = "mental_break"
	EffectDevelopmentPoints PreceptEffectKind = "development_points"
	EffectGoodwillSituation PreceptEffectKind = "goodwill_situation"
	// EffectOther is a comp of a class the contract does not type: only
	// CompClass is known.
	EffectOther PreceptEffectKind = "other"
)

// PreceptEffect is one PreceptComp of a precept def. A thought effect
// carries its thought and the mood each stage gives (negative penalises);
// an event effect the history event it reacts to; an unwilling effect the
// event the member refuses and the traits and hediffs that cancel the
// refusal.
type PreceptEffect struct {
	Kind              PreceptEffectKind
	CompClass         string
	HistoryEvent      string
	Thought           string
	StageMoods        []float64
	OnlyForNonSlaves  bool
	NullifyingTraits  []string
	NullifyingHediffs []string
	Chance            domain.Fact[float64]
	Gender            string
	Building          string
	MentalBreak       string
	GoodwillSituation string
}

// Penalises reports a thought effect with a stage that costs mood.
func (e PreceptEffect) Penalises() bool {
	for _, mood := range e.StageMoods {
		if mood < 0 {
			return true
		}
	}
	return false
}

// Approves reports a thought effect with a stage that gives mood.
func (e PreceptEffect) Approves() bool {
	for _, mood := range e.StageMoods {
		if mood > 0 {
			return true
		}
	}
	return false
}

// Forbids reports an effect that makes members refuse an action.
func (e PreceptEffect) Forbids() bool { return e.Kind == EffectUnwilling }

// PreceptBuilding is a ThingDef a building precept of a def may take, with
// the game's chance for it.
type PreceptBuilding struct {
	Building string
	Chance   float64
}

// MemeDef is one MemeDef.
type MemeDef struct {
	Name, Category      string
	Impact              int
	ExclusionTags       []string
	RequiredRituals     []RequiredRitual
	ConsumableBuildings []string
	RitualSeats         []string
}

// RequiredRitual is a meme's required ritual precept, its pattern and the
// building it needs; each empty when the meme names none.
type RequiredRitual struct{ Precept, Pattern, Building string }

// PreceptDef is one PreceptDef other than a role's. Name is its degree of
// Issue. Flags name the action rules it switches on (the game's own member
// names: approvesOfSlavery, disallowMiningCamps, ...).
type PreceptDef struct {
	Name, Issue, Impact, Class            string
	IssueAllowsMultiple                   bool
	RequiredMemes, ConflictingMemes       []string
	AssociatedMemes, ExclusionTags, Flags []string
	Effects                               []PreceptEffect
	Buildings                             []PreceptBuilding
	RitualPattern                         string
	MaxCount                              int
}

// HasFlag reports whether the def switches the named rule on.
func (d PreceptDef) HasFlag(flag string) bool {
	for _, f := range d.Flags {
		if f == flag {
			return true
		}
	}
	return false
}

// SkillRequirement is a role requirement's skill minimum.
type SkillRequirement struct {
	Skill    string
	MinLevel int
}

// RoleRequirement is one condition on a role's holder: its class and, for a
// skill requirement, the skills of which one must meet its minimum.
type RoleRequirement struct {
	Class  string
	Skills []SkillRequirement
}

// RoleEffect is one effect of holding a role.
type RoleEffect struct {
	Class    string
	Bad      bool
	Stat     string
	Modifier domain.Fact[float64]
}

// RoleDef is one role precept def.
type RoleDef struct {
	Name                                                   string
	Leader                                                 bool
	MaxCount, ActivationBelievers, DeactivationBelievers   int
	DisabledWorkTags, RequiredWorkTags, RequiredWorkTagAny []string
	Requirements                                           []RoleRequirement
	Effects                                                []RoleEffect
	GrantedAbilities, Tags                                 []string
}

// RitualRoleSlot is a ritual behavior's role slot.
type RitualRoleSlot struct {
	ID, Precept string
	MaxCount    int
	Required    bool
}

// RitualDef is one ritual pattern: its cadence in days, the buildings its
// obligation target filter accepts and its role slots.
type RitualDef struct {
	Name                                                 string
	IntervalDaysMin, IntervalDaysMax                     float64
	ObligationTriggers                                   []string
	ObligationTargetFilter                               string
	RequiredBuildings                                    []string
	TargetFilter, Behavior                               string
	Roles                                                []RitualRoleSlot
	CanStartAnytime, AlwaysStartAnytime, IdeoMembersOnly bool
	MinTechLevel, MaxTechLevel                           string
}

// IdeologyDefs are the static Ideology defs of one load, by def name.
type IdeologyDefs struct {
	Memes    map[string]MemeDef
	Precepts map[string]PreceptDef
	Roles    map[string]RoleDef
	Rituals  map[string]RitualDef
}

// HeldPrecept is a precept in force; role, ritual and building precepts are
// held apart.
type HeldPrecept struct{ ID, Def string }

// HeldRole is a role precept: whether enough believers make it active and
// the pawns holding it.
type HeldRole struct {
	ID, Def string
	Active  bool
	Pawns   []domain.PawnID
}

// HeldRitual is a ritual precept. LastFinishedTick is the game's
// Precept_Ritual.lastFinishedTick as read; no sentinel is interpreted here.
type HeldRitual struct {
	ID, Def, Pattern    string
	LastFinishedTick    int64
	ActiveObligations   int
	RepeatPenaltyActive bool
}

// HeldBuilding is a building precept and the ThingDef it takes.
type HeldBuilding struct{ ID, Def, Building string }

// IdeoligionFacts is the player faction's primary ideoligion as it stands.
type IdeoligionFacts struct {
	IdeoID            string
	Memes             []string
	Precepts          []HeldPrecept
	Roles             []HeldRole
	Rituals           []HeldRitual
	Buildings         []HeldBuilding
	ObligationsActive bool
	Believers         int
	MinBelievers      int
}

// Ideoligion is the ideoligion with the defs its rows resolve against.
type Ideoligion struct {
	Defs  IdeologyDefs
	Facts IdeoligionFacts
}

// EffectInForce is one effect of a precept the ideoligion holds.
type EffectInForce struct {
	Precept PreceptDef
	Effect  PreceptEffect
}

// EffectsInForce lists the effects of every precept in force, in precept id
// order. A held precept missing from the defs is skipped: the decode refuses
// such a row, so none reaches here.
func (i Ideoligion) EffectsInForce() []EffectInForce {
	held := append([]HeldPrecept(nil), i.Facts.Precepts...)
	sort.Slice(held, func(a, b int) bool { return held[a].ID < held[b].ID })
	var out []EffectInForce
	for _, precept := range held {
		def, ok := i.Defs.Precepts[precept.Def]
		if !ok {
			continue
		}
		for _, effect := range def.Effects {
			out = append(out, EffectInForce{Precept: def, Effect: effect})
		}
	}
	return out
}

// RequiredBuildings are the ThingDefs the ideoligion needs built, sorted: the
// ThingDef each building precept took and the buildings the obligation target
// filter of each held ritual's pattern accepts.
func (i Ideoligion) RequiredBuildings() []string {
	seen := map[string]bool{}
	for _, building := range i.Facts.Buildings {
		if building.Building != "" {
			seen[building.Building] = true
		}
	}
	for _, ritual := range i.Facts.Rituals {
		if pattern, ok := i.Defs.Rituals[ritual.Pattern]; ok {
			for _, building := range pattern.RequiredBuildings {
				seen[building] = true
			}
		}
	}
	out := make([]string, 0, len(seen))
	for building := range seen {
		out = append(out, building)
	}
	sort.Strings(out)
	return out
}
