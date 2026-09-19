package medical

import (
	"context"
	"fmt"
	"math"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:   "medical/plague-readback",
		Scope:  "Routine colonist health carries Plague severity and immunity per day, untended/tended presence and quality; colony facts retain envelope headroom.",
		Start:  cases.Fixture{Op: "test/medical_plague_prepare"},
		Budget: time.Minute,
		Run:    plagueReadback,
	})
}

func plagueReadback(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	patients := na.AsSlice(s.Prepared()["patients"])
	if len(patients) != 2 {
		return fmt.Errorf("expected two Plague patients")
	}
	identity, err := na.ReadIdentity(ctx, h, "plague-identity")
	if err != nil {
		return err
	}
	reply, err := h.Wire(ctx, "plague-readback", "observations_list_pawns", map[string]any{
		"scope":   map[string]any{"expectedIdentity": identity},
		"filter":  map[string]any{"colonist": true, "humanlike": true, "animal": false},
		"details": map[string]any{"health": true, "needs": false, "equipment": false, "biography": false, "settings": false, "social": false, "animals": false},
		"page":    map[string]any{"limit": 256},
	})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	found := 0
	for _, raw := range na.AsSlice(observed["pawns"]) {
		pawn, _ := na.AsMap(raw)
		ref, _ := na.AsMap(pawn["pawn"])
		id := na.AsString(ref["id"])
		index := -1
		for i, patient := range patients {
			if id == na.AsString(patient) {
				index = i
			}
		}
		if index < 0 {
			continue
		}
		health, _ := na.AsMap(pawn["health"])
		for _, rawCondition := range na.AsSlice(health["hediffs"]) {
			condition, _ := na.AsMap(rawCondition)
			def, _ := na.AsMap(condition["definition"])
			if na.AsString(def["defName"]) != "Plague" {
				continue
			}
			found++
			for field, want := range map[string]float64{"severity": .2, "immunity": .1, "severityPerDay": .666 - float64(index)*.3628*.75} {
				got, ok := condition[field].(float64)
				if !ok || math.Abs(got-want) > .0001 {
					return fmt.Errorf("%s %s = %v, want %g", id, field, condition[field], want)
				}
			}
			rate, ok := condition["immunityPerDay"].(float64)
			if !ok || rate < .01 || rate > 2 {
				return fmt.Errorf("%s immunity/day = %v", id, condition["immunityPerDay"])
			}
			tended, ok := na.AsBool(condition["tended"])
			if !ok || tended != (index == 1) {
				return fmt.Errorf("%s tended = %v", id, condition["tended"])
			}
			quality, present := condition["tendQuality"].(float64)
			if index == 0 && present || index == 1 && (!present || math.Abs(quality-.75) > .0001) {
				return fmt.Errorf("%s tend quality = %v", id, condition["tendQuality"])
			}
		}
	}
	if found != 2 {
		return fmt.Errorf("read %d Plague conditions, want 2", found)
	}
	s.Report()["plague_readback"] = observed
	return na.CheckCommittedSaveHeadroom(ctx, h, s.Report())
}
