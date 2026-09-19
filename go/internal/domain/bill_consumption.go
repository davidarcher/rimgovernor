package domain

// BillConsumption preserves uncertainty independently for each maintenance
// material; recipe alternatives do not establish which ingredient was spent.
type BillConsumption struct {
	Steel      *int64
	Components *int64
}
