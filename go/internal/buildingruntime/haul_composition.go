package buildingruntime

import "github.com/davidarcher/RimGovernor/go/internal/executor"

// withHaul mirrors withBill/withZone: keep optional execution capabilities
// visible only when configured, across every combination that can coexist
// with haul in the production session.
func withHaul(base executor.Boundary, haul executor.HaulBoundary) executor.Boundary {
	s, hs := base.(executor.SupplyBoundary)
	w, hw := base.(executor.WorkBoundary)
	a, ha := base.(executor.AcquisitionBoundary)
	z, hz := base.(executor.ZoneBoundary)
	b, hb := base.(executor.BillBoundary)
	if !hs && !hw && !ha && !hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
		}{base, haul}
	}
	if hs && !hw && !ha && !hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
		}{base, haul, s}
	}
	if !hs && hw && !ha && !hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.WorkBoundary
		}{base, haul, w}
	}
	if hs && hw && !ha && !hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.WorkBoundary
		}{base, haul, s, w}
	}
	if !hs && !hw && ha && !hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.AcquisitionBoundary
		}{base, haul, a}
	}
	if hs && !hw && ha && !hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.AcquisitionBoundary
		}{base, haul, s, a}
	}
	if !hs && hw && ha && !hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.WorkBoundary
			executor.AcquisitionBoundary
		}{base, haul, w, a}
	}
	if hs && hw && ha && !hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.WorkBoundary
			executor.AcquisitionBoundary
		}{base, haul, s, w, a}
	}
	if !hs && !hw && !ha && hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.ZoneBoundary
		}{base, haul, z}
	}
	if hs && !hw && !ha && hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.ZoneBoundary
		}{base, haul, s, z}
	}
	if !hs && hw && !ha && hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.WorkBoundary
			executor.ZoneBoundary
		}{base, haul, w, z}
	}
	if hs && hw && !ha && hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.WorkBoundary
			executor.ZoneBoundary
		}{base, haul, s, w, z}
	}
	if !hs && !hw && ha && hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.AcquisitionBoundary
			executor.ZoneBoundary
		}{base, haul, a, z}
	}
	if hs && !hw && ha && hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.AcquisitionBoundary
			executor.ZoneBoundary
		}{base, haul, s, a, z}
	}
	if !hs && hw && ha && hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.WorkBoundary
			executor.AcquisitionBoundary
			executor.ZoneBoundary
		}{base, haul, w, a, z}
	}
	if hs && hw && ha && hz && !hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.WorkBoundary
			executor.AcquisitionBoundary
			executor.ZoneBoundary
		}{base, haul, s, w, a, z}
	}
	if !hs && !hw && !ha && !hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.BillBoundary
		}{base, haul, b}
	}
	if hs && !hw && !ha && !hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.BillBoundary
		}{base, haul, s, b}
	}
	if !hs && hw && !ha && !hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.WorkBoundary
			executor.BillBoundary
		}{base, haul, w, b}
	}
	if hs && hw && !ha && !hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.WorkBoundary
			executor.BillBoundary
		}{base, haul, s, w, b}
	}
	if !hs && !hw && ha && !hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.AcquisitionBoundary
			executor.BillBoundary
		}{base, haul, a, b}
	}
	if hs && !hw && ha && !hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.AcquisitionBoundary
			executor.BillBoundary
		}{base, haul, s, a, b}
	}
	if !hs && hw && ha && !hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.WorkBoundary
			executor.AcquisitionBoundary
			executor.BillBoundary
		}{base, haul, w, a, b}
	}
	if hs && hw && ha && !hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.WorkBoundary
			executor.AcquisitionBoundary
			executor.BillBoundary
		}{base, haul, s, w, a, b}
	}
	if !hs && !hw && !ha && hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.ZoneBoundary
			executor.BillBoundary
		}{base, haul, z, b}
	}
	if hs && !hw && !ha && hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.ZoneBoundary
			executor.BillBoundary
		}{base, haul, s, z, b}
	}
	if !hs && hw && !ha && hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.WorkBoundary
			executor.ZoneBoundary
			executor.BillBoundary
		}{base, haul, w, z, b}
	}
	if hs && hw && !ha && hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.WorkBoundary
			executor.ZoneBoundary
			executor.BillBoundary
		}{base, haul, s, w, z, b}
	}
	if !hs && !hw && ha && hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.AcquisitionBoundary
			executor.ZoneBoundary
			executor.BillBoundary
		}{base, haul, a, z, b}
	}
	if hs && !hw && ha && hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.AcquisitionBoundary
			executor.ZoneBoundary
			executor.BillBoundary
		}{base, haul, s, a, z, b}
	}
	if !hs && hw && ha && hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.WorkBoundary
			executor.AcquisitionBoundary
			executor.ZoneBoundary
			executor.BillBoundary
		}{base, haul, w, a, z, b}
	}
	if hs && hw && ha && hz && hb {
		return &struct {
			executor.Boundary
			executor.HaulBoundary
			executor.SupplyBoundary
			executor.WorkBoundary
			executor.AcquisitionBoundary
			executor.ZoneBoundary
			executor.BillBoundary
		}{base, haul, s, w, a, z, b}
	}
	return base
}
