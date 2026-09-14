package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"strconv"

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
	} else if mineAcquisition, ok := a.MineAcquisition(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,x,z) VALUES(?,?,?,'mine_acquisition',?,?,?,?)", a.ID(), plan, ordinal, mineAcquisition.Thing(), mineAcquisition.Definition(), mineAcquisition.Cell().X, mineAcquisition.Cell().Z)
	} else if supply, ok := a.SupplyAllow(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,x,z) VALUES(?,?,?,'supply_allow',?,?,?,?)", a.ID(), plan, ordinal, supply.Thing(), supply.Definition(), supply.Cell().X, supply.Cell().Z)
	} else if tend, ok := a.Tend(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target) VALUES(?,?,?,'tend',?,?)", a.ID(), plan, ordinal, tend.Doctor(), tend.Patient())
	} else if rescue, ok := a.Rescue(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target) VALUES(?,?,?,'rescue',?,?)", a.ID(), plan, ordinal, rescue.Rescuer(), rescue.Patient())
	} else if capture, ok := a.Capture(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target) VALUES(?,?,?,'capture',?,?)", a.ID(), plan, ordinal, capture.Capturer(), capture.Patient())
	} else if ranged, ok := a.RangedAttack(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,draft_action) VALUES(?,?,?,'ranged_attack',?,?,?)", a.ID(), plan, ordinal, ranged.Pawn(), ranged.Target(), ranged.DraftAction())
	} else if mv, ok := a.Movement(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,x,z,draft_action) VALUES(?,?,?,'movement',?,?,?,?)", a.ID(), plan, ordinal, mv.Pawn(), mv.Destination().X, mv.Destination().Z, mv.DraftAction())
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
	} else if surgery, ok := a.Surgery(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,definition,x) VALUES(?,?,?,'surgery',?,?,?)", a.ID(), plan, ordinal, surgery.Patient(), surgery.Recipe(), surgery.Part())
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
	} else if accept, ok := a.QuestAccept(); ok {
		var accepter sql.NullString
		if accept.AccepterPawn() != "" {
			accepter = sql.NullString{String: string(accept.AccepterPawn()), Valid: true}
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,pawn) VALUES(?,?,?,'quest_accept',?,?,?)", a.ID(), plan, ordinal, accept.Quest(), strconv.FormatInt(int64(accept.RewardChoice()), 10), accepter)
	} else if gift, ok := a.SettlementGift(); ok {
		data, encodeErr := json.Marshal(settlementGiftPayload{gift.Caravan(), gift.Settlement(), gift.Faction(), gift.CrewIDs(), gift.Silver()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,settlement_gift_payload) VALUES(?,?,?,'settlement_gift',?)", a.ID(), plan, ordinal, data)
	} else if fulfill, ok := a.QuestFulfill(); ok {
		data, encodeErr := json.Marshal(questFulfillPayload{fulfill.Quest(), fulfill.Caravan(), fulfill.CrewIDs()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,quest_fulfill_payload) VALUES(?,?,?,'quest_fulfill',?)", a.ID(), plan, ordinal, data)
	} else if removal, ok := a.WallRemoval(); ok {
		data, encodeErr := json.Marshal(wallRemovalPayload{removal.Original(), removal.BackupOf(), removal.X(), removal.Z(), removal.NX(), removal.NZ(), removal.Left(), removal.Right(), removal.Material()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,wall_removal_payload) VALUES(?,?,?,'wall_removal',?)", a.ID(), plan, ordinal, data)
	} else if productionPolicy, ok := a.ProductionPolicy(); ok {
		data, encodeErr := json.Marshal(productionPolicyPayload{productionPolicy.Floors(), productionPolicy.Stopped()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,production_policy_payload) VALUES(?,?,?,'production_policy',?)", a.ID(), plan, ordinal, data)
	} else if temperature, ok := a.BuildingTemperature(); ok {
		data, encodeErr := json.Marshal(buildingTemperaturePayload{temperature.Celsius(), temperature.BeforeToken()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,building_temperature_payload) VALUES(?,?,?,'building_temperature',?,?)", a.ID(), plan, ordinal, temperature.Thing(), data)
	} else if travel, ok := a.TravelCaravan(); ok {
		data, encodeErr := json.Marshal(travelCaravanPayload{travel.Caravan(), travel.Kind(), travel.DestinationTile()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,travel_caravan_payload) VALUES(?,?,?,'travel_caravan',?)", a.ID(), plan, ordinal, data)
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
	var work, zone, bill, caravan, settlementGiftBlob, questFulfillBlob, wallRemoval, productionPolicyBlob, buildingTemperatureBlob, travelCaravanBlob []byte
	if err := rows.Scan(&id, &kind, &def, &x, &z, &rotation, &stuff, &pawn, &target, &draftAction, &work, &zone, &bill, &caravan, &settlementGiftBlob, &questFulfillBlob, &wallRemoval, &productionPolicyBlob, &buildingTemperatureBlob, &travelCaravanBlob, &ordinal); err != nil {
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
	if kind == "settlement_gift" && !pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		var payload settlementGiftPayload
		if len(settlementGiftBlob) > 32768 || json.Unmarshal(settlementGiftBlob, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid settlement gift payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, settlementGiftBlob) {
			return domain.Action{}, 0, errors.New("noncanonical settlement gift payload")
		}
		g, err := domain.NewSettlementGift(payload.Caravan, payload.Settlement, payload.Faction, payload.CrewIDs, payload.Silver)
		if err != nil {
			return domain.Action{}, 0, err
		}
		action, err := domain.NewSettlementGiftAction(id, g)
		return action, ordinal, err
	}
	if settlementGiftBlob != nil {
		return domain.Action{}, 0, errors.New("mixed settlement gift payload")
	}
	if kind == "quest_fulfill" && !pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		var payload questFulfillPayload
		if len(questFulfillBlob) > 32768 || json.Unmarshal(questFulfillBlob, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid quest fulfill payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, questFulfillBlob) {
			return domain.Action{}, 0, errors.New("noncanonical quest fulfill payload")
		}
		f, err := domain.NewQuestFulfill(payload.Quest, payload.Caravan, payload.CrewIDs)
		if err != nil {
			return domain.Action{}, 0, err
		}
		action, err := domain.NewQuestFulfillAction(id, f)
		return action, ordinal, err
	}
	if questFulfillBlob != nil {
		return domain.Action{}, 0, errors.New("mixed quest fulfill payload")
	}
	if kind == "wall_removal" && !pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		var payload wallRemovalPayload
		if len(wallRemoval) > 32768 || json.Unmarshal(wallRemoval, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid wall removal payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, wallRemoval) {
			return domain.Action{}, 0, errors.New("noncanonical wall removal payload")
		}
		value, err := domain.NewWallRemoval(payload.Original, payload.BackupOf, payload.X, payload.Z, payload.NX, payload.NZ, payload.Left, payload.Right, payload.Material)
		if err != nil {
			return domain.Action{}, 0, err
		}
		action, err := domain.NewWallRemovalAction(id, value)
		return action, ordinal, err
	}
	if wallRemoval != nil {
		return domain.Action{}, 0, errors.New("mixed wall removal payload")
	}
	if kind == "production_policy" && !pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		var payload productionPolicyPayload
		if len(productionPolicyBlob) > 32768 || json.Unmarshal(productionPolicyBlob, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid production policy payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, productionPolicyBlob) {
			return domain.Action{}, 0, errors.New("noncanonical production policy payload")
		}
		v, err := domain.NewProductionPolicy(payload.Floors, payload.Stopped)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewProductionPolicyAction(id, v)
		return a, ordinal, err
	}
	if productionPolicyBlob != nil {
		return domain.Action{}, 0, errors.New("mixed production policy payload")
	}
	if kind == "building_temperature" && target.Valid && !pawn.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		var payload buildingTemperaturePayload
		if len(buildingTemperatureBlob) > 32768 || json.Unmarshal(buildingTemperatureBlob, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid building temperature payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, buildingTemperatureBlob) {
			return domain.Action{}, 0, errors.New("noncanonical building temperature payload")
		}
		bt, err := domain.NewBuildingTemperature(target.String, payload.Celsius, payload.Before)
		if err != nil {
			return domain.Action{}, 0, err
		}
		action, err := domain.NewBuildingTemperatureAction(id, bt)
		return action, ordinal, err
	}
	if buildingTemperatureBlob != nil {
		return domain.Action{}, 0, errors.New("mixed building temperature payload")
	}
	if kind == "travel_caravan" && !pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		var payload travelCaravanPayload
		if len(travelCaravanBlob) > 32768 || json.Unmarshal(travelCaravanBlob, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid travel caravan payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, travelCaravanBlob) {
			return domain.Action{}, 0, errors.New("noncanonical travel caravan payload")
		}
		t, err := domain.NewTravelCaravan(payload.Caravan, payload.Kind, payload.DestinationTile)
		if err != nil {
			return domain.Action{}, 0, err
		}
		action, err := domain.NewTravelCaravanAction(id, t)
		return action, ordinal, err
	}
	if travelCaravanBlob != nil {
		return domain.Action{}, 0, errors.New("mixed travel caravan payload")
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
	if kind == "mine_acquisition" && target.Valid && def.Valid && x.Valid && z.Valid && !pawn.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		s, err := domain.NewAcquisition(target.String, def.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewMineAcquisitionAction(id, s)
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
	if kind == "capture" && pawn.Valid && target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		c, err := domain.NewCapture(domain.PawnID(pawn.String), domain.PawnID(target.String))
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewCaptureAction(id, c)
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
	if kind == "movement" && pawn.Valid && !target.Valid && x.Valid && z.Valid && draftAction.Valid && !def.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		mv, err := domain.NewMovement(domain.PawnID(pawn.String), domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)}, domain.ActionID(draftAction.String))
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewMovementAction(id, mv)
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
	if kind == "surgery" && pawn.Valid && def.Valid && x.Valid && !target.Valid && !z.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= -1 && x.Int64 <= 2147483647 {
		surgery, err := domain.NewSurgery(domain.PawnID(pawn.String), def.String, int32(x.Int64))
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewSurgeryAction(id, surgery)
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
	if kind == "quest_accept" && target.Valid && def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid && !draftAction.Valid {
		rewardChoice, convErr := strconv.ParseInt(def.String, 10, 32)
		if convErr != nil {
			return domain.Action{}, 0, convErr
		}
		accepter := domain.PawnID("")
		if pawn.Valid {
			accepter = domain.PawnID(pawn.String)
		}
		accept, err := domain.NewQuestAccept(domain.QuestID(target.String), accepter, int32(rewardChoice))
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewQuestAcceptAction(id, accept)
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

type settlementGiftPayload struct {
	Caravan    domain.CaravanID
	Settlement domain.SettlementID
	Faction    domain.FactionID
	CrewIDs    []domain.PawnID
	Silver     int32
}

type questFulfillPayload struct {
	Quest   domain.QuestID
	Caravan domain.CaravanID
	CrewIDs []domain.PawnID
}

type wallRemovalPayload struct {
	Original     string
	BackupOf     domain.ActionID
	X, Z, NX, NZ int32
	Left, Right  bool
	Material     string
}
type productionPolicyPayload struct {
	Floors  []domain.ResourceFloor
	Stopped []string
}

type buildingTemperaturePayload struct {
	Celsius float64
	Before  string
}

type travelCaravanPayload struct {
	Caravan         domain.CaravanID
	Kind            domain.TravelKind
	DestinationTile int32
}
