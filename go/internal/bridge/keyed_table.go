package bridge

import (
	"iter"
	"math/bits"
	"slices"
)

// Table is a persistent map from row id to row (#1578): a hash-array
// mapped trie with structural sharing. Set and Delete return a new
// version in O(log n) that shares every untouched node with the old one,
// and a version is never modified, so a consumer may keep one across
// frames and read it from any goroutine. The zero value is an empty table.
type Table[V any] struct {
	root *tnode[V]
	n    int
}

const (
	tableBits = 5
	tableMask = 1<<tableBits - 1
)

type tnode[V any] struct {
	// bitmap marks the occupied branches; slots holds them in branch
	// order. Below the last hash bit a node is a collision bucket: every
	// slot a leaf, searched linearly.
	bitmap uint32
	slots  []tslot[V]
}

// tslot is a leaf (child nil) or a branch.
type tslot[V any] struct {
	child *tnode[V]
	key   string
	hash  uint64
	val   V
}

func hashKey(key string) uint64 {
	h := uint64(14695981039346656037)
	for i := 0; i < len(key); i++ {
		h ^= uint64(key[i])
		h *= 1099511628211
	}
	// Spread the low bits FNV leaves weak.
	h ^= h >> 32
	h *= 0x9e3779b97f4a7c15
	h ^= h >> 29
	return h
}

// Len is the number of rows.
func (t Table[V]) Len() int { return t.n }

// Get is the row of id.
func (t Table[V]) Get(id string) (V, bool) {
	var zero V
	n := t.root
	if n == nil {
		return zero, false
	}
	h := hashKey(id)
	for shift := uint(0); ; shift += tableBits {
		if shift >= 64 {
			for _, s := range n.slots {
				if s.key == id {
					return s.val, true
				}
			}
			return zero, false
		}
		bit := uint32(1) << ((h >> shift) & tableMask)
		if n.bitmap&bit == 0 {
			return zero, false
		}
		s := n.slots[bits.OnesCount32(n.bitmap&(bit-1))]
		if s.child == nil {
			if s.key == id {
				return s.val, true
			}
			return zero, false
		}
		n = s.child
	}
}

// At is the row of id, the zero value when absent.
func (t Table[V]) At(id string) V {
	v, _ := t.Get(id)
	return v
}

// Map is the rows as a plain map: a copy, for the readers that publish
// a whole table.
func (t Table[V]) Map() map[string]V {
	out := make(map[string]V, t.n)
	for k, v := range t.All() {
		out[k] = v
	}
	return out
}

// TableOf is the table of a map's rows.
func TableOf[V any](rows map[string]V) Table[V] {
	var t Table[V]
	for id, v := range rows {
		t = t.Set(id, v)
	}
	return t
}

// Has reports whether the table holds id.
func (t Table[V]) Has(id string) bool {
	_, ok := t.Get(id)
	return ok
}

// Set is the table with id's row replaced or added.
func (t Table[V]) Set(id string, v V) Table[V] {
	root, added := tset(t.root, 0, hashKey(id), id, v)
	if added {
		t.n++
	}
	t.root = root
	return t
}

// Delete is the table without id.
func (t Table[V]) Delete(id string) Table[V] {
	if t.root == nil {
		return t
	}
	root, removed := tdelete(t.root, 0, hashKey(id), id)
	if !removed {
		return t
	}
	t.root, t.n = root, t.n-1
	if t.n == 0 {
		t.root = nil
	}
	return t
}

// All iterates the rows in a fixed order for a given content.
func (t Table[V]) All() iter.Seq2[string, V] {
	return func(yield func(string, V) bool) {
		if t.root != nil {
			t.root.each(yield)
		}
	}
}

// Values iterates the rows in All's order.
func (t Table[V]) Values() iter.Seq[V] {
	return func(yield func(V) bool) {
		for _, v := range t.All() {
			if !yield(v) {
				return
			}
		}
	}
}

// Keys are the ids in sorted order.
func (t Table[V]) Keys() []string {
	out := make([]string, 0, t.n)
	for k := range t.All() {
		out = append(out, k)
	}
	slices.Sort(out)
	return out
}

// Sorted are the rows in id order.
func (t Table[V]) Sorted() []V {
	keys := t.Keys()
	out := make([]V, len(keys))
	for i, k := range keys {
		out[i], _ = t.Get(k)
	}
	return out
}

func (n *tnode[V]) each(yield func(string, V) bool) bool {
	for _, s := range n.slots {
		if s.child != nil {
			if !s.child.each(yield) {
				return false
			}
		} else if !yield(s.key, s.val) {
			return false
		}
	}
	return true
}

func tset[V any](n *tnode[V], shift uint, h uint64, key string, v V) (*tnode[V], bool) {
	leaf := tslot[V]{key: key, hash: h, val: v}
	if n == nil {
		return &tnode[V]{bitmap: 1 << ((h >> shift) & tableMask), slots: []tslot[V]{leaf}}, true
	}
	if shift >= 64 {
		for i, s := range n.slots {
			if s.key == key {
				return &tnode[V]{slots: replaceSlot(n.slots, i, leaf)}, false
			}
		}
		return &tnode[V]{slots: append(slices.Clone(n.slots), leaf)}, true
	}
	bit := uint32(1) << ((h >> shift) & tableMask)
	pos := bits.OnesCount32(n.bitmap & (bit - 1))
	if n.bitmap&bit == 0 {
		slots := make([]tslot[V], len(n.slots)+1)
		copy(slots, n.slots[:pos])
		slots[pos] = leaf
		copy(slots[pos+1:], n.slots[pos:])
		return &tnode[V]{bitmap: n.bitmap | bit, slots: slots}, true
	}
	s := n.slots[pos]
	switch {
	case s.child != nil:
		child, added := tset(s.child, shift+tableBits, h, key, v)
		return &tnode[V]{bitmap: n.bitmap, slots: replaceSlot(n.slots, pos, tslot[V]{child: child})}, added
	case s.key == key:
		return &tnode[V]{bitmap: n.bitmap, slots: replaceSlot(n.slots, pos, leaf)}, false
	}
	child := tmerge(shift+tableBits, s, leaf)
	return &tnode[V]{bitmap: n.bitmap, slots: replaceSlot(n.slots, pos, tslot[V]{child: child})}, true
}

// tmerge is the node holding two leaves with different keys.
func tmerge[V any](shift uint, a, b tslot[V]) *tnode[V] {
	if shift >= 64 {
		return &tnode[V]{slots: []tslot[V]{a, b}}
	}
	ia, ib := (a.hash>>shift)&tableMask, (b.hash>>shift)&tableMask
	if ia == ib {
		return &tnode[V]{bitmap: 1 << ia, slots: []tslot[V]{{child: tmerge(shift+tableBits, a, b)}}}
	}
	slots := []tslot[V]{a, b}
	if ia > ib {
		slots = []tslot[V]{b, a}
	}
	return &tnode[V]{bitmap: 1<<ia | 1<<ib, slots: slots}
}

func tdelete[V any](n *tnode[V], shift uint, h uint64, key string) (*tnode[V], bool) {
	if shift >= 64 {
		for i, s := range n.slots {
			if s.key == key {
				if len(n.slots) == 1 {
					return nil, true
				}
				return &tnode[V]{slots: slices.Delete(slices.Clone(n.slots), i, i+1)}, true
			}
		}
		return n, false
	}
	bit := uint32(1) << ((h >> shift) & tableMask)
	if n.bitmap&bit == 0 {
		return n, false
	}
	pos := bits.OnesCount32(n.bitmap & (bit - 1))
	s := n.slots[pos]
	var repl tslot[V]
	if s.child != nil {
		child, removed := tdelete(s.child, shift+tableBits, h, key)
		if !removed {
			return n, false
		}
		switch {
		case child == nil:
			return tremove(n, pos, bit), true
		case len(child.slots) == 1 && child.slots[0].child == nil:
			// A branch left with one leaf collapses into it.
			repl = child.slots[0]
		default:
			repl = tslot[V]{child: child}
		}
	} else {
		if s.key != key {
			return n, false
		}
		return tremove(n, pos, bit), true
	}
	return &tnode[V]{bitmap: n.bitmap, slots: replaceSlot(n.slots, pos, repl)}, true
}

func tremove[V any](n *tnode[V], pos int, bit uint32) *tnode[V] {
	if len(n.slots) == 1 {
		return nil
	}
	return &tnode[V]{bitmap: n.bitmap &^ bit, slots: slices.Delete(slices.Clone(n.slots), pos, pos+1)}
}

func replaceSlot[V any](slots []tslot[V], i int, s tslot[V]) []tslot[V] {
	out := slices.Clone(slots)
	out[i] = s
	return out
}
