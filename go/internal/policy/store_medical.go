package policy

// medicalOwner is the Medical department's stockpile (#2193): the hospital's
// medicine store, a 2x2 beside the medical beds, declared once and sized once.
type medicalOwner struct{}

func (medicalOwner) Department() Department { return DepartmentMedical }

func (o medicalOwner) RoomDemand(v StorageRequest) RoomDemand {
	return DeclaredDemand(v, o.Stores(v))
}

func (medicalOwner) Stores(v StorageRequest) []Store {
	if medicine, ok := v.medicineStore(); ok {
		return []Store{medicine}
	}
	return nil
}
