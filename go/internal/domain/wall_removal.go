package domain

import "errors"

// WallRemovalAction is declared here alongside its payload.
const WallRemovalAction ActionKind = "wall_removal"

// WallRemoval clears the colonist wall at one cell as one step of a staged
// stone-shell replacement bundle. It either removes the pre-existing wall the
// bundle replaces (BackupOf empty, Original its native identity captured at
// proposal time) or a temporary backup wall this same plan built to hold the
// shell open while the original is rebuilt (BackupOf names that same-plan
// Wall BuildingAction, whose cell is Cell; Original empty). It is an
// Actions/Apply intent: native resolves the wall-upgrade site from the wall
// live, and applied means the guarded demolition is designated. Work that
// needs the cell clear waits on the census (store checkDependencies).
type WallRemoval struct {
	original string
	backupOf ActionID
	cell     Cell
}

func NewWallRemoval(original string, backupOf ActionID, cell Cell) (WallRemoval, error) {
	if backupOf == "" && !validID(original) {
		return WallRemoval{}, errors.New("wall removal requires either an original native identity or a backup prerequisite")
	}
	if backupOf != "" && (original != "" || !validID(string(backupOf))) {
		return WallRemoval{}, errors.New("backup wall removal must reference its backup prerequisite alone")
	}
	if cell.X < 0 || cell.Z < 0 {
		return WallRemoval{}, errors.New("wall removal cell must be nonnegative")
	}
	return WallRemoval{original: original, backupOf: backupOf, cell: cell}, nil
}

func (w WallRemoval) Original() string   { return w.original }
func (w WallRemoval) BackupOf() ActionID { return w.backupOf }
func (w WallRemoval) Cell() Cell         { return w.cell }

func NewWallRemovalAction(id ActionID, removal WallRemoval) (Action, error) {
	if !validID(string(id)) || id == removal.backupOf {
		return Action{}, errors.New("invalid wall removal action identity or self prerequisite")
	}
	if _, err := NewWallRemoval(removal.original, removal.backupOf, removal.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: WallRemovalAction, wallRemoval: removal}, nil
}

func (a Action) WallRemoval() (WallRemoval, bool) { return a.wallRemoval, a.kind == WallRemovalAction }
