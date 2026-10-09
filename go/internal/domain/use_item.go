package domain

import "errors"

// UseItem orders one colonist to use one item: a worn or equipped
// item's target verb on a pawn (the psychic shock and insanity lances'
// Verb_CastTargetEffect), a CompTargetable item's use job on a pawn, or,
// when the pawn is its own target, the use job of a CompUsable item
// without a target comp (a neuroformer). Native validates the user, the
// item's verb or use comp and the target live when the GiveJobIntent
// UseItem applies.
type UseItem struct {
	pawn, target PawnID
	item         string
}

// SelfUse reports the colonist using the item on itself.
func (u UseItem) SelfUse() bool { return u.pawn == u.target }

func NewUseItem(pawn PawnID, item string, target PawnID) (UseItem, error) {
	if !validID(string(pawn)) || !validID(item) || !validID(string(target)) || item == string(pawn) || item == string(target) {
		return UseItem{}, errors.New("use item requires valid pawn, item and target identities, the item distinct from both")
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
