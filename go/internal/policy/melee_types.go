package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

const (
	UnsupportedThreat   Reason = "unsupported_threat"
	UnsuitableEquipment Reason = "unsuitable_equipment"
)

// MeleeDraftOwner is the owned draft claim a drafted order rides on.
type MeleeDraftOwner struct {
	Claim   domain.DraftClaimID
	Session domain.ControllerSessionID
}
