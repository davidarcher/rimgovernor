package domain

import (
	"encoding/json"
	"errors"
)

const CombatBatchAction ActionKind = "combat_batch"

// CombatCommand is a native combat order. Policy reasons are deliberately not
// part of this command: only the immutable dispatched effect is journaled.
type CombatCommand struct {
	Kind     string
	Pawn     PawnID
	Target   PawnID
	Cell     Cell
	Aim      Cell
	Door     string
	FireMode string
	Clear    bool
	Shell    string
	Drug     string
}

// CombatBatch keeps ordered commands immutable and comparable, like RulesAttach.
// Fight is the session fight plan; Key identifies this exact tactical decision.
type CombatBatch struct {
	fight       PlanID
	key, orders string
}

func NewCombatBatch(fight PlanID, key string, orders []CombatCommand) (CombatBatch, error) {
	if !validID(string(fight)) || !validID(key) || len(orders) == 0 {
		return CombatBatch{}, errors.New("invalid combat batch")
	}
	for _, o := range orders {
		if o.Cell.X < 0 || o.Cell.Z < 0 || o.Aim.X < 0 || o.Aim.Z < 0 {
			return CombatBatch{}, errors.New("invalid combat cell")
		}
		switch o.Kind {
		case "door", "mortar_fire":
			if o.Pawn != "" {
				return CombatBatch{}, errors.New("combat structure order names pawn")
			}
		case "draft", "move", "attack", "rescue", "man_mortar", "attack_ground", "repair", "stop", "hold_position", "release", "animal_area", "animal_clear", "fire_mode", "drug":
			if !validID(string(o.Pawn)) {
				return CombatBatch{}, errors.New("combat pawn missing")
			}
		default:
			return CombatBatch{}, errors.New("unsupported combat command")
		}
		if (o.Kind == "attack" || o.Kind == "rescue" || o.Kind == "release") && (!validID(string(o.Target)) || o.Target == o.Pawn) {
			return CombatBatch{}, errors.New("invalid combat target")
		}
		if o.Kind == "door" && o.Door != "allow" && o.Door != "forbid" && o.Door != "hold_open" && o.Door != "close" {
			return CombatBatch{}, errors.New("invalid combat door mode")
		}
		if o.Kind == "fire_mode" && o.FireMode != "fire_at_will" && o.FireMode != "hold_fire" {
			return CombatBatch{}, errors.New("invalid combat fire mode")
		}
		if o.Kind == "drug" && !validID(o.Drug) {
			return CombatBatch{}, errors.New("invalid combat drug")
		}
	}
	raw, err := json.Marshal(orders)
	return CombatBatch{fight, key, string(raw)}, err
}
func (b CombatBatch) Fight() PlanID { return b.fight }
func (b CombatBatch) Key() string   { return b.key }
func (b CombatBatch) Orders() []CombatCommand {
	var v []CombatCommand
	_ = json.Unmarshal([]byte(b.orders), &v)
	return v
}
func NewCombatBatchAction(id ActionID, b CombatBatch) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid combat action id")
	}
	v, err := NewCombatBatch(b.fight, b.key, b.Orders())
	if err != nil || v != b {
		return Action{}, errors.New("invalid combat batch value")
	}
	return Action{id: id, kind: CombatBatchAction, combatBatch: b}, nil
}
func (a Action) CombatBatch() (CombatBatch, bool) { return a.combatBatch, a.kind == CombatBatchAction }

// CombatResult preserves one native result. Missing results remain unknown.
type CombatResult struct {
	Index   int
	PawnID  string
	Applied bool
	Refusal string
	JobDef  string
}
type CombatResults struct{ encoded string }

func (r CombatResults) MarshalJSON() ([]byte, error) { return json.Marshal(r.Orders()) }

func (r CombatResults) Orders() []CombatResult {
	var v []CombatResult
	_ = json.Unmarshal([]byte(r.encoded), &v)
	return v
}
func (p Progress) RecordCombatReceipt(attempt AttemptID, receipt Receipt, results []CombatResult) (Progress, error) {
	b, ok := p.action.CombatBatch()
	if !ok {
		return p, errors.New("not a combat action")
	}
	if receipt == ReceiptAccepted && len(results) != len(b.Orders()) {
		return p, errors.New("combat results missing")
	}
	if receipt != ReceiptAccepted && len(results) != 0 {
		return p, errors.New("combat results without applied receipt")
	}
	for i, r := range results {
		if r.Index != i || r.PawnID != string(b.Orders()[i].Pawn) || r.Applied && r.Refusal != "" || !r.Applied && r.Refusal == "" {
			return p, errors.New("invalid combat result")
		}
	}
	next, err := p.recordReceipt(attempt, receipt)
	if err != nil {
		return p, err
	}
	if receipt == ReceiptAccepted {
		raw, err := json.Marshal(results)
		if err != nil {
			return p, err
		}
		next.view.Combat = Known(CombatResults{string(raw)})
		next.view.Unresolved = false
		if next.view.Stage != Cancelled {
			next.view.Stage = Completed
		}
		next.view.Effect = Known(EffectCompleted)
	} else if receipt == ReceiptRefused {
		next.view.Unresolved = false
		next.view.Stage = Unsuccessful
		next.view.Effect = Known(EffectAbsent)
	}
	return next, nil
}
