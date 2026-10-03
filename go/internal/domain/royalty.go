package domain

import "errors"

const RoyaltyAction ActionKind = "royalty"

// RoyaltyVerb is the closed set of royalty writes a Royalty command asks of
// the colonist's title standing; later verbs (abdicate, reset permits) add
// an arm here and native-side, not an action kind.
type RoyaltyVerb string

const (
	// RoyaltyChoosePermit spends permit points on one permit
	// (Pawn_RoyaltyTracker.AddPermit, the player's title UI).
	RoyaltyChoosePermit RoyaltyVerb = "choose_permit"
)

// Royalty is one royalty write to one colonist's standing with one faction
// (#1606, epic #1598). Permit names the permit def the verb takes; the
// game's own checks (title high enough, points available, not yet held)
// decide whether it applies.
type Royalty struct {
	pawn    PawnID
	faction string
	verb    RoyaltyVerb
	permit  string
}

func NewRoyalty(pawn PawnID, faction string, verb RoyaltyVerb, permit string) (Royalty, error) {
	if !validID(string(pawn)) {
		return Royalty{}, errors.New("royalty requires a valid pawn identity")
	}
	if !validID(faction) {
		return Royalty{}, errors.New("royalty requires a faction def")
	}
	if verb != RoyaltyChoosePermit {
		return Royalty{}, errors.New("unsupported royalty verb")
	}
	if !validID(permit) {
		return Royalty{}, errors.New("royalty verb requires a permit def")
	}
	return Royalty{pawn: pawn, faction: faction, verb: verb, permit: permit}, nil
}

func (r Royalty) Pawn() PawnID      { return r.pawn }
func (r Royalty) Faction() string   { return r.faction }
func (r Royalty) Verb() RoyaltyVerb { return r.verb }
func (r Royalty) Permit() string    { return r.permit }

func NewRoyaltyAction(id ActionID, royalty Royalty) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewRoyalty(royalty.pawn, royalty.faction, royalty.verb, royalty.permit); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: RoyaltyAction, royalty: royalty}, nil
}

func (a Action) Royalty() (Royalty, bool) { return a.royalty, a.kind == RoyaltyAction }
