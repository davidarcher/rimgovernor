package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func insertAction(ctx context.Context, tx *sql.Tx, plan domain.PlanID, ordinal int, a domain.Action) error {
	var reserved int
	if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM clock_attempts WHERE native_action_id=?", a.ID()).Scan(&reserved); err != nil {
		return err
	}
	if reserved != 0 {
		return fmt.Errorf("%w: action %s already reserved by a clock attempt", ErrConflict, a.ID())
	}
	var err error
	if b, ok := a.ProductionBill(); ok {
		data, err := json.Marshal(billPayload{b.Bench(), b.Recipe(), b.Mode(), b.Target(), b.Ingredients(), b.Worker(), b.Replaces(), storedCorpses(b)})
		if err != nil {
			return err
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,bill_payload) VALUES(?,?,?,'production_bill',?)", a.ID(), plan, ordinal, data)
		return conflict(err)
	} else if z, ok := a.ZoneCreate(); ok {
		payload := zonePayload{Kind: z.Kind(), Crop: z.Crop(), Priority: z.Priority(), Cells: z.Cells(), ExtendZoneID: z.ExtendZoneID(), Role: z.Role()}
		if z.Kind() == domain.StockpileZone {
			f := z.Filter()
			payload.Filter = &f
		}
		data, encodeErr := json.Marshal(payload)
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,zone_payload) VALUES(?,?,?,'zone_create',?)", a.ID(), plan, ordinal, data)
	} else if b, ok := a.Building(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,definition,x,z,rotation,stuff) VALUES(?,?,?,'building',?,?,?,?,?)", a.ID(), plan, ordinal, b.Definition(), b.Cell().X, b.Cell().Z, b.Rotation(), b.Stuff())
	} else if d, ok := a.OwnedDraft(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn) VALUES(?,?,?,'owned_draft',?)", a.ID(), plan, ordinal, d.Pawn())
	} else if m, ok := a.Subdue(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,draft_action) VALUES(?,?,?,'subdue',?,?,?)", a.ID(), plan, ordinal, m.Pawn(), m.Target(), m.DraftAction())
	} else if work, ok := a.WorkAssignment(); ok {
		data, encodeErr := json.Marshal(workPayload{work.Settings(), work.HasArea(), work.AreaClear(), work.Area(), work.Schedule(), work.FoodAllow(), work.MedicalCare(), work.DrugPolicy()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,work_payload) VALUES(?,?,?,'work_assignment',?,?)", a.ID(), plan, ordinal, work.Pawn(), data)
	} else if acquisition, ok := a.Acquisition(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,x,z) VALUES(?,?,?,'acquisition',?,?,?,?)", a.ID(), plan, ordinal, acquisition.Thing(), acquisition.Definition(), acquisition.Cell().X, acquisition.Cell().Z)
	} else if withdraw, ok := a.AcquisitionWithdraw(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,x,z) VALUES(?,?,?,'acquisition_withdraw',?,?,?,?)", a.ID(), plan, ordinal, withdraw.Thing(), withdraw.Definition(), withdraw.Cell().X, withdraw.Cell().Z)
	} else if mineAcquisition, ok := a.MineAcquisition(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,x,z) VALUES(?,?,?,'mine_acquisition',?,?,?,?)", a.ID(), plan, ordinal, mineAcquisition.Thing(), mineAcquisition.Definition(), mineAcquisition.Cell().X, mineAcquisition.Cell().Z)
	} else if excavation, ok := a.Excavation(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,definition,x,z) VALUES(?,?,?,'excavation',?,?,?)", a.ID(), plan, ordinal, excavation.Definition(), excavation.Cell().X, excavation.Cell().Z)
	} else if lift, ok := a.FoundationRemoval(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,definition,x,z) VALUES(?,?,?,'foundation_removal',?,?,?)", a.ID(), plan, ordinal, lift.Definition(), lift.Cell().X, lift.Cell().Z)
	} else if cut, ok := a.Deconstruction(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,x,z) VALUES(?,?,?,'deconstruction',?,?,?,?)", a.ID(), plan, ordinal, cut.Target(), cut.Definition(), cut.Cell().X, cut.Cell().Z)
	} else if move, _, ok := a.Relocation(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,x,z,rotation) VALUES(?,?,?,?,?,?,?,?,?)", a.ID(), plan, ordinal, a.Kind(), move.Thing(), move.Definition(), move.Cell().X, move.Cell().Z, move.Rotation())
	} else if cut, ok := a.CutPlant(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,x,z) VALUES(?,?,?,'cut_plant',?,?,?,?)", a.ID(), plan, ordinal, cut.Plant(), cut.Definition(), cut.Cell().X, cut.Cell().Z)
	} else if clear, ok := a.CoverClearance(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,x,z,stuff) VALUES(?,?,?,'cover_clearance',?,?,?,?,?)", a.ID(), plan, ordinal, clear.Thing(), clear.Definition(), clear.Cell().X, clear.Cell().Z, clear.Designation())
	} else if supply, ok := a.SupplyAllow(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,x,z) VALUES(?,?,?,?,?,?,?,?)", a.ID(), plan, ordinal, a.Kind(), supply.Thing(), supply.Definition(), supply.Cell().X, supply.Cell().Z)
	} else if tend, ok := a.Tend(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target) VALUES(?,?,?,'tend',?,?)", a.ID(), plan, ordinal, tend.Doctor(), tend.Patient())
	} else if rescue, ok := a.Rescue(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target) VALUES(?,?,?,'rescue',?,?)", a.ID(), plan, ordinal, rescue.Rescuer(), rescue.Patient())
	} else if capture, ok := a.Capture(); ok {
		if capture.Arrest() {
			_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,definition) VALUES(?,?,?,'capture',?,?,?)", a.ID(), plan, ordinal, capture.Capturer(), capture.Patient(), capture.Bed())
		} else {
			_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target) VALUES(?,?,?,'capture',?,?)", a.ID(), plan, ordinal, capture.Capturer(), capture.Patient())
		}
	} else if use, ok := a.UseItem(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,definition) VALUES(?,?,?,'use_item',?,?,?)", a.ID(), plan, ordinal, use.Pawn(), use.Target(), use.Item())
	} else if strip, ok := a.Strip(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target) VALUES(?,?,?,'strip',?)", a.ID(), plan, ordinal, strip.Target())
	} else if mv, ok := a.Movement(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,x,z,draft_action) VALUES(?,?,?,'movement',?,?,?,?)", a.ID(), plan, ordinal, mv.Pawn(), mv.Destination().X, mv.Destination().Z, mv.DraftAction())
	} else if haul, ok := a.Haul(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,definition,x,z) VALUES(?,?,?,'haul',?,?,?,?,?)", a.ID(), plan, ordinal, haul.Pawn(), haul.Thing(), haul.Definition(), haul.Cell().X, haul.Cell().Z)
	} else if equip, ok := a.Equip(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,definition,x,z) VALUES(?,?,?,'equip',?,?,?,?,?)", a.ID(), plan, ordinal, equip.Pawn(), equip.Thing(), equip.Definition(), equip.Cell().X, equip.Cell().Z)
	} else if replace, ok := a.GearReplace(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,definition) VALUES(?,?,?,'gear_replace',?,?,?)", a.ID(), plan, ordinal, replace.Pawn(), replace.Thing(), replace.Definition())
	} else if open, ok := a.OpenCasket(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,x,z) VALUES(?,?,?,'open_casket',?,?,?,?)", a.ID(), plan, ordinal, open.Pawn(), open.Casket(), open.Cell().X, open.Cell().Z)
	} else if repair, ok := a.Repair(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,x,z) VALUES(?,?,?,'repair',?,?,?,?)", a.ID(), plan, ordinal, repair.Pawn(), repair.Structure(), repair.Cell().X, repair.Cell().Z)
	} else if clean, ok := a.Clean(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,x,z) VALUES(?,?,?,'clean',?,?,?,?)", a.ID(), plan, ordinal, clean.Pawn(), clean.Filth(), clean.Cell().X, clean.Cell().Z)
	} else if waste, ok := a.Waste(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,x,z) VALUES(?,?,?,'waste',?,?,?,?)", a.ID(), plan, ordinal, waste.Pawn(), waste.Target(), waste.Cell().X, waste.Cell().Z)
	} else if service, ok := a.RecoveryService(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,definition) VALUES(?,?,?,'recovery_service',?,?,?)", a.ID(), plan, ordinal, service.Pawn(), service.Thing(), string(service.Method()))
	} else if coverage, ok := a.HomeCoverage(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition) VALUES(?,?,?,'home_coverage',?,?)", a.ID(), plan, ordinal, coverage.Target(), coverage.Shape())
	} else if research, ok := a.ResearchSelect(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,definition) VALUES(?,?,?,'research_select',?)", a.ID(), plan, ordinal, research.Project())
	} else if husbandry, ok := a.Husbandry(); ok {
		// stuff carries the method argument; an empty argument (a designation,
		// or an allowed-area/master clear) is stored as NULL.
		var argument sql.NullString
		if husbandry.Argument() != "" {
			argument = sql.NullString{String: husbandry.Argument(), Valid: true}
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,stuff) VALUES(?,?,?,'husbandry',?,?,?)", a.ID(), plan, ordinal, husbandry.Animal(), string(husbandry.Method()), argument)
	} else if interaction, ok := a.PrisonerInteraction(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition) VALUES(?,?,?,'prisoner_interaction',?,?)", a.ID(), plan, ordinal, interaction.Pawn(), string(interaction.Interaction()))
	} else if accept, ok := a.QuestAccept(); ok {
		var accepter sql.NullString
		if accept.AccepterPawn() != "" {
			accepter = sql.NullString{String: string(accept.AccepterPawn()), Valid: true}
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition,pawn) VALUES(?,?,?,'quest_accept',?,?,?)", a.ID(), plan, ordinal, accept.Quest(), strconv.FormatInt(int64(accept.RewardChoice()), 10), accepter)
	} else if removal, ok := a.WallRemoval(); ok {
		data, encodeErr := json.Marshal(wallRemovalPayload{removal.Original(), removal.BackupOf(), removal.Cell().X, removal.Cell().Z})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,wall_removal_payload) VALUES(?,?,?,'wall_removal',?)", a.ID(), plan, ordinal, data)
	} else if temperature, ok := a.BuildingTemperature(); ok {
		data, encodeErr := json.Marshal(buildingTemperaturePayload{temperature.Celsius()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,building_temperature_payload) VALUES(?,?,?,'building_temperature',?,?)", a.ID(), plan, ordinal, temperature.Thing(), data)
	} else if surgery, ok := a.Surgery(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,definition,x,stuff,target) VALUES(?,?,?,'surgery',?,?,?,?,NULLIF(?,''))", a.ID(), plan, ordinal, string(surgery.Pawn()), surgery.Recipe(), surgery.Part(), strconv.FormatBool(surgery.AcknowledgeViolation()), string(surgery.Surgeon()))
	} else if refuel, ok := a.AutoRefuel(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition) VALUES(?,?,?,'auto_refuel',?,?)", a.ID(), plan, ordinal, refuel.Thing(), strconv.FormatBool(refuel.Allow()))
	} else if claim, ok := a.ClaimBuilding(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target) VALUES(?,?,?,'claim_building',?)", a.ID(), plan, ordinal, claim.Thing())
	} else if edit, ok := a.ZoneCellEdit(); ok {
		data, encodeErr := json.Marshal(zoneCellEditPayload{edit.Mode(), edit.Cells()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,zone_payload) VALUES(?,?,?,'zone_cell_edit',?,?)", a.ID(), plan, ordinal, edit.Zone(), data)
	} else if patch, ok := a.StockpilePatch(); ok {
		data, encodeErr := json.Marshal(stockpilePatchPayload{patch.TargetKind(), patch.Filter(), patch.Priority(), patch.Role()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,zone_payload) VALUES(?,?,?,'stockpile_patch',?,?)", a.ID(), plan, ordinal, patch.Target(), data)
	} else if del, ok := a.ZoneDelete(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target) VALUES(?,?,?,'zone_delete',?)", a.ID(), plan, ordinal, del.Zone())
	} else if crop, ok := a.GrowerCrop(); ok {
		// definition carries the wanted crop.
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition) VALUES(?,?,?,'grower_crop',?,?)", a.ID(), plan, ordinal, crop.Thing(), crop.Crop())
	} else if medical, ok := a.BedUse(); ok {
		// definition carries the wanted flag, or "prisoners" (#880).
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,target,definition) VALUES(?,?,?,'bed_medical',?,?)", a.ID(), plan, ordinal, medical.Thing(), bedUseDefinition(medical))
	} else if assign, ok := a.BedAssign(); ok {
		// definition carries the expected previous bed; empty means none.
		def := ""
		if !assign.PreviousBed().Clear() {
			def = assign.PreviousBed().ID()
		}
		// stuff carries "swap" for a bedroom swap (#1243).
		var swap any
		if assign.Swap() {
			swap = "swap"
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,target,definition,stuff) VALUES(?,?,?,'bed_assign',?,?,?,?)", a.ID(), plan, ordinal, assign.Pawn(), assign.Bed(), def, swap)
	} else if relief, ok := a.MoodRelief(); ok {
		data, encodeErr := json.Marshal(moodReliefPayload{relief.Need()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,pawn,mood_relief_payload) VALUES(?,?,?,'mood_relief',?,?)", a.ID(), plan, ordinal, relief.Pawn(), data)
	} else if apparel, ok := a.ApparelPolicy(); ok {
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,definition) VALUES(?,?,?,'apparel_policy',?)", a.ID(), plan, ordinal, apparel.Encoded())
	} else if dialog, ok := a.DialogAnswer(); ok {
		// x carries the window ID and z the option's list position; definition
		// is the exact observed option label.
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,definition,x,z,stuff) VALUES(?,?,?,'dialog_answer',?,?,?,?)", a.ID(), plan, ordinal, dialog.OptionLabel(), dialog.WindowID(), dialog.OptionIndex(), sql.NullString{String: dialog.LetterToken(), Valid: dialog.LetterToken() != ""})
	} else if trade, ok := a.Trade(); ok {
		data, encodeErr := json.Marshal(tradePayload{trade.Kind(), trade.Trader(), trade.Negotiator(), trade.GiftMode(), trade.Lines(), trade.AllowPawns(), trade.ExpectedDealSignature(), trade.EconomicFloors(), trade.AllowEmpty(), trade.EndKind(), trade.ReceiveQuest()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,trade_payload) VALUES(?,?,?,'trade',?)", a.ID(), plan, ordinal, data)
	} else if departure, ok := a.CaravanDeparture(); ok {
		data, encodeErr := json.Marshal(caravanPayload{departure.Crew(), departure.Cargo(), departure.DestinationTile()})
		if encodeErr != nil {
			return encodeErr
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,caravan_payload) VALUES(?,?,?,'caravan_departure',?)", a.ID(), plan, ordinal, data)
	} else if naming, ok := a.NamingConfirmation(); ok {
		// x carries the window ID; definition and stuff are the exact observed
		// faction and settlement suggestions.
		_, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,kind,definition,stuff,x) VALUES(?,?,?,'naming_confirmation',?,?,?)", a.ID(), plan, ordinal, naming.FactionName(), naming.SettlementName(), naming.WindowID())
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
	var work, zone, bill, wallRemoval, buildingTemperatureBlob, moodReliefBlob, tradeBlob, caravanBlob []byte
	if err := rows.Scan(&id, &kind, &def, &x, &z, &rotation, &stuff, &pawn, &target, &draftAction, &work, &zone, &bill, &wallRemoval, &buildingTemperatureBlob, &moodReliefBlob, &tradeBlob, &caravanBlob, &ordinal); err != nil {
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
		value, err := domain.NewProductionBill(payload.Bench, payload.Recipe, payload.Mode, payload.Target, payload.Ingredients...)
		if payload.Mode == domain.HumanButcherForever && payload.Recipe == "ButcherCorpseFlesh" && payload.Target == 0 && len(payload.Ingredients) == 0 {
			value, err = domain.NewHumanButcherBill(payload.Bench, payload.Worker)
		}
		if payload.Corpses != "" {
			if payload.Mode != domain.ButcherForever || payload.Target != 0 || len(payload.Ingredients) > 0 {
				return domain.Action{}, 0, errors.New("invalid corpse bill payload")
			}
			value, err = domain.NewCorpseBill(payload.Bench, payload.Recipe, payload.Corpses)
		}
		if payload.Mode != domain.HumanButcherForever && payload.Mode != domain.GearBatch && payload.Worker != "" {
			return domain.Action{}, 0, errors.New("worker on ordinary bill")
		}
		if err != nil {
			return domain.Action{}, 0, err
		}
		// A pinned batch (#1190: one art bill per artist) reloads pinned.
		if payload.Mode == domain.GearBatch && payload.Worker != "" {
			if value, err = value.PinWorker(payload.Worker); err != nil {
				return domain.Action{}, 0, err
			}
		}
		if payload.Replace != "" {
			value, err = value.ReplaceOwnedBill(payload.Replace)
			if err != nil {
				return domain.Action{}, 0, err
			}
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
		if payload.Kind != domain.FishingZone && payload.ExtendZoneID != "" || payload.Kind == domain.FishingZone && (payload.Crop != "" || payload.Preset != "" || payload.Priority != "" || len(payload.Allow) != 0) {
			return domain.Action{}, 0, errors.New("mixed fishing zone payload")
		}
		switch payload.Kind {
		case domain.GrowingZone:
			value, valueErr = domain.NewZoneCreate(payload.Kind, payload.Crop, payload.Cells)
		case domain.FishingZone:
			value, valueErr = domain.NewFishingZone(payload.Cells)
			if payload.ExtendZoneID != "" {
				value, valueErr = domain.NewFishingZoneExtension(payload.ExtendZoneID, payload.Cells)
			}
		case domain.StockpileZone:
			var filter domain.StockpileFilter
			filter, valueErr = legacyZoneFilter(payload)
			if valueErr == nil {
				value, valueErr = domain.NewFilteredStockpileZone(filter, payload.Priority, payload.Cells)
			}
			if valueErr == nil && payload.Role != "" {
				value, valueErr = value.WithRole(payload.Role)
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
	if (kind == "zone_cell_edit" || kind == "stockpile_patch") && target.Valid && !stuff.Valid && !def.Valid && !pawn.Valid && !x.Valid && !z.Valid && !rotation.Valid && !draftAction.Valid && work == nil && len(zone) <= 32768 {
		if kind == "zone_cell_edit" {
			var payload zoneCellEditPayload
			if json.Unmarshal(zone, &payload) != nil {
				return domain.Action{}, 0, errors.New("invalid zone cell edit payload")
			}
			edit, err := domain.NewZoneCellEdit(target.String, payload.Mode, payload.Cells)
			if err != nil {
				return domain.Action{}, 0, err
			}
			a, err := domain.NewZoneCellEditAction(id, edit)
			return a, ordinal, err
		}
		var payload stockpilePatchPayload
		if json.Unmarshal(zone, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid stockpile patch payload")
		}
		patch, err := domain.NewStockpilePatch(payload.Target, target.String, payload.Filter, payload.Priority, payload.Role)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewStockpilePatchAction(id, patch)
		return a, ordinal, err
	}
	if zone != nil {
		return domain.Action{}, 0, errors.New("mixed zone payload")
	}
	if kind == "work_assignment" && pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		var payload workPayload
		if len(work) > 32768 || json.Unmarshal(work, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid work payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, work) {
			return domain.Action{}, 0, errors.New("noncanonical work payload")
		}
		var w domain.WorkAssignment
		var err error
		if payload.DrugPolicy != "" {
			if payload.MedicalCare != "" || payload.HasArea || payload.AreaClear || payload.Area != "" || len(payload.Schedule) > 0 || len(payload.Settings) > 0 || len(payload.FoodAllow) > 0 {
				return domain.Action{}, 0, errors.New("mixed drug policy payload")
			}
			w, err = domain.NewDrugPolicyAssignment(domain.PawnID(pawn.String), payload.DrugPolicy)
		} else if payload.MedicalCare != "" {
			if payload.DrugPolicy != "" || len(payload.Settings) != 0 || payload.HasArea || payload.AreaClear || payload.Area != "" || len(payload.Schedule) != 0 || len(payload.FoodAllow) != 0 {
				return domain.Action{}, 0, errors.New("mixed medical care payload")
			}
			w, err = domain.NewMedicalCareAssignment(domain.PawnID(pawn.String), payload.MedicalCare)
		} else if len(payload.FoodAllow) > 0 {
			if payload.HasArea || len(payload.Schedule) > 0 || len(payload.Settings) > 0 {
				return domain.Action{}, 0, errors.New("mixed food payload")
			}
			w, err = domain.NewFoodAssignment(domain.PawnID(pawn.String), payload.FoodAllow)
		} else if payload.HasArea {
			if len(payload.Settings) != 0 {
				return domain.Action{}, 0, errors.New("mixed work/area payload not supported")
			}
			w, err = domain.NewAreaAssignment(domain.PawnID(pawn.String), payload.AreaClear, payload.Area)
		} else if len(payload.Schedule) != 0 {
			w, err = domain.NewScheduleAssignment(domain.PawnID(pawn.String), payload.Settings, payload.Schedule)
		} else {
			w, err = domain.NewWorkAssignment(domain.PawnID(pawn.String), payload.Settings)
		}
		if err != nil {
			return domain.Action{}, 0, err
		}
		action, err := domain.NewWorkAssignmentAction(id, w)
		return action, ordinal, err
	}
	if work != nil {
		return domain.Action{}, 0, errors.New("mixed work action payload")
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
		value, err := domain.NewWallRemoval(payload.Original, payload.BackupOf, domain.Cell{X: payload.X, Z: payload.Z})
		if err != nil {
			return domain.Action{}, 0, err
		}
		action, err := domain.NewWallRemovalAction(id, value)
		return action, ordinal, err
	}
	if wallRemoval != nil {
		return domain.Action{}, 0, errors.New("mixed wall removal payload")
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
		bt, err := domain.NewBuildingTemperature(target.String, payload.Celsius)
		if err != nil {
			return domain.Action{}, 0, err
		}
		action, err := domain.NewBuildingTemperatureAction(id, bt)
		return action, ordinal, err
	}
	if buildingTemperatureBlob != nil {
		return domain.Action{}, 0, errors.New("mixed building temperature payload")
	}
	if kind == "mood_relief" && pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		var payload moodReliefPayload
		if len(moodReliefBlob) > 32768 || json.Unmarshal(moodReliefBlob, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid mood relief payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, moodReliefBlob) {
			return domain.Action{}, 0, errors.New("noncanonical mood relief payload")
		}
		relief, err := domain.NewMoodRelief(domain.PawnID(pawn.String), payload.Need)
		if err != nil {
			return domain.Action{}, 0, err
		}
		action, err := domain.NewMoodReliefAction(id, relief)
		return action, ordinal, err
	}
	if moodReliefBlob != nil {
		return domain.Action{}, 0, errors.New("mixed mood relief payload")
	}
	if kind == "trade" && !pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		var payload tradePayload
		if len(tradeBlob) > 32768 || json.Unmarshal(tradeBlob, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid trade payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, tradeBlob) {
			return domain.Action{}, 0, errors.New("noncanonical trade payload")
		}
		var value domain.Trade
		var valueErr error
		switch payload.Kind {
		case domain.TradeOpen:
			value, valueErr = domain.NewTradeOpen(payload.Trader, payload.Negotiator, payload.GiftMode)
		case domain.TradeSetLines:
			value, valueErr = domain.NewTradeSetLines(payload.Trader, payload.Negotiator, payload.Lines, payload.AllowPawns)
		case domain.TradeAccept:
			value, valueErr = domain.NewTradeAccept(payload.Trader, payload.Negotiator, payload.ExpectedDealSignature, payload.EconomicFloors, payload.AllowEmpty, payload.ReceiveQuest)
		case domain.TradeEnd:
			value, valueErr = domain.NewTradeEnd(payload.Trader, payload.Negotiator, payload.EndKind, payload.ReceiveQuest)
		default:
			valueErr = errors.New("unsupported trade operation kind")
		}
		if valueErr != nil {
			return domain.Action{}, 0, valueErr
		}
		action, err := domain.NewTradeAction(id, value)
		return action, ordinal, err
	}
	if tradeBlob != nil {
		return domain.Action{}, 0, errors.New("mixed trade payload")
	}
	if kind == "caravan_departure" && !pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		var payload caravanPayload
		if len(caravanBlob) > 32768 || json.Unmarshal(caravanBlob, &payload) != nil {
			return domain.Action{}, 0, errors.New("invalid caravan payload")
		}
		canonical, _ := json.Marshal(payload)
		if !bytes.Equal(canonical, caravanBlob) {
			return domain.Action{}, 0, errors.New("noncanonical caravan payload")
		}
		c, err := domain.NewCaravanDeparture(payload.Crew, payload.Cargo, payload.DestinationTile)
		if err != nil {
			return domain.Action{}, 0, err
		}
		action, err := domain.NewCaravanDepartureAction(id, c)
		return action, ordinal, err
	}
	if caravanBlob != nil {
		return domain.Action{}, 0, errors.New("mixed caravan payload")
	}
	if kind == "apparel_policy" && def.Valid && !pawn.Valid && !target.Valid && !draftAction.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		var spec domain.ApparelPolicySpec
		if len(def.String) > 32768 || json.Unmarshal([]byte(def.String), &spec) != nil {
			return domain.Action{}, 0, errors.New("invalid apparel policy payload")
		}
		v, err := domain.NewApparelPolicy(spec)
		if err != nil || v.Encoded() != def.String {
			return domain.Action{}, 0, errors.New("noncanonical apparel policy")
		}
		a, err := domain.NewApparelPolicyAction(id, v)
		return a, ordinal, err
	}
	if kind == "owned_draft" && pawn.Valid && !target.Valid && !draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		d, e := domain.NewOwnedDraft(domain.PawnID(pawn.String))
		if e != nil {
			return domain.Action{}, 0, e
		}
		a, e := domain.NewOwnedDraftAction(id, d)
		return a, ordinal, e
	}
	if (kind == "acquisition" || kind == "acquisition_withdraw") && target.Valid && def.Valid && x.Valid && z.Valid && !pawn.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		s, err := domain.NewAcquisition(target.String, def.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		if kind == "acquisition_withdraw" {
			a, err := domain.NewAcquisitionWithdrawAction(id, s)
			return a, ordinal, err
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
	if kind == "excavation" && !target.Valid && def.Valid && x.Valid && z.Valid && !pawn.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		e, err := domain.NewExcavation(domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)}, def.String)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewExcavationAction(id, e)
		return a, ordinal, err
	}
	if kind == "foundation_removal" && !target.Valid && def.Valid && x.Valid && z.Valid && !pawn.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		f, err := domain.NewFoundationRemoval(def.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewFoundationRemovalAction(id, f)
		return a, ordinal, err
	}
	if kind == "deconstruction" && target.Valid && def.Valid && x.Valid && z.Valid && !pawn.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		c, err := domain.NewDeconstruction(target.String, def.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewDeconstructionAction(id, c)
		return a, ordinal, err
	}
	if kind == "cover_clearance" && target.Valid && def.Valid && x.Valid && z.Valid && !pawn.Valid && !draftAction.Valid && !rotation.Valid && stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		c, err := domain.NewCoverClearance(target.String, def.String, stuff.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewCoverClearanceAction(id, c)
		return a, ordinal, err
	}
	if (kind == "move_building" || kind == "uninstall_building") && target.Valid && def.Valid && x.Valid && z.Valid && rotation.Valid && !pawn.Valid && !draftAction.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		m, err := domain.NewMoveBuilding(target.String, def.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)}, domain.Rotation(rotation.String))
		if err != nil {
			return domain.Action{}, 0, err
		}
		if kind == "uninstall_building" {
			a, err := domain.NewUninstallBuildingAction(id, m)
			return a, ordinal, err
		}
		a, err := domain.NewMoveBuildingAction(id, m)
		return a, ordinal, err
	}
	if kind == "cut_plant" && target.Valid && def.Valid && x.Valid && z.Valid && !pawn.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		c, err := domain.NewCutPlant(target.String, def.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewCutPlantAction(id, c)
		return a, ordinal, err
	}
	if (kind == "supply_allow" || kind == "supply_forbid") && target.Valid && def.Valid && x.Valid && z.Valid && !pawn.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		s, err := domain.NewSupplyAllow(target.String, def.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		if kind == "supply_forbid" {
			s, err = domain.NewSupplyForbid(target.String, def.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
			if err != nil {
				return domain.Action{}, 0, err
			}
		}
		a, err := domain.NewSupplyAllowAction(id, s)
		return a, ordinal, err
	}
	if kind == "subdue" && pawn.Valid && target.Valid && draftAction.Valid && !def.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		m, err := domain.NewSubdue(domain.PawnID(pawn.String), domain.PawnID(target.String), domain.ActionID(draftAction.String))
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewSubdueAction(id, m)
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
	if kind == "capture" && pawn.Valid && target.Valid && !draftAction.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		c, err := domain.NewCapture(domain.PawnID(pawn.String), domain.PawnID(target.String))
		if def.Valid {
			c, err = domain.NewArrest(domain.PawnID(pawn.String), domain.PawnID(target.String), def.String)
		}
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewCaptureAction(id, c)
		return a, ordinal, err
	}
	if kind == "use_item" && pawn.Valid && target.Valid && def.Valid && !draftAction.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		u, err := domain.NewUseItem(domain.PawnID(pawn.String), def.String, domain.PawnID(target.String))
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewUseItemAction(id, u)
		return a, ordinal, err
	}
	if kind == "strip" && target.Valid && !pawn.Valid && !def.Valid && !draftAction.Valid && !x.Valid && !z.Valid && !rotation.Valid && !stuff.Valid {
		s, err := domain.NewStrip(target.String)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewStripAction(id, s)
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
	if kind == "open_casket" && pawn.Valid && target.Valid && x.Valid && z.Valid && !def.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		op, err := domain.NewOpenCasket(domain.PawnID(pawn.String), target.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewOpenCasketAction(id, op)
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
	if kind == "waste" && pawn.Valid && target.Valid && x.Valid && z.Valid && !def.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid && x.Int64 >= 0 && x.Int64 <= 2147483647 && z.Int64 >= 0 && z.Int64 <= 2147483647 {
		w, err := domain.NewWaste(domain.PawnID(pawn.String), target.String, domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewWasteAction(id, w)
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
	if kind == "home_coverage" && target.Valid && def.Valid && !pawn.Valid && !x.Valid && !z.Valid && !draftAction.Valid && !rotation.Valid && !stuff.Valid {
		hc, err := domain.NewHomeCoverage(target.String, def.String)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewHomeCoverageAction(id, hc)
		return a, ordinal, err
	}
	if kind == "dialog_answer" && def.Valid && x.Valid && z.Valid && !pawn.Valid && !target.Valid && !draftAction.Valid && !rotation.Valid && work == nil && zone == nil && bill == nil {
		if x.Int64 < 0 || x.Int64 > math.MaxInt32 || z.Int64 < 0 || z.Int64 > math.MaxInt32 {
			return domain.Action{}, 0, errors.New("dialog answer window or option out of range")
		}
		v, err := domain.NewDialogAnswer(int32(x.Int64), int32(z.Int64), def.String)
		if stuff.Valid {
			if z.Int64 != 0 {
				return domain.Action{}, 0, errors.New("invalid joiner letter option")
			}
			v, err = domain.NewJoinerLetterAnswer(int32(x.Int64), def.String, stuff.String)
		}
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewDialogAnswerAction(id, v)
		return a, ordinal, err
	}
	if kind == "naming_confirmation" && def.Valid && stuff.Valid && x.Valid && !z.Valid && !pawn.Valid && !target.Valid && !draftAction.Valid && !rotation.Valid && work == nil && zone == nil && bill == nil {
		if x.Int64 < 0 || x.Int64 > math.MaxInt32 {
			return domain.Action{}, 0, errors.New("naming confirmation window out of range")
		}
		v, err := domain.NewNamingConfirmation(int32(x.Int64), def.String, stuff.String)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewNamingConfirmationAction(id, v)
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
	if kind == "surgery" && pawn.Valid && def.Valid && x.Valid && x.Int64 >= domain.NoSurgeryPart && x.Int64 <= 2147483647 && (stuff.String == "true" || stuff.String == "false") && !z.Valid && !rotation.Valid && !draftAction.Valid {
		surgery, err := domain.NewSurgery(domain.PawnID(pawn.String), def.String, int(x.Int64), stuff.String == "true")
		if err == nil && target.Valid {
			surgery, err = surgery.WithSurgeon(domain.PawnID(target.String)) // #1253
		}
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewSurgeryAction(id, surgery)
		return a, ordinal, err
	}
	if kind == "auto_refuel" && target.Valid && def.Valid && (def.String == "true" || def.String == "false") && !stuff.Valid && !pawn.Valid && !x.Valid && !z.Valid && !rotation.Valid && !draftAction.Valid {
		refuel, err := domain.NewAutoRefuel(target.String, def.String == "true")
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewAutoRefuelAction(id, refuel)
		return a, ordinal, err
	}
	if kind == "claim_building" && target.Valid && !stuff.Valid && !def.Valid && !pawn.Valid && !x.Valid && !z.Valid && !rotation.Valid && !draftAction.Valid {
		claim, err := domain.NewClaimBuilding(target.String)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewClaimBuildingAction(id, claim)
		return a, ordinal, err
	}
	if kind == "zone_delete" && target.Valid && !stuff.Valid && !def.Valid && !pawn.Valid && !x.Valid && !z.Valid && !rotation.Valid && !draftAction.Valid {
		del, err := domain.NewZoneDelete(target.String)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewZoneDeleteAction(id, del)
		return a, ordinal, err
	}
	if kind == "grower_crop" && target.Valid && def.Valid && !stuff.Valid && !pawn.Valid && !x.Valid && !z.Valid && !rotation.Valid && !draftAction.Valid {
		crop, err := domain.NewGrowerCrop(target.String, def.String)
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewGrowerCropAction(id, crop)
		return a, ordinal, err
	}
	if kind == "bed_medical" && target.Valid && def.Valid && !stuff.Valid && !pawn.Valid && !x.Valid && !z.Valid && !rotation.Valid && !draftAction.Valid && (def.String == "true" || def.String == "false" || def.String == bedPrisonersUse || def.String == bedSlavesUse) {
		medical, err := domain.NewBedMedical(target.String, def.String == "true")
		if def.String == bedPrisonersUse {
			medical, err = domain.NewBedPrisoners(target.String)
		} else if def.String == bedSlavesUse {
			medical, err = domain.NewBedSlaves(target.String)
		}
		if err != nil {
			return domain.Action{}, 0, err
		}
		a, err := domain.NewBedUseAction(id, medical)
		return a, ordinal, err
	}
	if kind == "bed_assign" && pawn.Valid && target.Valid && def.Valid && !x.Valid && !z.Valid && !draftAction.Valid && !rotation.Valid && (!stuff.Valid || stuff.String == "swap") {
		previous := domain.ClearPreviousBed()
		if def.String != "" {
			var err error
			if previous, err = domain.KnownPreviousBed(def.String); err != nil {
				return domain.Action{}, 0, err
			}
		}
		assign, err := domain.NewBedAssign(domain.PawnID(pawn.String), target.String, previous)
		if err != nil {
			return domain.Action{}, 0, err
		}
		if stuff.Valid {
			assign = assign.AsSwap()
		}
		a, err := domain.NewBedAssignAction(id, assign)
		return a, ordinal, err
	}
	if kind == "husbandry" && target.Valid && def.Valid && !pawn.Valid && !x.Valid && !z.Valid && !rotation.Valid && !draftAction.Valid {
		argument := ""
		if stuff.Valid {
			argument = stuff.String
		}
		h, err := domain.NewHusbandry(domain.PawnID(target.String), domain.HusbandryMethod(def.String), argument)
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
	Settings  []domain.WorkSetting
	HasArea   bool   `json:",omitempty"`
	AreaClear bool   `json:",omitempty"`
	Area      string `json:",omitempty"`
	// Schedule is the 24-hour timetable a schedule write carries (#417);
	// omitted for work-only and area-only rows so older payloads stay
	// canonical.
	Schedule    []string `json:",omitempty"`
	FoodAllow   []string `json:",omitempty"`
	MedicalCare string   `json:",omitempty"`
	DrugPolicy  string   `json:",omitempty"`
}

// zonePayload is a zone_create row. Preset and Allow are only ever read:
// rows written before #932 name a stockpile's filter by preset (and an
// allow-list for "nothing"); newer rows carry Filter with Preset empty.
type zonePayload struct {
	Kind         domain.ZoneKind
	Crop         string
	Preset       string
	Priority     domain.StockpilePriority
	Cells        []domain.Cell
	Allow        []string `json:",omitempty"`
	ExtendZoneID string   `json:",omitempty"`
	// Filter is a stockpile's filter; Role a stockpile's planner role key.
	// Both are absent on older rows.
	Filter *domain.StockpileFilter `json:",omitempty"`
	Role   string                  `json:",omitempty"`
}

type zoneCellEditPayload struct {
	Mode  domain.CellEditMode
	Cells []domain.Cell
}

type stockpilePatchPayload struct {
	Target   domain.StorageTargetKind
	Filter   domain.StockpileFilter
	Priority domain.StockpilePriority
	Role     string `json:",omitempty"`
}

type billPayload struct {
	Bench, Recipe string
	Mode          domain.BillMode
	Target        int32
	Ingredients   []string        `json:",omitempty"`
	Worker        string          `json:",omitempty"`
	Replace       string          `json:",omitempty"`
	Corpses       domain.CorpseOf `json:",omitempty"`
}

// storedCorpses is the bill row's corpse filter: only a cremation bill
// records one. A butcher row implies animal (stranger with a worker), so
// rows written before #833 load unchanged.
func storedCorpses(b domain.ProductionBill) domain.CorpseOf {
	if b.Recipe() == domain.CremateRecipe {
		return b.Corpses()
	}
	return ""
}

type wallRemovalPayload struct {
	Original string
	BackupOf domain.ActionID
	X, Z     int32
}

type buildingTemperaturePayload struct {
	Celsius float64
}

type caravanPayload struct {
	Crew            []domain.PawnID
	Cargo           []domain.CargoItem
	DestinationTile int32
}

type tradePayload struct {
	Kind                  domain.TradeOperationKind
	Trader                string
	Negotiator            domain.PawnID
	GiftMode              bool
	Lines                 []domain.TradeLine
	AllowPawns            bool
	ExpectedDealSignature string
	EconomicFloors        []domain.TradeEconomicFloor
	AllowEmpty            bool
	EndKind               domain.TradeEndKind
	ReceiveQuest          bool
}
type moodReliefPayload struct {
	Need domain.MoodReliefNeed
}

// bedPrisonersUse is a bed_medical row's definition for a bed set for
// prisoners (#880); the medical rows keep "true" and "false".
const bedPrisonersUse = "prisoners"

// bedSlavesUse is a bed_medical row's definition for a bed set for slaves.
const bedSlavesUse = "slaves"

func bedUseDefinition(b domain.BedUse) string {
	if b.Prisoners() {
		return bedPrisonersUse
	}
	if b.Slaves() {
		return bedSlavesUse
	}
	return strconv.FormatBool(b.Medical())
}

// legacyZoneFilter is a stockpile row's filter: Filter on current rows, the
// retired preset's filter on rows written before #932.
func legacyZoneFilter(payload zonePayload) (domain.StockpileFilter, error) {
	if payload.Filter != nil {
		if payload.Preset != "" || len(payload.Allow) != 0 {
			return domain.StockpileFilter{}, errors.New("mixed stockpile filter payload")
		}
		return *payload.Filter, nil
	}
	if payload.Preset != "nothing" && len(payload.Allow) != 0 {
		return domain.StockpileFilter{}, errors.New("mixed stockpile allow payload")
	}
	switch payload.Preset {
	case "food":
		return domain.FoodFilter(), nil
	case "corpse_larder":
		return domain.CorpseLarderFilter(), nil
	case "general":
		return domain.GeneralFilter(), nil
	case "nothing":
		return domain.AllowOnlyFilter(payload.Allow)
	}
	return domain.StockpileFilter{}, errors.New("unsupported stockpile preset")
}
