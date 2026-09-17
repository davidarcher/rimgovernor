package domain

import (
	"errors"
	"fmt"
	"sort"
)

type Cell struct{ X, Z int32 }
type Rotation string

const (
	North Rotation = "north"
	East  Rotation = "east"
	South Rotation = "south"
	West  Rotation = "west"
)

type ActionKind string

const BuildingAction ActionKind = "building"
const OwnedDraftAction ActionKind = "owned_draft"
const MeleeAttackAction ActionKind = "melee_attack"
const TendAction ActionKind = "tend"
const RescueAction ActionKind = "rescue"
const RangedAttackAction ActionKind = "ranged_attack"
const HaulAction ActionKind = "haul"
const EquipAction ActionKind = "equip"
const GearReplaceAction ActionKind = "gear_replace"
const RepairAction ActionKind = "repair"
const CaravanDepartureAction ActionKind = "caravan_departure"
const CleanAction ActionKind = "clean"
const WasteAction ActionKind = "waste"
const RecoveryServiceAction ActionKind = "recovery_service"
const MovementAction ActionKind = "movement"
const BuildingTemperatureAction ActionKind = "building_temperature"
const BedMedicalAction ActionKind = "bed_medical"
const GrowerCropAction ActionKind = "grower_crop"

// ZoneEditAction is declared in zone_edit.go alongside its ZoneEdit payload.

// ConstructionCancelAction is declared in construction_cancel.go alongside its
// ConstructionCancel payload.

// WallRemovalAction is declared in wall_removal.go alongside its payload.

// SurgeryAction is declared in surgery.go alongside its Surgery payload.

// TravelCaravanAction is declared in travel_caravan.go alongside its
// TravelCaravan payload.

// TradeAction is declared in trade.go alongside its Trade payload.

// HusbandryAction is declared in husbandry.go alongside its Husbandry payload.
// PrisonerInteractionAction is declared in prisoner_interaction.go alongside
// its PrisonerInteraction payload.

// Building is one resolved placement. Native discovery owns definition existence,
// footprint, map bounds, costs and placement legality; these are not inferred here.
type Building struct {
	definition string
	cell       Cell
	rotation   Rotation
	stuff      string
}

func NewBuilding(definition string, cell Cell, rotation Rotation, stuff string) (Building, error) {
	if !nativeText(definition, false) || !nativeText(stuff, true) {
		return Building{}, errors.New("invalid native definition or stuff text")
	}
	if cell.X < 0 || cell.Z < 0 {
		return Building{}, errors.New("building anchor must be nonnegative")
	}
	switch rotation {
	case North, East, South, West:
	default:
		return Building{}, errors.New("invalid building rotation")
	}
	return Building{definition, cell, rotation, stuff}, nil
}
func (b Building) Definition() string { return b.definition }
func (b Building) Cell() Cell         { return b.cell }
func (b Building) Rotation() Rotation { return b.rotation }

// Empty Stuff requests the native default material selection.
func (b Building) Stuff() string { return b.stuff }
func nativeText(s string, allowEmpty bool) bool {
	return (s == "" && allowEmpty) || validID(s)
}

// Action is a closed variant. New families require constructor and handler coverage.
type Action struct {
	id                  ActionID
	kind                ActionKind
	building            Building
	draft               OwnedDraft
	melee               MeleeAttack
	zone                ZoneCreate
	bill                ProductionBill
	acquisition         Acquisition
	supply              SupplyAllow
	work                WorkAssignment
	tend                Tend
	rescue              Rescue
	capture             Capture
	ranged              RangedAttack
	movement            Movement
	haul                Haul
	equip               Equip
	gearReplace         GearReplace
	repair              Repair
	clean               Clean
	waste               Waste
	recoveryService     RecoveryService
	buildingTemperature BuildingTemperature
	bedMedical          BedMedical
	growerCrop          GrowerCrop
	researchSelect      ResearchSelect
	husbandry           Husbandry
	homeCoverage        HomeCoverage
	prisonerInteraction PrisonerInteraction
	mineAcquisition     Acquisition
	wallRemoval         WallRemoval
	excavation          Excavation
	productionPolicy    ProductionPolicy
	moodRelief          MoodRelief
	namingConfirmation  NamingConfirmation
}

func NewBuildingAction(id ActionID, building Building) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewBuilding(building.definition, building.cell, building.rotation, building.stuff); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: BuildingAction, building: building}, nil
}
func (a Action) ID() ActionID               { return a.id }
func (a Action) Kind() ActionKind           { return a.kind }
func (a Action) Building() (Building, bool) { return a.building, a.kind == BuildingAction }
func SupportedActionKinds() []ActionKind {
	return []ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, WorkAssignmentAction, AcquisitionAction, ZoneCreateAction, TendAction, RescueAction, CaptureAction, RangedAttackAction, ProductionBillAction, HaulAction, EquipAction, GearReplaceAction, RepairAction, CleanAction, WasteAction, RecoveryServiceAction, MovementAction, BuildingTemperatureAction, BedMedicalAction, GrowerCropAction, ResearchSelectAction, HusbandryAction, HomeCoverageAction, PrisonerInteractionAction, MineAcquisitionAction, WallRemovalAction, ExcavationAction, ProductionPolicyAction, MoodReliefAction, NamingConfirmationAction}
}
func ValidateHandlerCoverage(kinds []ActionKind) error {
	seen := make(map[ActionKind]bool)
	for _, kind := range kinds {
		if (kind != BuildingAction && kind != OwnedDraftAction && kind != MeleeAttackAction && kind != SupplyAllowAction && kind != WorkAssignmentAction && kind != AcquisitionAction && kind != ZoneCreateAction && kind != TendAction && kind != RescueAction && kind != CaptureAction && kind != RangedAttackAction && kind != ProductionBillAction && kind != HaulAction && kind != EquipAction && kind != GearReplaceAction && kind != RepairAction && kind != CleanAction && kind != WasteAction && kind != RecoveryServiceAction && kind != MovementAction && kind != BuildingTemperatureAction && kind != BedMedicalAction && kind != GrowerCropAction && kind != ResearchSelectAction && kind != HusbandryAction && kind != HomeCoverageAction && kind != PrisonerInteractionAction && kind != MineAcquisitionAction && kind != WallRemovalAction && kind != ExcavationAction && kind != ProductionPolicyAction && kind != MoodReliefAction && kind != NamingConfirmationAction) || seen[kind] {
			return fmt.Errorf("unknown or duplicate action handler %q", kind)
		}
		seen[kind] = true
	}
	for _, kind := range SupportedActionKinds() {
		if !seen[kind] {
			return fmt.Errorf("missing action handler %q", kind)
		}
	}
	return nil
}

type PlanSpec struct {
	id           PlanID
	revision     PlanRevision
	actions      []Action
	dependencies []ActionDependency
}

// ActionDependency requires observed completion of another action in this plan.
// It orders work; current native legality and colony invariants are still checked
// at dispatch. It does not assert that an old completed object still exists.
type ActionDependency struct{ Action, Requires ActionID }

func NewPlan(id PlanID, revision PlanRevision, actions []Action, dependencies ...ActionDependency) (PlanSpec, error) {
	if !validID(string(id)) {
		return PlanSpec{}, errors.New("invalid plan identity")
	}
	seen := make(map[ActionID]Action)
	for _, a := range actions {
		var canonical Action
		var err error
		switch a.kind {
		case BuildingAction:
			canonical, err = NewBuildingAction(a.id, a.building)
		case OwnedDraftAction:
			canonical, err = NewOwnedDraftAction(a.id, a.draft)
		case MeleeAttackAction:
			canonical, err = NewMeleeAttackAction(a.id, a.melee)
		case WorkAssignmentAction:
			canonical, err = NewWorkAssignmentAction(a.id, a.work)
		case ProductionBillAction:
			canonical, err = NewProductionBillAction(a.id, a.bill)
		case ZoneCreateAction:
			canonical, err = NewZoneCreateAction(a.id, a.zone)
		case AcquisitionAction:
			canonical, err = NewAcquisitionAction(a.id, a.acquisition)
		case SupplyAllowAction:
			canonical, err = NewSupplyAllowAction(a.id, a.supply)
		case TendAction:
			canonical, err = NewTendAction(a.id, a.tend)
		case RescueAction:
			canonical, err = NewRescueAction(a.id, a.rescue)
		case CaptureAction:
			canonical, err = NewCaptureAction(a.id, a.capture)
		case RangedAttackAction:
			canonical, err = NewRangedAttackAction(a.id, a.ranged)
		case HaulAction:
			canonical, err = NewHaulAction(a.id, a.haul)
		case EquipAction:
			canonical, err = NewEquipAction(a.id, a.equip)
		case GearReplaceAction:
			canonical, err = NewGearReplaceAction(a.id, a.gearReplace)
		case RepairAction:
			canonical, err = NewRepairAction(a.id, a.repair)
		case CleanAction:
			canonical, err = NewCleanAction(a.id, a.clean)
		case WasteAction:
			canonical, err = NewWasteAction(a.id, a.waste)
		case RecoveryServiceAction:
			canonical, err = NewRecoveryServiceAction(a.id, a.recoveryService)
		case MovementAction:
			canonical, err = NewMovementAction(a.id, a.movement)
		case BuildingTemperatureAction:
			canonical, err = NewBuildingTemperatureAction(a.id, a.buildingTemperature)
		case BedMedicalAction:
			canonical, err = NewBedMedicalAction(a.id, a.bedMedical)
		case GrowerCropAction:
			canonical, err = NewGrowerCropAction(a.id, a.growerCrop)
		case ResearchSelectAction:
			canonical, err = NewResearchSelectAction(a.id, a.researchSelect)
		case HusbandryAction:
			canonical, err = NewHusbandryAction(a.id, a.husbandry)
		case HomeCoverageAction:
			canonical, err = NewHomeCoverageAction(a.id, a.homeCoverage)
		case PrisonerInteractionAction:
			canonical, err = NewPrisonerInteractionAction(a.id, a.prisonerInteraction)
		case MineAcquisitionAction:
			canonical, err = NewMineAcquisitionAction(a.id, a.mineAcquisition)
		case WallRemovalAction:
			canonical, err = NewWallRemovalAction(a.id, a.wallRemoval)
		case ExcavationAction:
			canonical, err = NewExcavationAction(a.id, a.excavation)
		case ProductionPolicyAction:
			canonical, err = NewProductionPolicyAction(a.id, a.productionPolicy)
		case MoodReliefAction:
			canonical, err = NewMoodReliefAction(a.id, a.moodRelief)
		case NamingConfirmationAction:
			canonical, err = NewNamingConfirmationAction(a.id, a.namingConfirmation)
		default:
			return PlanSpec{}, errors.New("unsupported action variant")
		}
		if err != nil {
			return PlanSpec{}, err
		}
		if canonical != a {
			return PlanSpec{}, errors.New("mixed action variants")
		}
		if _, exists := seen[a.id]; exists {
			return PlanSpec{}, fmt.Errorf("duplicate action identity %q", a.id)
		}
		if a.kind == MeleeAttackAction {
			prerequisite, exists := seen[a.melee.draftAction]
			if !exists || prerequisite.kind != OwnedDraftAction || prerequisite.draft.pawn != a.melee.pawn {
				return PlanSpec{}, errors.New("melee attack requires its preceding owned draft for the same pawn")
			}
		}
		if a.kind == RangedAttackAction {
			prerequisite, exists := seen[a.ranged.draftAction]
			if !exists || prerequisite.kind != OwnedDraftAction || prerequisite.draft.pawn != a.ranged.pawn {
				return PlanSpec{}, errors.New("ranged attack requires its preceding owned draft for the same pawn")
			}
		}
		if a.kind == MovementAction {
			prerequisite, exists := seen[a.movement.draftAction]
			if !exists || prerequisite.kind != OwnedDraftAction || prerequisite.draft.pawn != a.movement.pawn {
				return PlanSpec{}, errors.New("movement requires its preceding owned draft for the same pawn")
			}
		}
		if a.kind == WallRemovalAction && a.wallRemoval.backupOf != "" {
			prerequisite, exists := seen[a.wallRemoval.backupOf]
			building, isBuilding := prerequisite.Building()
			if !exists || !isBuilding || building.Definition() != "Wall" {
				return PlanSpec{}, errors.New("backup wall removal requires its preceding backup wall in the same bundle")
			}
		}
		seen[a.id] = a
	}
	deps, err := validateDependencies(seen, dependencies)
	if err != nil {
		return PlanSpec{}, err
	}
	return PlanSpec{id, revision, append([]Action(nil), actions...), deps}, nil
}
func (p PlanSpec) ID() PlanID             { return p.id }
func (p PlanSpec) Revision() PlanRevision { return p.revision }
func (p PlanSpec) Actions() []Action      { return append([]Action(nil), p.actions...) }
func (p PlanSpec) Dependencies() []ActionDependency {
	return append([]ActionDependency(nil), p.dependencies...)
}
func (p PlanSpec) Validate() error {
	_, err := NewPlan(p.id, p.revision, p.actions, p.dependencies...)
	return err
}

func validateDependencies(actions map[ActionID]Action, dependencies []ActionDependency) ([]ActionDependency, error) {
	if len(dependencies) > 4096 {
		return nil, errors.New("too many action dependencies")
	}
	deps := append([]ActionDependency(nil), dependencies...)
	sort.Slice(deps, func(i, j int) bool {
		if deps[i].Action != deps[j].Action {
			return deps[i].Action < deps[j].Action
		}
		return deps[i].Requires < deps[j].Requires
	})
	graph := map[ActionID][]ActionID{}
	for i, d := range deps {
		_, hasAction := actions[d.Action]
		_, hasRequired := actions[d.Requires]
		if !hasAction || !hasRequired || d.Action == d.Requires || i > 0 && d == deps[i-1] {
			return nil, errors.New("invalid or duplicate dependency")
		}
		graph[d.Action] = append(graph[d.Action], d.Requires)
	}
	// Melee, ranged attacks and movement already have a mandatory draft prerequisite.
	// Include it in cycle detection without changing either family's exact
	// owned-claim checks.
	for id, a := range actions {
		if melee, k := a.MeleeAttack(); k {
			graph[id] = append(graph[id], melee.DraftAction())
		}
		if ranged, k := a.RangedAttack(); k {
			graph[id] = append(graph[id], ranged.DraftAction())
		}
		if movement, k := a.Movement(); k {
			graph[id] = append(graph[id], movement.DraftAction())
		}
		if removal, k := a.WallRemoval(); k && removal.BackupOf() != "" {
			graph[id] = append(graph[id], removal.BackupOf())
		}
	}
	visiting, done := map[ActionID]bool{}, map[ActionID]bool{}
	var visit func(ActionID) bool
	visit = func(id ActionID) bool {
		if visiting[id] {
			return false
		}
		if done[id] {
			return true
		}
		visiting[id] = true
		for _, required := range graph[id] {
			if !visit(required) {
				return false
			}
		}
		visiting[id] = false
		done[id] = true
		return true
	}
	for id := range graph {
		if !visit(id) {
			return nil, errors.New("cyclic action dependency")
		}
	}
	return deps, nil
}

var ErrDependency = errors.New("action prerequisite has not completed in the current world")

func (p PlanSpec) CheckDependencies(action ActionID, progress []Progress, current GenerationSnapshot, tick Tick) error {
	if current.Validate() != nil || current.Plan != p.id || current.Revision != p.revision || tick < 0 {
		return ErrDependency
	}
	known := false
	for _, a := range p.actions {
		known = known || a.id == action
	}
	if !known {
		return ErrDependency
	}
	byID := map[ActionID]Progress{}
	for _, v := range progress {
		if _, duplicate := byID[v.View().Action]; duplicate {
			return ErrDependency
		}
		byID[v.View().Action] = v
	}
	for _, d := range p.dependencies {
		if d.Action != action {
			continue
		}
		v, exists := byID[d.Requires]
		s := v.View()
		effect, k := s.Effect.Value()
		if !exists || s.Plan != p.id || s.Revision != p.revision || s.Stage != Completed || s.Unresolved || !k || effect != EffectCompleted || !s.Snapshot.sameWorld(current) || s.Tick > tick {
			return ErrDependency
		}
	}
	return nil
}
