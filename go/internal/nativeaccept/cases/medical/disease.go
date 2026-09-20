package medical

import (
	"context"
	"encoding/json"
	"fmt"
	"math"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:        "medical/disease",
		Scope:       "Two tribal8 Plague patients select industrial medicine, receive tending and bed rest, and reach immunity without a colonist death.",
		Start:       cases.Fixture{On: cases.Save{Name: "RimGovernor-tribal8-baseline"}, Op: "test/medical_plague_prepare", Args: map[string]any{"survival": true}},
		RequiredOps: []string{"test/medical_disease_stock"},
		Serve:       &cases.ServeSpec{Families: []string{"medical", "tend", "rescue"}, Prefix: "disease"},
		Budget:      5 * time.Minute, Stall: 45 * time.Second,
		// The proof spans initial stock, tier selection and subsequent immunity.
		// A resumed save cannot reproduce the first two observations.
		NoCheckpoint: true,
		Run:          disease,
	})
}

type diseasePatient struct {
	Tier, Tended, Rested, Immune, BedRestEnabled bool
	Immunity                                     float64
}

func disease(ctx context.Context, s cases.Session) error {
	patients := map[string]*diseasePatient{}
	for _, raw := range na.AsSlice(s.Prepared()["patients"]) {
		id := na.AsString(raw)
		if id == "" {
			return fmt.Errorf("missing fixture patient identity")
		}
		patients[id] = &diseasePatient{}
	}
	if len(patients) != 2 {
		return fmt.Errorf("expected two distinct Plague patients")
	}
	s.Report()["patients"] = patients
	stock, err := s.Harness().Call(ctx, "initial-stock", "test/medical_disease_stock", nil)
	if err != nil {
		return err
	}
	s.Report()["initial_stock"] = stock
	if na.AsNumber(stock["herbal"]) != 5 || na.AsNumber(stock["industrial"]) != 5 {
		return fmt.Errorf("expected exactly 5 herbal + 5 industrial: %v", stock)
	}
	before, err := diseaseRead(ctx, s, "initial-patients")
	if err != nil {
		return err
	}
	if len(na.AsSlice(before["pawns"])) != 8 {
		return fmt.Errorf("expected tribal8 roster")
	}
	if err = diseaseObserve(before, patients); err != nil {
		return err
	}
	for id, p := range patients {
		if math.Abs(p.Immunity-.1) > .00001 || p.Immune || p.Tended || p.Tier || p.BedRestEnabled {
			return fmt.Errorf("unexpected initial disease state for %s: %+v", id, p)
		}
		p.Rested = false
	}
	// First establish the tier while industrial medicine is still in stock.
	// Then resume the same colony to observe the actual native immunity race.
	for phase := 0; phase < 2; phase++ {
		spec := s.Spec()
		spec.Prefix = fmt.Sprintf("disease-%d", phase)
		service, err := s.Serve(ctx, spec)
		if err != nil {
			return err
		}
		tail := na.NewFlightTail(service.FlightPath)
		if _, err = service.Acquire(); err != nil {
			service.Stop()
			return err
		}
		service.KeepAuthority(ctx)
		err = na.WaitProgress(ctx, na.Wait{Ceiling: 4 * time.Minute, Stall: 45 * time.Second, Interval: 100 * time.Millisecond, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
			rows, err := tail.Next()
			if err != nil {
				return "", false, err
			}
			for _, event := range rows {
				if event.Kind != "native_response" || na.AsString(event.Payload["native_tool"]) != "rimgovernor/observations_list_pawns" {
					continue
				}
				result, _ := na.AsMap(event.Payload["result"])
				var reply map[string]any
				if json.Unmarshal([]byte(na.AsString(result["payload"])), &reply) != nil {
					continue
				}
				if _, err := na.PawnRow(reply, s.Identity(), ""); err != nil {
					continue
				}
				_, observed, _ := na.Outcome(reply, "observed")
				if err := diseaseObserve(observed, patients); err != nil {
					return "", false, err
				}
			}
			done := true
			for _, p := range patients {
				done = done && p.Tier
				if phase == 1 {
					done = done && p.Tended && p.BedRestEnabled && p.Rested && p.Immune
				}
			}
			encoded, _ := json.Marshal(patients)
			return string(encoded), done, nil
		})
		service.Stop()
		if err != nil {
			return err
		}
		if _, err = s.Reattach(ctx); err != nil {
			return err
		}
		if phase == 0 {
			current, readErr := diseaseRead(ctx, s, "industrial-care")
			if readErr != nil {
				return readErr
			}
			selected := map[string]*diseasePatient{}
			for id := range patients {
				selected[id] = &diseasePatient{}
			}
			if err = diseaseObserve(current, selected); err != nil {
				return err
			}
			for id, p := range selected {
				if !p.Tier {
					return fmt.Errorf("%s did not retain NormalOrWorse", id)
				}
			}
			s.Report()["industrial_care"] = current
			stock, err = s.Harness().Call(ctx, "tier-stock", "test/medical_disease_stock", nil)
			if err != nil {
				return err
			}
			s.Report()["tier_stock"] = stock
			if na.AsNumber(stock["industrial"]) <= 0 {
				return fmt.Errorf("industrial stock exhausted before tier assertion")
			}
		}
	}
	after, err := diseaseRead(ctx, s, "immune-survivors")
	if err != nil {
		return err
	}
	s.Report()["survivors"] = after
	if err = diseaseObserve(after, patients); err != nil {
		return err
	}
	beforeIDs := map[string]bool{}
	for _, raw := range na.AsSlice(before["pawns"]) {
		row, _ := na.AsMap(raw)
		ref, _ := na.AsMap(row["pawn"])
		beforeIDs[na.AsString(ref["id"])] = true
	}
	for _, raw := range na.AsSlice(after["pawns"]) {
		row, _ := na.AsMap(raw)
		ref, _ := na.AsMap(row["pawn"])
		if row["dead"] == false {
			delete(beforeIDs, na.AsString(ref["id"]))
		}
	}
	if len(beforeIDs) != 0 {
		return fmt.Errorf("dead or missing original colonists: %v", beforeIDs)
	}
	return nil
}

func diseaseRead(ctx context.Context, s cases.Session, label string) (map[string]any, error) {
	reply, err := s.Harness().Wire(ctx, label, "observations_list_pawns", map[string]any{
		"scope":   map[string]any{"expectedIdentity": s.Identity()},
		"filter":  map[string]any{"colonist": true, "includeDead": true},
		"details": map[string]any{"health": true, "settings": true}, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	return observed, err
}

// Missing/partial health never proves immunity. Require an explicit native
// immunity of one before disease removal can count as recovery.
func diseaseObserve(observed map[string]any, patients map[string]*diseasePatient) error {
	for _, raw := range na.AsSlice(observed["pawns"]) {
		row, _ := na.AsMap(raw)
		ref, _ := na.AsMap(row["pawn"])
		id := na.AsString(ref["id"])
		if row["dead"] == true && (row["colonist"] == true || patients[id] != nil) {
			return fmt.Errorf("colonist %s died", id)
		}
		p := patients[id]
		if p == nil || row["dead"] != false {
			continue
		}
		settings, _ := na.AsMap(row["settings"])
		p.Tier = p.Tier || na.AsString(settings["medicalCare"]) == "NormalOrWorse"
		for _, rawWork := range na.AsSlice(settings["work"]) {
			work, _ := na.AsMap(rawWork)
			if na.AsString(work["defName"]) == "PatientBedRest" && na.AsNumber(work["priority"]) > 0 {
				p.BedRestEnabled = true
			}
		}
		health, _ := na.AsMap(row["health"])
		for _, raw := range na.AsSlice(health["hediffs"]) {
			condition, _ := na.AsMap(raw)
			def, _ := na.AsMap(condition["definition"])
			if na.AsString(def["defName"]) != "Plague" {
				continue
			}
			if immunity, ok := condition["immunity"].(float64); ok {
				p.Immunity = immunity
				p.Immune = p.Immune || immunity >= 1
			}
			p.Tended = p.Tended || condition["tended"] == true
			p.Rested = p.Rested || row["inBed"] == true
		}
	}
	return nil
}
