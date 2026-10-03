package bridge

import "math/bits"

// Changed reports how t differs from prev: each row of t that is new or
// holds a different value (pointer rows compare by identity) to changed,
// each id prev held that t lacks to removed. Subtrees the two versions
// share are skipped, so the cost follows the rows that changed between
// them, not the table's size.
func (t Table[V]) Changed(prev Table[V], changed func(id string, row V), removed func(id string)) {
	diffNodes(t.root, prev.root, 0, changed, removed)
}

func diffNodes[V any](a, b *tnode[V], shift uint, changed func(string, V), removed func(string)) {
	if a == b {
		return
	}
	if a == nil || b == nil || shift >= 64 {
		diffLeaves(leavesOf(a), leavesOf(b), changed, removed)
		return
	}
	for bit := uint32(1); ; bit <<= 1 {
		inA, inB := a.bitmap&bit != 0, b.bitmap&bit != 0
		var sa, sb tslot[V]
		if inA {
			sa = a.slots[bits.OnesCount32(a.bitmap&(bit-1))]
		}
		if inB {
			sb = b.slots[bits.OnesCount32(b.bitmap&(bit-1))]
		}
		switch {
		case inA && inB && sa.child != nil && sb.child != nil:
			diffNodes(sa.child, sb.child, shift+tableBits, changed, removed)
		case inA || inB:
			diffLeaves(slotLeaves(sa, inA), slotLeaves(sb, inB), changed, removed)
		}
		if bit == 1<<31 {
			return
		}
	}
}

type tleaf[V any] struct {
	key string
	val V
}

func leavesOf[V any](n *tnode[V]) []tleaf[V] {
	var out []tleaf[V]
	if n != nil {
		n.each(func(k string, v V) bool { out = append(out, tleaf[V]{k, v}); return true })
	}
	return out
}

func slotLeaves[V any](s tslot[V], present bool) []tleaf[V] {
	switch {
	case !present:
		return nil
	case s.child != nil:
		return leavesOf(s.child)
	}
	return []tleaf[V]{{s.key, s.val}}
}

func diffLeaves[V any](a, b []tleaf[V], changed func(string, V), removed func(string)) {
	old := make(map[string]V, len(b))
	for _, l := range b {
		old[l.key] = l.val
	}
	for _, l := range a {
		if prev, ok := old[l.key]; !ok || any(prev) != any(l.val) {
			changed(l.key, l.val)
		}
		delete(old, l.key)
	}
	for _, l := range b {
		if _, gone := old[l.key]; gone {
			removed(l.key)
		}
	}
}
