package medical

import (
	"context"
	"fmt"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// Surgery planner fixture cases (#1170, epic #1160). Each opens on
// test/medical_management_setup (three Medicine 20 doctors, the third
// colonist missing a leg with a SimpleProstheticLeg stocked, a sterile lit
// surgery room with a medical hospital bed) plus one condition on the
// second colonist, and serves the medical family, which carries
// MaintainSurgery and CriticalMedical.
func init() {
	setup := func(condition string) cases.Start {
		return cases.Fixture{Op: "test/medical_management_setup", On: cases.LabStart(),
			Args: map[string]any{"disease": false, "condition": condition}}
	}
	cases.Register(cases.Case{
		Name: "medical/surgery-restore",
		Scope: "Restore (#1164): a colonist missing a leg with a prosthetic in stock gets an InstallSimpleProstheticLeg " +
			"SurgeryIntent from MaintainSurgery, and native doctors install it (the part is no longer missing). " +
			"End to end: the vanilla doctor job completing the bill is no Go snapshot; the service queues it, the case then advances the game.",
		Start: setup("restore"), Service: true, Budget: 10 * time.Minute, Run: surgeryRestore,
	})
	cases.Register(cases.Case{
		Name: "medical/surgery-cataract",
		Scope: "Chronic replacement (#1165): the pawn read carries a Cataract on the eye, and with a bionic eye in stock " +
			"MaintainSurgery admits a SurgeryIntent on that eye. A native read contract: the chronic defName and part index come from vanilla hediffs.",
		Start: setup("cataract"), Service: true, Budget: 5 * time.Minute, Run: surgeryCataract,
	})
	cases.Register(cases.Case{
		Name: "medical/surgery-amputation",
		Scope: "Life-saving amputation (#1166): a hand WoundInfection losing its immunity race reads with its part index " +
			"and a RemoveBodyPart operation of kind amputate; CriticalMedical queues it and native doctors complete it. Native: the read contract and vanilla infection physics.",
		Start: setup("infectedHand"), Service: true, Budget: 10 * time.Minute, Run: surgeryAmputation,
	})
	cases.Register(cases.Case{
		Name: "medical/surgery-harvest",
		Scope: "Organ harvest (#1169): with an unrecruitable prisoner, a colonist missing a kidney and none stocked, the " +
			"population read carries the prisoner's harvest facts, MaintainSurgery harvests a kidney from the prisoner, " +
			"then installs it with InstallNaturalKidney. Native: the population read contract and the vanilla harvest yielding the kidney.",
		Start: setup("missingKidney"), Service: true, Budget: 10 * time.Minute, Run: surgeryHarvest,
	})
}

// surgeryRun hosts serve with the medical family and runs the clock.
type surgeryRun struct {
	plans func(ctx context.Context) ([]domain.Surgery, error)
	stop  func()
}

func startSurgeryRun(ctx context.Context, s cases.Session, prefix string) (*surgeryRun, error) {
	report, identity := s.Report(), s.Identity()
	service, err := s.Launch(ctx, na.ServiceLaunch{Families: []string{"medical", "work"}, Extra: na.ClockSpeedArgs()})
	if err != nil {
		return nil, err
	}
	fail := func(err error) (*surgeryRun, error) { service.Stop(); return nil, err }
	token, err := service.SessionToken()
	if err != nil {
		return fail(err)
	}
	if _, err = service.WaitAttached(identity, 90*time.Second); err != nil {
		return fail(err)
	}
	if _, err = service.Resume(prefix, identity, token, report); err != nil {
		return fail(err)
	}
	keepAlive := &na.AuthorityKeepAlive{Service: service, Prefix: prefix, Identity: identity, Token: token}
	stopKeepAlive := keepAlive.Start(ctx)
	journal, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		report["authority_reacquisitions"] = stopKeepAlive()
		return fail(err)
	}
	var once sync.Once
	return &surgeryRun{stop: func() {
		once.Do(func() {
			journal.Close()
			report["authority_reacquisitions"] = stopKeepAlive()
			service.Stop()
		})
	}, plans: func(ctx context.Context) ([]domain.Surgery, error) {
		// History, not LoadPlans: a surgery plan retires once its bill is
		// queued, before the probe may see it.
		plans, err := journal.PlanHistoryWithMethods(ctx, 256, "restore-*", "amputate-*")
		if err != nil {
			return nil, err
		}
		var out []domain.Surgery
		for _, plan := range plans {
			for _, progress := range plan.Progress {
				if surgery, ok := progress.Action().Surgery(); ok {
					out = append(out, surgery)
				}
			}
		}
		return out, nil
	}}, nil
}

// until polls the journal's surgery intents and the probe until done.
func (r *surgeryRun) until(ctx context.Context, limit time.Duration, probe func(ctx context.Context, surgeries []domain.Surgery) (string, bool, error)) error {
	waitCtx, cancel := context.WithTimeout(ctx, limit)
	defer cancel()
	return na.WaitProgress(waitCtx, na.Wait{Stall: na.StallBudget(), Interval: 2 * time.Second}, func(ctx context.Context) (string, bool, error) {
		surgeries, err := r.plans(ctx)
		if err != nil {
			return "", false, err
		}
		return probe(ctx, surgeries)
	})
}

// findSurgery is the first intent on pawn (and part, when part >= 0)
// whose recipe matches (any recipe when empty).
func findSurgery(surgeries []domain.Surgery, pawn string, part int, recipe string) (domain.Surgery, bool) {
	for _, s := range surgeries {
		if string(s.Pawn()) == pawn && (part < 0 || s.Part() == part) && (recipe == "" || s.Recipe() == recipe) {
			return s, true
		}
	}
	return domain.Surgery{}, false
}

// pawnHealth reads one pawn's health detail from list_pawns.
func pawnHealth(ctx context.Context, s cases.Session, label, pawn string) (map[string]any, error) {
	reply, err := s.Harness().Wire(ctx, label, "observations_list_pawns", map[string]any{
		"scope":   map[string]any{"expectedIdentity": s.Identity()},
		"filter":  map[string]any{"humanlike": true, "animal": false},
		"details": map[string]any{"health": true, "needs": false, "equipment": false, "biography": false, "settings": false, "social": false, "animals": false},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	for _, raw := range na.AsSlice(observed["pawns"]) {
		row, _ := na.AsMap(raw)
		ref, _ := na.AsMap(row["pawn"])
		if na.AsString(ref["id"]) == pawn {
			health, _ := na.AsMap(row["health"])
			return health, nil
		}
	}
	return nil, fmt.Errorf("%s: pawn %s not in the pawn read", label, pawn)
}

func partMissing(health map[string]any, part int) bool {
	for _, raw := range na.AsSlice(health["missingParts"]) {
		m, _ := na.AsMap(raw)
		if index, ok := m["partIndex"].(float64); ok && int(index) == part {
			return true
		}
	}
	return false
}

func hediffAt(health map[string]any, def string, part int) bool {
	for _, raw := range na.AsSlice(health["hediffs"]) {
		h, _ := na.AsMap(raw)
		d, _ := na.AsMap(h["definition"])
		index, ok := h["partIndex"].(float64)
		if na.AsString(d["defName"]) == def && ok && int(index) == part {
			return true
		}
	}
	return false
}

func operationAt(health map[string]any, recipe, kind string, part int) bool {
	for _, raw := range na.AsSlice(health["operations"]) {
		op, _ := na.AsMap(raw)
		r, _ := na.AsMap(op["recipe"])
		index, ok := op["partIndex"].(float64)
		if na.AsString(r["defName"]) == recipe && na.AsString(op["kind"]) == kind && ok && int(index) == part {
			return true
		}
	}
	return false
}

func conditionFixture(s cases.Session) (patient string, part int, err error) {
	prepared := s.Prepared()
	patient = na.AsString(prepared["conditionPatient"])
	index, ok := prepared["conditionPart"].(float64)
	if patient == "" || !ok || index < 0 {
		return "", 0, fmt.Errorf("medical_management_setup reply lacks conditionPatient/conditionPart: %#v", prepared)
	}
	return patient, int(index), nil
}

func surgeryRestore(ctx context.Context, s cases.Session) error {
	prepared, report := s.Prepared(), s.Report()
	patient := na.AsString(prepared["surgical"])
	index, ok := prepared["part"].(float64)
	if patient == "" || !ok {
		return fmt.Errorf("medical_management_setup reply lacks surgical/part: %#v", prepared)
	}
	part := int(index)
	run, err := startSurgeryRun(ctx, s, "surgery-restore")
	if err != nil {
		return err
	}
	defer run.stop()
	if err = run.until(ctx, 4*time.Minute, func(ctx context.Context, surgeries []domain.Surgery) (string, bool, error) {
		_, ok := findSurgery(surgeries, patient, part, "InstallSimpleProstheticLeg")
		return na.Signature(len(surgeries)), ok, nil
	}); err != nil {
		return err
	}
	report["restore_intent"] = true
	run.stop()
	return completeNatively(ctx, s, "surgery-restore", patient, func(health map[string]any) bool { return !partMissing(health, part) })
}

// completeNatively takes the game back from the stopped service and runs
// it at Ultrafast until the patient's health read shows the operation
// done: the native doctor job completes the bill the service queued.
// Session.Advance cannot carry it: anesthesia downs the patient, and a
// colonist_downed stop ends an advance.
func completeNatively(ctx context.Context, s cases.Session, label, patient string, done func(health map[string]any) bool) error {
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	speed := func(key, value string) error {
		_, err := h.Call(ctx, key, "rimworld/set_time_speed", map[string]any{"speed": value, "ultraSpeedBoost": false})
		return err
	}
	defer speed(label+"-pause", "Paused")
	waitCtx, cancel := context.WithTimeout(ctx, 4*time.Minute)
	defer cancel()
	var health map[string]any
	err = na.WaitProgress(waitCtx, na.Wait{Stall: na.StallBudget(), Interval: 2 * time.Second}, func(ctx context.Context) (string, bool, error) {
		if err := speed(label+"-run", "Ultrafast"); err != nil {
			return "", false, err
		}
		var err error
		if health, err = pawnHealth(ctx, s, label+"-after", patient); err != nil {
			return "", false, err
		}
		return na.Signature(health["missingParts"], health["surgeryBills"], health["hediffs"]), done(health), nil
	})
	if err != nil {
		return fmt.Errorf("%s: operation not completed: %w (missing=%#v bills=%#v)", label, err, health["missingParts"], health["surgeryBills"])
	}
	return nil
}

func surgeryCataract(ctx context.Context, s cases.Session) error {
	report := s.Report()
	patient, part, err := conditionFixture(s)
	if err != nil {
		return err
	}
	health, err := pawnHealth(ctx, s, "surgery-cataract-health", patient)
	if err != nil {
		return err
	}
	if !hediffAt(health, "Cataract", part) {
		return fmt.Errorf("no Cataract hediff read on part %d: %#v", part, health["hediffs"])
	}
	if !operationAt(health, "InstallBionicEye", "SURGERY_KIND_INSTALL", part) {
		return fmt.Errorf("no InstallBionicEye install operation on the cataract eye %d: %#v", part, health["operations"])
	}
	run, err := startSurgeryRun(ctx, s, "surgery-cataract")
	if err != nil {
		return err
	}
	defer run.stop()
	return run.until(ctx, 4*time.Minute, func(ctx context.Context, surgeries []domain.Surgery) (string, bool, error) {
		surgery, ok := findSurgery(surgeries, patient, part, "")
		if !ok {
			return na.Signature(len(surgeries)), false, nil
		}
		report["cataract_intent"] = map[string]any{"pawn": patient, "part": part, "recipe": surgery.Recipe()}
		return "", true, nil
	})
}

func surgeryAmputation(ctx context.Context, s cases.Session) error {
	report := s.Report()
	patient, part, err := conditionFixture(s)
	if err != nil {
		return err
	}
	health, err := pawnHealth(ctx, s, "surgery-amputation-health", patient)
	if err != nil {
		return err
	}
	if !hediffAt(health, "WoundInfection", part) {
		return fmt.Errorf("WoundInfection not read on its part %d: %#v", part, health["hediffs"])
	}
	if !operationAt(health, "RemoveBodyPart", "SURGERY_KIND_AMPUTATE", part) {
		return fmt.Errorf("no RemoveBodyPart amputate operation on the infected hand %d: %#v", part, health["operations"])
	}
	run, err := startSurgeryRun(ctx, s, "surgery-amputation")
	if err != nil {
		return err
	}
	defer run.stop()
	if err = run.until(ctx, 4*time.Minute, func(ctx context.Context, surgeries []domain.Surgery) (string, bool, error) {
		_, ok := findSurgery(surgeries, patient, part, "RemoveBodyPart")
		return na.Signature(len(surgeries)), ok, nil
	}); err != nil {
		return err
	}
	report["amputation_intent"] = true
	run.stop()
	return completeNatively(ctx, s, "surgery-amputation", patient, func(health map[string]any) bool {
		return partMissing(health, part) && !hediffAt(health, "WoundInfection", part)
	})
}

func surgeryHarvest(ctx context.Context, s cases.Session) error {
	report := s.Report()
	patient, part, err := conditionFixture(s)
	if err != nil {
		return err
	}
	prisoner := na.AsString(s.Prepared()["prisonerId"])
	if prisoner == "" {
		return fmt.Errorf("medical_management_setup reply lacks prisonerId: %#v", s.Prepared())
	}
	if err = harvestFacts(ctx, s, prisoner); err != nil {
		return err
	}
	run, err := startSurgeryRun(ctx, s, "surgery-harvest")
	if err != nil {
		return err
	}
	defer run.stop()
	var cut domain.Surgery
	if err = run.until(ctx, 4*time.Minute, func(ctx context.Context, surgeries []domain.Surgery) (string, bool, error) {
		var ok bool
		cut, ok = findSurgery(surgeries, prisoner, -1, "RemoveBodyPart")
		return na.Signature(len(surgeries)), ok, nil
	}); err != nil {
		return err
	}
	report["harvest_intent"] = map[string]any{"prisoner": prisoner, "part": cut.Part()}
	run.stop()
	// A surgery bill is no clock work, so the harvest completes natively,
	// then a second service sees the kidney in stock.
	if err = completeNatively(ctx, s, "surgery-harvest-cut", prisoner, func(health map[string]any) bool { return partMissing(health, cut.Part()) }); err != nil {
		return err
	}
	install, err := startSurgeryRun(ctx, s, "surgery-harvest-install")
	if err != nil {
		return err
	}
	defer install.stop()
	return install.until(ctx, 4*time.Minute, func(ctx context.Context, surgeries []domain.Surgery) (string, bool, error) {
		_, ok := findSurgery(surgeries, patient, part, "InstallNaturalKidney")
		if ok {
			report["install_intent"] = map[string]any{"pawn": patient, "part": part}
		}
		return na.Signature(len(surgeries)), ok, nil
	})
}

// harvestFacts checks the population read's #1169 fields on the prisoner:
// a harvest operation on a kidney, its faction id and goodwill change,
// and the colony's OrganUse precept (classic on a Core-only game).
func harvestFacts(ctx context.Context, s cases.Session, prisoner string) error {
	reply, err := s.Harness().Wire(ctx, "surgery-harvest-population", "observations_read_population", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	precept := na.AsString(observed["organUsePrecept"])
	for _, raw := range na.AsSlice(observed["persons"]) {
		person, _ := na.AsMap(raw)
		state, _ := na.AsMap(person["pawn"])
		ref, _ := na.AsMap(state["pawn"])
		if na.AsString(ref["id"]) != prisoner {
			continue
		}
		surgery, _ := na.AsMap(person["surgery"])
		goodwill, gk := person["harvestGoodwillChange"].(float64)
		s.Report()["harvest_facts"] = map[string]any{"precept": precept, "faction": person["factionId"], "goodwill": person["harvestGoodwillChange"]}
		harvest := false
		for _, raw := range na.AsSlice(surgery["operations"]) {
			op, _ := na.AsMap(raw)
			harvest = harvest || na.AsString(op["kind"]) == "SURGERY_KIND_HARVEST" && na.AsString(op["partDefName"]) == "Kidney"
		}
		switch {
		case !harvest:
			return fmt.Errorf("prisoner %s has no kidney harvest operation: %#v", prisoner, surgery["operations"])
		case na.AsString(person["factionId"]) == "":
			return fmt.Errorf("prisoner %s has no faction_id", prisoner)
		case !gk || goodwill > 0:
			return fmt.Errorf("prisoner %s harvest_goodwill_change absent or positive: %#v", prisoner, person["harvestGoodwillChange"])
		case precept != "OrganUse_Classic":
			return fmt.Errorf("organ_use_precept %q, want OrganUse_Classic on a Core game", precept)
		}
		return nil
	}
	return fmt.Errorf("prisoner %s not in the population read", prisoner)
}
