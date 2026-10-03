package bridge

// Rows calls row for every row of the table, for the mirror's recording
// (facts.KeyedRows).
func (t Table[V]) Rows(row func(id string, row any)) {
	for id, v := range t.All() {
		row(id, v)
	}
}

// ChangedSince is Changed with prev and the rows untyped, for the
// mirror's recording (facts.KeyedRows): prev is a table of the same type
// or nil, which holds nothing.
func (t Table[V]) ChangedSince(prev any, changed func(id string, row any), removed func(id string)) {
	before, _ := prev.(Table[V])
	t.Changed(before, func(id string, row V) { changed(id, row) }, removed)
}
