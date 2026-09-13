package store

import "github.com/davidarcher/RimGovernor/go/internal/store/melee"

// MeleeAdmission binds the exact pawn pair and prerequisite draft at dispatch.
// Snapshot tokens and the claim are evidence, never a persisted lease.
type MeleeAdmission = melee.Admission
