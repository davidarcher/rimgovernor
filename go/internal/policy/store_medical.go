package policy

// medicalOwner is the Medical department's stockpile: the hospital's
// medicine store, a 2x2 beside the medical beds, declared once and sized once.
type medicalOwner struct{}

func (medicalOwner) Department() Department { return DepartmentMedical }

func (o medicalOwner) RoomDemand(v StoreView) RoomDemand {
	return DeclaredDemand(v, o.Stores(v))
}

func (medicalOwner) Stores(v StoreView) []Store {
	if medicine, ok := v.medicineStore(); ok {
		return []Store{medicine}
	}
	return nil
}
