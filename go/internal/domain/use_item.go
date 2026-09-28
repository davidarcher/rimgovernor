package domain

import "errors"

// UseItem orders one colonist to use one targetable item on one pawn
// (#1038): a worn or equipped item's target verb (the psychic shock and
// insanity lances' Verb_CastTargetEffect) or a CompTargetable item's use
// job. Native validates the user, the item's verb or use comp and the
// target live when the UseItemIntent applies.
type UseItem struct {
	pawn, target PawnID
	item         string
}

func NewUseItem(pawn PawnID, item string, target PawnID) (UseItem, error) {
	if !validID(string(pawn)) || !validID(item) || !validID(string(target)) || pawn == target || item == string(pawn) || item == string(target) {
		return UseItem{}, errors.New("use item requires distinct valid pawn, item and target identities")
	}
	return UseItem{pawn: pawn, item: item, target: target}, nil
}

func (u UseItem) Pawn() PawnID   { return u.pawn }
func (u UseItem) Item() string   { return u.item }
func (u UseItem) Target() PawnID { return u.target }

const UseItemAction ActionKind = "use_item"

func NewUseItemAction(id ActionID, use UseItem) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewUseItem(use.pawn, use.item, use.target); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: UseItemAction, useItem: use}, nil
}

func (a Action) UseItem() (UseItem, bool) { return a.useItem, a.kind == UseItemAction }
