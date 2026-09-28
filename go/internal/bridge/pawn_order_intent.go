package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// pawnOrderIntent is the PawnOrderIntent of one pawn, one target and one
// order kind. Native checks both live and builds the game's own job; a pawn
// already running it on the target applies again.
func pawnOrderIntent(pawn domain.PawnID, target string, kind o.PawnOrderKind) (*o.Action, error) {
	if validID(string(pawn)) != nil || validID(target) != nil || string(pawn) == target {
		return nil, contract("pawn order intent requires a distinct pawn and target")
	}
	return &o.Action{Intent: &o.Action_PawnOrder{PawnOrder: &o.PawnOrderIntent{
		PawnId: proto.String(string(pawn)), TargetId: proto.String(target), Kind: kind.Enum()}}}, nil
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
	return pawnOrderIntent(v.Pawn(), string(v.Target()), o.PawnOrderKind_PAWN_ORDER_KIND_SUBDUE)
}

func repairAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Repair()
	if !ok {
		return nil, contract("not a repair action")
	}
	return pawnOrderIntent(v.Pawn(), v.Structure(), o.PawnOrderKind_PAWN_ORDER_KIND_REPAIR)
}

func cleanAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Clean()
	if !ok {
		return nil, contract("not a clean action")
	}
	return pawnOrderIntent(v.Pawn(), v.Filth(), o.PawnOrderKind_PAWN_ORDER_KIND_CLEAN)
}

func openCasketAction(action domain.Action) (*o.Action, error) {
	v, ok := action.OpenCasket()
	if !ok {
		return nil, contract("not an open casket action")
	}
	return pawnOrderIntent(v.Pawn(), v.Casket(), o.PawnOrderKind_PAWN_ORDER_KIND_OPEN_CASKET)
}

func tendAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Tend()
	if !ok {
		return nil, contract("not a tend action")
	}
	return pawnOrderIntent(v.Doctor(), string(v.Patient()), o.PawnOrderKind_PAWN_ORDER_KIND_TEND)
}

func equipAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Equip()
	if !ok {
		return nil, contract("not an equip action")
	}
	return pawnOrderIntent(v.Pawn(), v.Thing(), o.PawnOrderKind_PAWN_ORDER_KIND_EQUIP)
}

func rescueAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Rescue()
	if !ok {
		return nil, contract("not a rescue action")
	}
	return pawnOrderIntent(v.Rescuer(), string(v.Patient()), o.PawnOrderKind_PAWN_ORDER_KIND_RESCUE)
}

// captureAction orders a capture of a downed pawn, or, with a bed, an arrest
// of a standing one; the arrest rides the capturer's coupled owned draft.
func captureAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Capture()
	if !ok {
		return nil, contract("not a capture action")
	}
	if !v.Arrest() {
		return pawnOrderIntent(v.Capturer(), string(v.Patient()), o.PawnOrderKind_PAWN_ORDER_KIND_CAPTURE)
	}
	if validID(v.Bed()) != nil {
		return nil, contract("arrest intent requires a valid bed")
	}
	out, err := pawnOrderIntent(v.Capturer(), string(v.Patient()), o.PawnOrderKind_PAWN_ORDER_KIND_ARREST)
	if err == nil {
		out.GetPawnOrder().BedId = proto.String(v.Bed())
	}
	return out, err
}

// wearAction orders a pawn to wear one loose apparel item as ordered work,
// not a forced outfit entry; native checks eligibility and gain live.
func wearAction(action domain.Action) (*o.Action, error) {
	v, ok := action.GearReplace()
	if !ok {
		return nil, contract("not a gear replace action")
	}
	return pawnOrderIntent(v.Pawn(), v.Thing(), o.PawnOrderKind_PAWN_ORDER_KIND_WEAR)
}

// useItemAction is the UseItemIntent (#1038): one colonist uses one
// targetable item (a worn lance's verb, a CompTargetable item) on one pawn;
// native validates the verb or use comp against the target live.
func useItemAction(action domain.Action) (*o.Action, error) {
	v, ok := action.UseItem()
	if !ok {
		return nil, contract("not a use item action")
	}
	if validID(string(v.Pawn())) != nil || validID(v.Item()) != nil || validID(string(v.Target())) != nil || v.Pawn() == v.Target() {
		return nil, contract("use item intent requires a distinct pawn, item and target")
	}
	return &o.Action{Intent: &o.Action_UseItem{UseItem: &o.UseItemIntent{
		PawnId: proto.String(string(v.Pawn())), ItemId: proto.String(v.Item()), TargetId: proto.String(string(v.Target()))}}}, nil
}

// moodReliefAction is the NeedReliefIntent: native offers the pawn the job
// its own need giver issues, checked live.
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
	return &o.Action{Intent: &o.Action_NeedRelief{NeedRelief: &o.NeedReliefIntent{PawnId: proto.String(string(v.Pawn())), Need: need.Enum()}}}, nil
}
