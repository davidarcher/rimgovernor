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
	if z, ok := a.ZoneCreate(); ok {
		data, encodeErr := json.Marshal(zonePayload{z.Kind(), z.Crop(), z.Cells()})
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
	var work, zone []byte
	if err := rows.Scan(&id, &kind, &def, &x, &z, &rotation, &stuff, &pawn, &target, &draftAction, &work, &zone, &ordinal); err != nil {
		return domain.Action{}, 0, err
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
		value, err := domain.NewZoneCreate(payload.Kind, payload.Crop, payload.Cells)
		if err != nil {
			return domain.Action{}, 0, err
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
	Kind  domain.ZoneKind
	Crop  string
	Cells []domain.Cell
}
