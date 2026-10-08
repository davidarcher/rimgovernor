package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// Vanilla JobDef names a GiveJobIntent orders (#1352).
const (
	JobRepair      = "Repair"
	JobClean       = "Clean"
	JobOpen        = "Open"
	JobTendPatient = "TendPatient"
	JobEquip       = "Equip"
	// JobDropWeapon is this protocol's token, not a JobDef name: native drops the weapon the pawn holds with the game's own
	// drop job, which it finds by its driver class (#1740).
	JobDropWeapon  = "DropWeapon"
	JobWear        = "Wear"
	JobRescue      = "Rescue"
	JobCapture     = "Capture"
	JobArrest      = "Arrest"
	JobAttackMelee = "AttackMelee"
	JobUseItem     = "UseItem"
	// JobSlaughter is the Handling giver's slaughter of a designated animal,
	// ordered as a prioritized job.
	JobSlaughter = "Slaughter"
	// Recovery service and waste hauling (#1351).
	JobFixBrokenDownBuilding = "FixBrokenDownBuilding"
	JobRefuel                = "Refuel"
	// The void monolith's orders (#2437): the Inactive monolith is
	// investigated, every later level activated.
	JobInvestigateMonolith = "InvestigateMonolith"
	JobActivateMonolith    = "ActivateMonolith"
)

// giveJob is the GiveJobIntent of one pawn, one vanilla job and its targets
// in job target order. Native checks the game rules live and builds the
// game's own job; a pawn already running it on the targets applies again.
func giveJob(pawn domain.PawnID, job string, targets ...string) (*o.Action, error) {
	return giveJobTo(pawn, job, false, targets...)
}

// giveJobTo is giveJob; self admits the pawn among the targets, for the one
// job that may target its own user (UseItem, #1609).
func giveJobTo(pawn domain.PawnID, job string, self bool, targets ...string) (*o.Action, error) {
	if validID(string(pawn)) != nil || validID(job) != nil || len(targets) == 0 {
		return nil, contract("give job intent requires a pawn, a job and a target")
	}
	for _, t := range targets {
		if validID(t) != nil || t == string(pawn) && !self {
			return nil, contract("give job intent requires valid targets distinct from the pawn")
		}
	}
	return &o.Action{Intent: &o.Action_GiveJob{GiveJob: &o.GiveJobIntent{
		Pawn: NewRef(string(pawn)), Job: proto.String(job), Targets: NewRefs(targets)}}}, nil
}

// PrioritizedJob is the vanilla "Prioritize" order (#1352): pawn takes job
// (a JobDef name such as FinishFrame, HaulToContainer or Deconstruct) on
// target as a player-forced order, built by the first work giver the pawn
// may do that makes exactly that job.
func PrioritizedJob(pawn domain.PawnID, job, target string) (*o.Action, error) {
	out, err := giveJob(pawn, job, target)
	if err == nil {
		out.GetGiveJob().Options = &o.GiveJobOptions{Prioritized: proto.Bool(true)}
	}
	return out, err
}

// draftAction drafts one plan-owned pawn (#939).
func draftAction(action domain.Action) (*o.Action, error) {
	v, ok := action.OwnedDraft()
	if !ok || validID(string(v.Pawn())) != nil {
		return nil, contract("not a draft action")
	}
	return &o.Action{Intent: &o.Action_Draft{Draft: &o.DraftIntent{PawnId: proto.String(string(v.Pawn())), Drafted: proto.Bool(true)}}}, nil
}

// UndraftAction is the wire intent that undrafts one pawn no live plan
// needs (#939).
func UndraftAction(key string, pawn domain.PawnID) (*o.Action, error) {
	if validID(key) != nil || validID(string(pawn)) != nil {
		return nil, contract("undraft requires a key and a pawn")
	}
	return &o.Action{Key: proto.String(key), Intent: &o.Action_Draft{Draft: &o.DraftIntent{PawnId: proto.String(string(pawn)), Drafted: proto.Bool(false)}}}, nil
}

func subdueAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Subdue()
	if !ok {
		return nil, contract("not a subdue action")
	}
	return giveJob(v.Pawn(), JobAttackMelee, string(v.Target()))
}

func repairAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Repair()
	if !ok {
		return nil, contract("not a repair action")
	}
	return giveJob(v.Pawn(), JobRepair, v.Structure())
}

func cleanAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Clean()
	if !ok {
		return nil, contract("not a clean action")
	}
	return giveJob(v.Pawn(), JobClean, v.Filth())
}

func openCasketAction(action domain.Action) (*o.Action, error) {
	v, ok := action.OpenCasket()
	if !ok {
		return nil, contract("not an open casket action")
	}
	return giveJob(v.Pawn(), JobOpen, v.Casket())
}

func tendAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Tend()
	if !ok {
		return nil, contract("not a tend action")
	}
	return giveJob(v.Doctor(), JobTendPatient, string(v.Patient()))
}

func equipAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Equip()
	if !ok {
		return nil, contract("not an equip action")
	}
	return giveJob(v.Pawn(), JobEquip, v.Thing())
}

// dropEquipmentAction orders a pawn to drop the weapon it holds; native
// checks the weapon is in the pawn's hands live.
func dropEquipmentAction(action domain.Action) (*o.Action, error) {
	v, ok := action.DropEquipment()
	if !ok {
		return nil, contract("not a drop equipment action")
	}
	return giveJob(v.Pawn(), JobDropWeapon, v.Thing())
}

func rescueAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Rescue()
	if !ok {
		return nil, contract("not a rescue action")
	}
	return giveJob(v.Rescuer(), JobRescue, string(v.Patient()))
}

// captureAction orders a capture of a downed pawn, or, with a bed, an arrest
// of a standing one; the arrest rides the capturer's coupled owned draft.
func captureAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Capture()
	if !ok {
		return nil, contract("not a capture action")
	}
	if !v.Arrest() {
		return giveJob(v.Capturer(), JobCapture, string(v.Patient()))
	}
	if validID(v.Bed()) != nil {
		return nil, contract("arrest intent requires a valid bed")
	}
	return giveJob(v.Capturer(), JobArrest, string(v.Patient()), v.Bed())
}

// wearAction orders a pawn to wear one loose apparel item as ordered work,
// not a forced outfit entry; native checks eligibility and gain live.
func wearAction(action domain.Action) (*o.Action, error) {
	v, ok := action.GearReplace()
	if !ok {
		return nil, contract("not a gear replace action")
	}
	return giveJob(v.Pawn(), JobWear, v.Thing())
}

// useItemAction (#1038): one colonist uses one targetable item (a worn
// lance's verb, a CompTargetable item) on one pawn, or its own CompUsable
// item on itself (#1609, a neuroformer: the target is the colonist); native
// validates the verb or use comp against the target live.
func useItemAction(action domain.Action) (*o.Action, error) {
	v, ok := action.UseItem()
	if !ok {
		return nil, contract("not a use item action")
	}
	if v.Item() == string(v.Target()) {
		return nil, contract("use item intent requires a distinct item and target")
	}
	return giveJobTo(v.Pawn(), JobUseItem, v.SelfUse(), v.Item(), string(v.Target()))
}

// moodReliefAction offers the pawn the job its own need giver issues for
// one deficient need, checked live; the giver picks the target.
func moodReliefAction(action domain.Action) (*o.Action, error) {
	v, ok := action.MoodRelief()
	if !ok {
		return nil, contract("not a mood relief action")
	}
	var need o.Need
	switch v.Need() {
	case domain.MoodReliefFood:
		need = o.Need_NEED_FOOD
	case domain.MoodReliefRest:
		need = o.Need_NEED_REST
	case domain.MoodReliefJoy:
		need = o.Need_NEED_JOY
	default:
		return nil, contract("invalid mood relief need")
	}
	if validID(string(v.Pawn())) != nil {
		return nil, contract("mood relief intent requires a valid pawn")
	}
	return &o.Action{Intent: &o.Action_GiveJob{GiveJob: &o.GiveJobIntent{
		Pawn: NewRef(string(v.Pawn())), Options: &o.GiveJobOptions{RelieveNeed: need.Enum()}}}}, nil
}
