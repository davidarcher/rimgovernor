package domain

import (
	"errors"
	"fmt"
	"slices"
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
const SubdueAction ActionKind = "subdue"
const TendAction ActionKind = "tend"
const RescueAction ActionKind = "rescue"
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
const BedUseAction ActionKind = "bed_medical"
const GrowerCropAction ActionKind = "grower_crop"
const ClaimBuildingAction ActionKind = "claim_building"
const ZoneDeleteAction ActionKind = "zone_delete"
const BedAssignAction ActionKind = "bed_assign"

// ZoneEditAction is declared in zone_edit.go alongside its ZoneEdit payload.

// ConstructionCancelAction is declared in construction_cancel.go alongside its
// ConstructionCancel payload.

// WallRemovalAction is declared in wall_removal.go alongside its payload.

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
	apparelPolicy       ApparelPolicy
	id                  ActionID
	kind                ActionKind
	building            Building
	draft               OwnedDraft
	subdue              Subdue
	zone                ZoneCreate
	bill                ProductionBill
	acquisition         Acquisition
	supply              SupplyAllow
	cutPlant            CutPlant
	moveBuilding        MoveBuilding
	coverClearance      CoverClearance
	foundationRemoval   FoundationRemoval
	deconstruction      Deconstruction
	work                WorkAssignment
	tend                Tend
	rescue              Rescue
	capture             Capture
	useItem             UseItem
	strip               Strip
	movement            Movement
	haul                Haul
	equip               Equip
	gearReplace         GearReplace
	repair              Repair
	clean               Clean
	waste               Waste
	recoveryService     RecoveryService
	buildingTemperature BuildingTemperature
	bedUse              BedUse
	growerCrop          GrowerCrop
	claimBuilding       ClaimBuilding
	zoneDelete          ZoneDelete
	zoneCellEdit        ZoneCellEdit
	stockpilePatch      StockpilePatch
	openCasket          OpenCasket
	bedAssign           BedAssign
	researchSelect      ResearchSelect
	husbandry           Husbandry
	homeCoverage        HomeCoverage
	prisonerInteraction PrisonerInteraction
	questAccept         QuestAccept
	mineAcquisition     Acquisition
	wallRemoval         WallRemoval
	excavation          Excavation
	moodRelief          MoodRelief
	namingConfirmation  NamingConfirmation
	dialogAnswer        DialogAnswer
	trade               Trade
	caravanDeparture    CaravanDeparture
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

type PlanSpec struct {
	id           PlanID
	revision     PlanRevision
	actions      []Action
	dependencies []ActionDependency
}

// ActionDependency requires observed completion of another action in this plan.
// It orders work; current native legality and colony invariants are still checked
// at dispatch. It does not assert that an old completed object still exists.
//
// Coupled marks a dependency whose order is written against the result of
// the required one: an id the earlier write produced, a position it
// reached. Ordering alone is not coupling, and the count of orders in a
// step never is: an ordered-only dependent dispatches live once its
// prerequisite's outcome arrives, while a coupled one is planned at the
// first step after that outcome, ahead of the planner wave's own cadence,
// and relies on the native CAS evidence of its admission rather than on a
// stopped clock (#584; it stopped the epoch until then, #244).
type ActionDependency struct {
	Action, Requires ActionID
	Coupled          bool
}

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
		case SubdueAction:
			canonical, err = NewSubdueAction(a.id, a.subdue)
		case WorkAssignmentAction:
			canonical, err = NewWorkAssignmentAction(a.id, a.work)
		case ProductionBillAction:
			canonical, err = NewProductionBillAction(a.id, a.bill)
		case ZoneCreateAction:
			canonical, err = NewZoneCreateAction(a.id, a.zone)
		case AcquisitionAction:
			canonical, err = NewAcquisitionAction(a.id, a.acquisition)
		case SupplyAllowAction, SupplyForbidAction:
			canonical, err = NewSupplyAllowAction(a.id, a.supply)
		case DeconstructionAction:
			canonical, err = NewDeconstructionAction(a.id, a.deconstruction)
		case MoveBuildingAction:
			canonical, err = NewMoveBuildingAction(a.id, a.moveBuilding)
		case UninstallBuildingAction:
			canonical, err = NewUninstallBuildingAction(a.id, a.moveBuilding)
		case CutPlantAction:
			canonical, err = NewCutPlantAction(a.id, a.cutPlant)
		case CoverClearanceAction:
			canonical, err = NewCoverClearanceAction(a.id, a.coverClearance)
		case FoundationRemovalAction:
			canonical, err = NewFoundationRemovalAction(a.id, a.foundationRemoval)
		case TendAction:
			canonical, err = NewTendAction(a.id, a.tend)
		case RescueAction:
			canonical, err = NewRescueAction(a.id, a.rescue)
		case CaptureAction:
			canonical, err = NewCaptureAction(a.id, a.capture)
		case HaulAction:
			canonical, err = NewHaulAction(a.id, a.haul)
		case EquipAction:
			canonical, err = NewEquipAction(a.id, a.equip)
		case ApparelPolicyAction:
			canonical, err = NewApparelPolicyAction(a.id, a.apparelPolicy)
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
		case BedUseAction:
			canonical, err = NewBedUseAction(a.id, a.bedUse)
		case GrowerCropAction:
			canonical, err = NewGrowerCropAction(a.id, a.growerCrop)
		case ClaimBuildingAction:
			canonical, err = NewClaimBuildingAction(a.id, a.claimBuilding)
		case ZoneDeleteAction:
			canonical, err = NewZoneDeleteAction(a.id, a.zoneDelete)
		case ZoneCellEditAction:
			canonical, err = NewZoneCellEditAction(a.id, a.zoneCellEdit)
		case StockpilePatchAction:
			canonical, err = NewStockpilePatchAction(a.id, a.stockpilePatch)
		case OpenCasketAction:
			canonical, err = NewOpenCasketAction(a.id, a.openCasket)
		case BedAssignAction:
			canonical, err = NewBedAssignAction(a.id, a.bedAssign)
		case ResearchSelectAction:
			canonical, err = NewResearchSelectAction(a.id, a.researchSelect)
		case HusbandryAction:
			canonical, err = NewHusbandryAction(a.id, a.husbandry)
		case HomeCoverageAction:
			canonical, err = NewHomeCoverageAction(a.id, a.homeCoverage)
		case PrisonerInteractionAction:
			canonical, err = NewPrisonerInteractionAction(a.id, a.prisonerInteraction)
		case QuestAcceptAction:
			canonical, err = NewQuestAcceptAction(a.id, a.questAccept)
		case MineAcquisitionAction:
			canonical, err = NewMineAcquisitionAction(a.id, a.mineAcquisition)
		case WallRemovalAction:
			canonical, err = NewWallRemovalAction(a.id, a.wallRemoval)
		case ExcavationAction:
			canonical, err = NewExcavationAction(a.id, a.excavation)
		case MoodReliefAction:
			canonical, err = NewMoodReliefAction(a.id, a.moodRelief)
		case NamingConfirmationAction:
			canonical, err = NewNamingConfirmationAction(a.id, a.namingConfirmation)
		case DialogAnswerAction:
			canonical, err = NewDialogAnswerAction(a.id, a.dialogAnswer)
		case TradeAction:
			canonical, err = NewTradeAction(a.id, a.trade)
		case CaravanDepartureAction:
			canonical, err = NewCaravanDepartureAction(a.id, a.caravanDeparture)
		case UseItemAction:
			canonical, err = NewUseItemAction(a.id, a.useItem)
		case StripAction:
			canonical, err = NewStripAction(a.id, a.strip)
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
		if a.kind == SubdueAction {
			prerequisite, exists := seen[a.subdue.draftAction]
			if !exists || prerequisite.kind != OwnedDraftAction || prerequisite.draft.pawn != a.subdue.pawn {
				return PlanSpec{}, errors.New("subdue requires its preceding owned draft for the same pawn")
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
			if !exists || !isBuilding || building.Definition() != "Wall" || building.Cell() != a.wallRemoval.cell {
				return PlanSpec{}, errors.New("backup wall removal requires its preceding backup wall at the same cell in the same bundle")
			}
		}
		seen[a.id] = a
	}
	deps, err := validateDependencies(seen, dependencies)
	if err != nil {
		return PlanSpec{}, err
	}
	for _, a := range actions {
		if removal, ok := a.WallRemoval(); ok && removal.BackupOf() != "" && !slices.Contains(deps, ActionDependency{Action: a.ID(), Requires: removal.BackupOf()}) {
			return PlanSpec{}, errors.New("backup wall removal must declare its backup wall as a dependency")
		}
		if capture, ok := a.Capture(); ok && capture.Arrest() {
			owned := false
			for _, dep := range deps {
				draft, isDraft := seen[dep.Requires].OwnedDraft()
				owned = owned || dep.Action == a.ID() && dep.Coupled && isDraft && draft.Pawn() == capture.Capturer()
			}
			if !owned {
				return PlanSpec{}, errors.New("arrest requires a coupled owned draft for its capturer")
			}
		}
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
		if !hasAction || !hasRequired || d.Action == d.Requires || i > 0 && d.Action == deps[i-1].Action && d.Requires == deps[i-1].Requires {
			return nil, errors.New("invalid or duplicate dependency")
		}
		graph[d.Action] = append(graph[d.Action], d.Requires)
	}
	// Subdue and movement already have a mandatory draft prerequisite.
	// Include it in cycle detection.
	for id, a := range actions {
		if subdue, k := a.Subdue(); k {
			graph[id] = append(graph[id], subdue.DraftAction())
		}
		if movement, k := a.Movement(); k {
			graph[id] = append(graph[id], movement.DraftAction())
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

// CoupledPending names the actions of this plan's coupled dependencies that
// are ready to dispatch: every prerequisite has completed in the current
// world and the action itself has not been dispatched. They are the orders
// a running clock window plans live for at once (#584); a plan without
// coupled dependencies never has any.
func (p PlanSpec) CoupledPending(progress []Progress, current GenerationSnapshot, tick Tick) []ActionID {
	var ready []ActionID
	seen := map[ActionID]bool{}
	for _, d := range p.dependencies {
		if !d.Coupled || seen[d.Action] {
			continue
		}
		seen[d.Action] = true
		for _, v := range progress {
			s := v.View()
			if s.Action != d.Action || s.Unresolved || s.Stage != Pending && s.Stage != Prepared {
				continue
			}
			if p.CheckDependencies(d.Action, progress, current, tick) == nil {
				ready = append(ready, d.Action)
			}
		}
	}
	return ready
}

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
