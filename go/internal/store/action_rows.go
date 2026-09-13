package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func insertAction(ctx context.Context, tx *sql.Tx, plan domain.PlanID, ordinal int, a domain.Action) error {
	var reserved int
	if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM clock_attempts WHERE native_action_id=?", a.ID()).Scan(&reserved); err != nil {
		return err
	}
	if reserved != 0 {
		return ErrConflict
	}
	var err error
	if b, ok := a.ProductionBill(); ok {
		data, err := json.Marshal(billPayload{b.Bench(), b.Recipe(), b.BeforeToken(), b.Mode(), b.Target()})
		if err != nil {
			return err
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,bill_payload) VALUES(?,?,?,'production_bill',?)", a.ID(), plan, ordinal, data)
		return conflict(err)
	} else if z, ok := a.ZoneCreate(); ok {
		data, encodeErr := json.Marshal(zonePayload{z.Kind(), z.Crop(), z.Preset(), z.Priority(), z.Cells(), z.Allow()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,zone_payload) VALUES(?,?,?,'zone_create',?)", a.ID(), plan, ordinal, data)
	} else if b, ok := a.Building(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,definition,x,z,rotation,stuff) VALUES(?,?,?,'building',?,?,?,?,?)", a.ID(), plan, ordinal, b.Definition(), b.Cell().X, b.Cell().Z, b.Rotation(), b.Stuff())
	} else if d, ok := a.OwnedDraft(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn) VALUES(?,?,?,'owned_draft',?)", a.ID(), plan, ordinal, d.Pawn())
	} else if m, ok := a.MeleeAttack(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,draft_action) VALUES(?,?,?,'melee_attack',?,?,?)", a.ID(), plan, ordinal, m.Pawn(), m.Target(), m.DraftAction())
	} else if work, ok := a.WorkAssignment(); ok {
		data, encodeErr := json.Marshal(workPayload{work.Manual(), work.Settings()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,work_payload) VALUES(?,?,?,'work_assignment',?,?,?)", a.ID(), plan, ordinal, work.Pawn(), work.BeforeToken(), data)
	} else if acquisition, ok := a.Acquisition(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,x,z) VALUES(?,?,?,'acquisition',?,?,?,?)", a.ID(), plan, ordinal, acquisition.Thing(), acquisition.Definition(), acquisition.Cell().X, acquisition.Cell().Z)
	} else if supply, ok := a.SupplyAllow(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,x,z) VALUES(?,?,?,'supply_allow',?,?,?,?)", a.ID(), plan, ordinal, supply.Thing(), supply.Definition(), supply.Cell().X, supply.Cell().Z)
	} else if tend, ok := a.Tend(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target) VALUES(?,?,?,'tend',?,?)", a.ID(), plan, ordinal, tend.Doctor(), tend.Patient())
	} else if rescue, ok := a.Rescue(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target) VALUES(?,?,?,'rescue',?,?)", a.ID(), plan, ordinal, rescue.Rescuer(), rescue.Patient())
	} else if ranged, ok := a.RangedAttack(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,draft_action) VALUES(?,?,?,'ranged_attack',?,?,?)", a.ID(), plan, ordinal, ranged.Pawn(), ranged.Target(), ranged.DraftAction())
	} else if haul, ok := a.Haul(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,definition,x,z) VALUES(?,?,?,'haul',?,?,?,?,?)", a.ID(), plan, ordinal, haul.Pawn(), haul.Thing(), haul.Definition(), haul.Cell().X, haul.Cell().Z)
	} else if equip, ok := a.Equip(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,definition,x,z) VALUES(?,?,?,'equip',?,?,?,?,?)", a.ID(), plan, ordinal, equip.Pawn(), equip.Thing(), equip.Definition(), equip.Cell().X, equip.Cell().Z)
	} else if replace, ok := a.GearReplace(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,definition) VALUES(?,?,?,'gear_replace',?,?,?)", a.ID(), plan, ordinal, replace.Pawn(), replace.Thing(), replace.Definition())
	} else if repair, ok := a.Repair(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,x,z) VALUES(?,?,?,'repair',?,?,?,?)", a.ID(), plan, ordinal, repair.Pawn(), repair.Structure(), repair.Cell().X, repair.Cell().Z)
	} else if departure, ok := a.CaravanDeparture(); ok {
		data, encodeErr := json.Marshal(caravanPayload{departure.Crew(), departure.Cargo(), departure.DestinationTile()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,caravan_payload) VALUES(?,?,?,'caravan_departure',?)", a.ID(), plan, ordinal, data)
	} else if clean, ok := a.Clean(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,x,z) VALUES(?,?,?,'clean',?,?,?,?)", a.ID(), plan, ordinal, clean.Pawn(), clean.Filth(), clean.Cell().X, clean.Cell().Z)
	} else if service, ok := a.RecoveryService(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,definition) VALUES(?,?,?,'recovery_service',?,?,?)", a.ID(), plan, ordinal, service.Pawn(), service.Thing(), string(service.Method()))
	} else if assign, ok := a.BedAssign(); ok {
		def := ""
		if !assign.PreviousBed().Clear() {
			def = assign.PreviousBed().ID()
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,definition) VALUES(?,?,?,'bed_assign',?,?,?)", a.ID(), plan, ordinal, assign.Pawn(), assign.Bed(), def)
	} else if coverage, ok := a.HomeCoverage(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition) VALUES(?,?,?,'home_coverage',?,?)", a.ID(), plan, ordinal, coverage.Target(), coverage.Shape())
	} else if research, ok := a.ResearchSelect(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,definition) VALUES(?,?,?,'research_select',?)", a.ID(), plan, ordinal, research.Project())
	} else if husbandry, ok := a.Husbandry(); ok {
		var trainableDef sql.NullString
		if husbandry.TrainableDef() != "" {
			trainableDef = sql.NullString{String: husbandry.TrainableDef(), Valid: true}
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,stuff) VALUES(?,?,?,'husbandry',?,?,?)", a.ID(), plan, ordinal, husbandry.Animal(), string(husbandry.Method()), trainableDef)
	} else if interaction, ok := a.PrisonerInteraction(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition) VALUES(?,?,?,'prisoner_interaction',?,?)", a.ID(), plan, ordinal, interaction.Pawn(), string(interaction.Interaction()))
	} else {
		return errors.New("unsupported persisted action")
	}
	return conflict(err)
}
func scanAction(rows *sql.Rows) (domain.Action, int, error) {
	var id domain.ActionID
	var kind string
	var ordinal int
	var def, rotation, stuff, pawn, target, draftAction sql.NullString
	var x, z sql.NullInt64
	var work, zone, bill, caravan []byte
	if err := rows.Scan(&id, &kind, &def, &x, &z, &rotation, &stuff, &pawn, &target, &draftAction, &work, &zone, &bill, &caravan, &ordinal); err != nil {
		return domain.Action{}, 0, err
	}
	if kind == "production_bill" && !pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid && work == nil && zone == nil {
		var payload billPayload
		if len(bill) > 32768 || json.Unmarshal(bill, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid bill payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, bill) {
			return domain.Action{}, 0, errors.New("noncanonical bill payload")
		}
		value, err := domain.NewProductionBill(payload.Bench, payload.Recipe, payload.Token, payload.Mode, payload.Target)
		if err != nil {
			return domain.Action{}, 0, err
		}
		action, err := domain.NewProductionBillAction(id, value)
		return action, ordinal, err
	}
	if bill != nil {
		return domain.Action{}, 0, errors.New("mixed bill payload")
	}
	if kind == "zone_create" && !pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid && work == nil {
		var payload zonePayload
		if len(zone) > 32768 || json.Unmarshal(zone, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid zone payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, zone) {
			return domain.Action{}, 0, errors.New("noncanonical zone payload")
		}
		var value domain.ZoneCreate
		var valueErr error
		switch payload.Kind {
		case domain.GrowingZone:
			value, valueErr = domain.NewZoneCreate(payload.Kind, payload.Crop, payload.Cells)
		case domain.StockpileZone:
			switch payload.Preset {
			case domain.NothingPreset:
				value, valueErr = domain.NewAllowListStockpileZone(payload.Priority, payload.Allow, payload.Cells)
			default:
				value, valueErr = domain.NewStockpileZone(payload.Preset, payload.Priority, payload.Cells)
			}
		default:
			valueErr = errors.New("unsupported zone kind")
		}
		if valueErr != nil {
			return domain.Action{}, 0, valueErr
		}
		action, err := domain.NewZoneCreateAction(id, value)
		return action, ordinal, err
	}
	if zone != nil {
		return domain.Action{}, 0, errors.New("mixed zone payload")
	}
	if kind == "work_assignment" && pawn.Valid && target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		var payload workPayload
		if len(work) > 32768 || json.Unmarshal(work, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid work payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, work) {
			return domain.Action{}, 0, errors.New("noncanonical work payload")
		}
		w, err := domain.NewWorkAssignment(domain.PawnID(pawn.String), target.String, payload.Manual, payload.Settings)
		if err != nil {
			return domain.Action{}, 0, err
		}
		action, err := domain.NewWorkAssignmentAction(id, w)
		return action, ordinal, err
	}
	if work != nil {
		return domain.Action{}, 0, errors.New("mixed work action payload")
	}
	if kind == "caravan_departure" && !pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		var payload caravanPayload
		if len(caravan) > 32768 || json.Unmarshal(caravan, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid caravan payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, caravan) {
			return domain.Action{}, 0, errors.New("noncanonical caravan payload")
		}
		c, err := domain.NewCaravanDeparture(payload.Crew, payload.Cargo, payload.DestinationTile)
		if err != nil {
			return domain.Action{}, 0, err
		}
		action, err := domain.NewCaravanDepartureAction(id, c)
		return action, ordinal, err
	}
	if caravan != nil {
		return domain.Action{}, 0, errors.New("mixed caravan payload")
	}
	if kind == "owned_draft" && pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		d, e := domain.NewOwnedDraft(domain.PawnID(pawn.String))
		if e != nil {
			return domain.Action{}, 0, e
		}
		a, e := domain.NewOwnedDraftAction(id, d)
		return a, ordinal, e
	}
	if kind == "acquisition" && target.Valid && def.Valid && x.Valid && z.Valid && !pawn.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		s, err := domain.NewAcquisition(target.String, def.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewAcquisitionAction(id, s)
		return a, ordinal, err
	}
	if kind == "supply_allow" && target.Valid && def.Valid && x.Valid && z.Valid && !pawn.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		s, err := domain.NewSupplyAllow(target.String, def.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewSupplyAllowAction(id, s)
		return a, ordinal, err
	}
	if kind == "melee_attack" && pawn.Valid && target.Valid && draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		m, err := domain.NewMeleeAttack(domain.PawnID(pawn.String), domain.PawnID(target.String), domain.ActionID(draftAction.String))
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewMeleeAttackAction(id, m)
		return a, ordinal, err
	}
	if kind == "tend" && pawn.Valid && target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		t, err := domain.NewTend(domain.PawnID(pawn.String), domain.PawnID(target.String))
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewTendAction(id, t)
		return a, ordinal, err
	}
	if kind == "rescue" && pawn.Valid && target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		r, err := domain.NewRescue(domain.PawnID(pawn.String), domain.PawnID(target.String))
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewRescueAction(id, r)
		return a, ordinal, err
	}
	if kind == "ranged_attack" && pawn.Valid && target.Valid && draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		m, err := domain.NewRangedAttack(domain.PawnID(pawn.String), domain.PawnID(target.String), domain.ActionID(draftAction.String))
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewRangedAttackAction(id, m)
		return a, ordinal, err
	}
	if kind == "haul" && pawn.Valid && target.Valid && def.Valid && x.Valid && z.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		h, err := domain.NewHaul(domain.PawnID(pawn.String), target.String, def.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewHaulAction(id, h)
		return a, ordinal, err
	}
	if kind == "equip" && pawn.Valid && target.Valid && def.Valid && x.Valid && z.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		eq, err := domain.NewEquip(domain.PawnID(pawn.String), target.String, def.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewEquipAction(id, eq)
		return a, ordinal, err
	}
	if kind == "gear_replace" && pawn.Valid && target.Valid && def.Valid && !x.Valid && !z.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid {
		g, err := domain.NewGearReplace(domain.PawnID(pawn.String), target.String, def.String)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewGearReplaceAction(id, g)
		return a, ordinal, err
	}
	if kind == "repair" && pawn.Valid && target.Valid && x.Valid && z.Valid && !def.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		rp, err := domain.NewRepair(domain.PawnID(pawn.String), target.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewRepairAction(id, rp)
		return a, ordinal, err
	}
	if kind == "clean" && pawn.Valid && target.Valid && x.Valid && z.Valid && !def.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		cl, err := domain.NewClean(domain.PawnID(pawn.String), target.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewCleanAction(id, cl)
		return a, ordinal, err
	}
	if kind == "recovery_service" && pawn.Valid && target.Valid && def.Valid && !x.Valid && !z.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid {
		rs, err := domain.NewRecoveryService(domain.PawnID(pawn.String), target.String, domain.RecoveryMethod(def.String))
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewRecoveryServiceAction(id, rs)
		return a, ordinal, err
	}
	if kind == "bed_assign" && pawn.Valid && target.Valid && def.Valid && !x.Valid && !z.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid {
		var previous domain.PreviousBed
		var err error
		if def.String == "" {
			previous = domain.ClearPreviousBed()
		} else {
			previous, err = domain.KnownPreviousBed(def.String)
			if err != nil {
				return domain.Action{}, 0, err
			}
		}
		assign, err := domain.NewBedAssign(domain.PawnID(pawn.String), target.String, previous)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewBedAssignAction(id, assign)
		return a, ordinal, err
	}
	if kind == "home_coverage" && target.Valid && def.Valid && !pawn.Valid && !x.Valid && !z.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid {
		hc, err := domain.NewHomeCoverage(target.String, def.String)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewHomeCoverageAction(id, hc)
		return a, ordinal, err
	}
	if kind == "research_select" && def.Valid && !pawn.Valid && !target.Valid && !draftAction.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid && work == nil && zone == nil && bill == nil {
		v, err := domain.NewResearchSelect(def.String)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewResearchSelectAction(id, v)
		return a, ordinal, err
	}
	if kind == "husbandry" && target.Valid && def.Valid && !pawn.Valid && !x.Valid && !z.Valid && !rotation.Valid && !draftAction.Valid {
		trainableDef := ""
		if stuff.Valid {
			trainableDef = stuff.String
		}
		h, err := domain.NewHusbandry(domain.PawnID(target.String), domain.HusbandryMethod(def.String), trainableDef)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewHusbandryAction(id, h)
		return a, ordinal, err
	}
	if kind == "prisoner_interaction" && target.Valid && def.Valid && !pawn.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid && !draftAction.Valid {
		interaction, err := domain.NewPrisonerInteraction(domain.PawnID(target.String), domain.PrisonerInteractionMode(def.String))
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewPrisonerInteractionAction(id, interaction)
		return a, ordinal, err
	}
	if kind == "building" && !pawn.Valid && !target.Valid && !draftAction.Valid && def.Valid && x.Valid && z.Valid && rotation.Valid && stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		b, e := domain.NewBuilding(def.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)}, domain.Rotation(rotation.String), stuff.String)
		if e != nil {
			return domain.Action{}, 0, e
		}
		a, e := domain.NewBuildingAction(id, b)
		return a, ordinal, e
	}
	return domain.Action{}, 0, errors.New("invalid action payload")
}

type workPayload struct {
	Manual   bool
	Settings []domain.WorkSetting
}

type zonePayload struct {
	Kind     domain.ZoneKind
	Crop     string
	Preset   domain.StockpilePreset
	Priority domain.StockpilePriority
	Cells    []domain.Cell
	Allow    []string `json:",omitempty"`
}

type billPayload struct {
	Bench, Recipe, Token string
	Mode                 domain.BillMode
	Target               int32
}

type caravanPayload struct {
	Crew            []domain.PawnID
	Cargo           []domain.CargoItem
	DestinationTile int32
}
