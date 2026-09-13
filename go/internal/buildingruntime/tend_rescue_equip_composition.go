package buildingruntime

import "github.com/davidarcher/RimGovernor/go/internal/executor"

// withTend, withRescue and withEquip preserve every optional execution
// capability base already carries (across all of Supply/Work/Acquisition/
// Zone/Bill/Haul, plus, for the later two, Tend/Rescue) while adding the new
// one. Unlike withHaul's explicit combination branches, an interface
// extracted from base by a failed type assertion is simply a nil value of
// that interface type: embedding it anonymously still lets the returned
// struct satisfy the interface (so executor.New's own type assertions see
// it), and executor.New only wires in a capability whose extracted value is
// non-nil, so an unconfigured capability stays inert either way.
func withTend(base executor.Boundary, tend executor.TendBoundary) executor.Boundary {
	s, _ := base.(executor.SupplyBoundary)
	w, _ := base.(executor.WorkBoundary)
	a, _ := base.(executor.AcquisitionBoundary)
	z, _ := base.(executor.ZoneBoundary)
	b, _ := base.(executor.BillBoundary)
	h, _ := base.(executor.HaulBoundary)
	return &struct {
		executor.Boundary
		executor.TendBoundary
		executor.SupplyBoundary
		executor.WorkBoundary
		executor.AcquisitionBoundary
		executor.ZoneBoundary
		executor.BillBoundary
		executor.HaulBoundary
	}{base, tend, s, w, a, z, b, h}
}

func withRescue(base executor.Boundary, rescue executor.RescueBoundary) executor.Boundary {
	s, _ := base.(executor.SupplyBoundary)
	w, _ := base.(executor.WorkBoundary)
	a, _ := base.(executor.AcquisitionBoundary)
	z, _ := base.(executor.ZoneBoundary)
	b, _ := base.(executor.BillBoundary)
	h, _ := base.(executor.HaulBoundary)
	t, _ := base.(executor.TendBoundary)
	return &struct {
		executor.Boundary
		executor.RescueBoundary
		executor.SupplyBoundary
		executor.WorkBoundary
		executor.AcquisitionBoundary
		executor.ZoneBoundary
		executor.BillBoundary
		executor.HaulBoundary
		executor.TendBoundary
	}{base, rescue, s, w, a, z, b, h, t}
}

func withEquip(base executor.Boundary, equip executor.EquipBoundary) executor.Boundary {
	s, _ := base.(executor.SupplyBoundary)
	w, _ := base.(executor.WorkBoundary)
	a, _ := base.(executor.AcquisitionBoundary)
	z, _ := base.(executor.ZoneBoundary)
	b, _ := base.(executor.BillBoundary)
	h, _ := base.(executor.HaulBoundary)
	t, _ := base.(executor.TendBoundary)
	r, _ := base.(executor.RescueBoundary)
	return &struct {
		executor.Boundary
		executor.EquipBoundary
		executor.SupplyBoundary
		executor.WorkBoundary
		executor.AcquisitionBoundary
		executor.ZoneBoundary
		executor.BillBoundary
		executor.HaulBoundary
		executor.TendBoundary
		executor.RescueBoundary
	}{base, equip, s, w, a, z, b, h, t, r}
}
