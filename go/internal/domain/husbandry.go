package domain

import "errors"

// HusbandryMethod names the direct-write animal management orders, mirroring
// bridge.HusbandryMethod: a recursive training request; a slaughter, tame or
// release-to-wild designation; or one Animals-tab setting (allowed area,
// master, follow-drafted, follow-fieldwork). All are direct settings writes
// (no native job), so admission is the effect, not a promise of one; the
// taming and release work itself is native handler labor afterwards. Tame
// targets a wild animal, the others a player animal.
type HusbandryMethod string

const (
	HusbandryTrain           HusbandryMethod = "train"
	HusbandrySlaughter       HusbandryMethod = "slaughter"
	HusbandryTame            HusbandryMethod = "tame"
	HusbandryRelease         HusbandryMethod = "release"
	HusbandryAllowedArea     HusbandryMethod = "allowed_area"
	HusbandryMaster          HusbandryMethod = "master"
	HusbandryFollowDrafted   HusbandryMethod = "follow_drafted"
	HusbandryFollowFieldwork HusbandryMethod = "follow_fieldwork"
)

// Husbandry is explicit intent to write one already-observed animal's
// training request, slaughter/tame/release designation or Animals-tab
// setting. Argument is the method's one parameter: the trainable def for
// train; the area or master identity for allowed_area/master (empty clears
// the assignment); "true"/"false" for the follow flags; empty for the
// designations. Native eligibility (canTrain, safeToSlaughter, tameable,
// safeToRelease, obedient, supportsAllowedAreas) is established at
// inspection, not here.
type Husbandry struct {
	animal   PawnID
	method   HusbandryMethod
	argument string
}

func NewHusbandry(animal PawnID, method HusbandryMethod, argument string) (Husbandry, error) {
	if !validID(string(animal)) {
		return Husbandry{}, errors.New("husbandry requires a valid animal identity")
	}
	switch method {
	case HusbandryTrain:
		if !validID(argument) {
			return Husbandry{}, errors.New("husbandry training requires a valid trainable definition")
		}
	case HusbandrySlaughter, HusbandryTame, HusbandryRelease:
		if argument != "" {
			return Husbandry{}, errors.New("husbandry designation does not take an argument")
		}
	case HusbandryAllowedArea, HusbandryMaster:
		if argument != "" && !validID(argument) {
			return Husbandry{}, errors.New("husbandry assignment requires a valid target identity or none")
		}
	case HusbandryFollowDrafted, HusbandryFollowFieldwork:
		if argument != "true" && argument != "false" {
			return Husbandry{}, errors.New("husbandry follow flag requires true or false")
		}
	default:
		return Husbandry{}, errors.New("invalid husbandry method")
	}
	return Husbandry{animal: animal, method: method, argument: argument}, nil
}

func (h Husbandry) Animal() PawnID          { return h.animal }
func (h Husbandry) Method() HusbandryMethod { return h.method }
func (h Husbandry) Argument() string        { return h.argument }

// TrainableDef is the training argument, empty for every other method.
func (h Husbandry) TrainableDef() string {
	if h.method == HusbandryTrain {
		return h.argument
	}
	return ""
}

const HusbandryAction ActionKind = "husbandry"

func NewHusbandryAction(id ActionID, husbandry Husbandry) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewHusbandry(husbandry.animal, husbandry.method, husbandry.argument); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: HusbandryAction, husbandry: husbandry}, nil
}

func (a Action) Husbandry() (Husbandry, bool) { return a.husbandry, a.kind == HusbandryAction }
