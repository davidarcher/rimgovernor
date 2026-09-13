package domain

import "errors"

// HusbandryMethod names MaintainHerd-*'s two direct-write animal management
// orders, mirroring bridge.HusbandryMethod: a recursive training request or a
// slaughter designation. Both are direct settings writes (no native job), so
// admission is the effect, not a promise of one.
type HusbandryMethod string

const (
	HusbandryTrain     HusbandryMethod = "train"
	HusbandrySlaughter HusbandryMethod = "slaughter"
)

// Husbandry is explicit intent to write one already-observed animal's
// training request or slaughter designation. Native eligibility (canTrain,
// safeToSlaughter, protected/breeding-reserve policy) is established at
// inspection, not here.
type Husbandry struct {
	animal       PawnID
	method       HusbandryMethod
	trainableDef string
}

func NewHusbandry(animal PawnID, method HusbandryMethod, trainableDef string) (Husbandry, error) {
	if !validID(string(animal)) {
		return Husbandry{}, errors.New("husbandry requires a valid animal identity")
	}
	switch method {
	case HusbandryTrain:
		if !validID(trainableDef) {
			return Husbandry{}, errors.New("husbandry training requires a valid trainable definition")
		}
	case HusbandrySlaughter:
		if trainableDef != "" {
			return Husbandry{}, errors.New("husbandry slaughter does not take a trainable definition")
		}
	default:
		return Husbandry{}, errors.New("invalid husbandry method")
	}
	return Husbandry{animal: animal, method: method, trainableDef: trainableDef}, nil
}

func (h Husbandry) Animal() PawnID          { return h.animal }
func (h Husbandry) Method() HusbandryMethod { return h.method }
func (h Husbandry) TrainableDef() string    { return h.trainableDef }

const HusbandryAction ActionKind = "husbandry"

func NewHusbandryAction(id ActionID, husbandry Husbandry) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewHusbandry(husbandry.animal, husbandry.method, husbandry.trainableDef); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: HusbandryAction, husbandry: husbandry}, nil
}

func (a Action) Husbandry() (Husbandry, bool) { return a.husbandry, a.kind == HusbandryAction }
