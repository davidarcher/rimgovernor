package domain

import "errors"

// WallRemovalAction is declared here alongside its payload.
const WallRemovalAction ActionKind = "wall_removal"

// WallRemoval clears one wall cell as one step of a staged stone-shell
// replacement bundle. It either removes the pre-existing wall the bundle
// replaces (BackupOf empty, Original its proven native identity captured at
// proposal time) or a temporary backup wall this same plan built to hold the
// shell open while the original is rebuilt (BackupOf names that same-plan
// Wall BuildingAction; Original empty). Native reachability, current occupant
// identity and safe demolition legality are established at dispatch, not
// here; Original is re-validated fresh against current facts before then.
type WallRemoval struct {
	original     string
	backupOf     ActionID
	x, z, nx, nz int32
	left, right  bool
	material     string
}

func NewWallRemoval(original string, backupOf ActionID, x, z, nx, nz int32, left, right bool, material string) (WallRemoval, error) {
	if backupOf == "" && !validID(original) {
		return WallRemoval{}, errors.New("wall removal requires either an original native identity or a backup prerequisite")
	}
	if backupOf != "" && (original != "" || !validID(string(backupOf))) {
		return WallRemoval{}, errors.New("backup wall removal must reference its backup prerequisite alone")
	}
	if x < 0 || z < 0 {
		return WallRemoval{}, errors.New("wall removal site must be nonnegative")
	}
	if !nativeText(material, true) {
		return WallRemoval{}, errors.New("invalid wall removal material")
	}
	return WallRemoval{original: original, backupOf: backupOf, x: x, z: z, nx: nx, nz: nz, left: left, right: right, material: material}, nil
}

func (w WallRemoval) Original() string   { return w.original }
func (w WallRemoval) BackupOf() ActionID { return w.backupOf }
func (w WallRemoval) X() int32           { return w.x }
func (w WallRemoval) Z() int32           { return w.z }
func (w WallRemoval) NX() int32          { return w.nx }
func (w WallRemoval) NZ() int32          { return w.nz }
func (w WallRemoval) Left() bool         { return w.left }
func (w WallRemoval) Right() bool        { return w.right }
func (w WallRemoval) Material() string   { return w.material }

func NewWallRemovalAction(id ActionID, removal WallRemoval) (Action, error) {
	if !validID(string(id)) || id == removal.backupOf {
		return Action{}, errors.New("invalid wall removal action identity or self prerequisite")
	}
	if _, err := NewWallRemoval(removal.original, removal.backupOf, removal.x, removal.z, removal.nx, removal.nz, removal.left, removal.right, removal.material); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: WallRemovalAction, wallRemoval: removal}, nil
}

func (a Action) WallRemoval() (WallRemoval, bool) { return a.wallRemoval, a.kind == WallRemovalAction }
