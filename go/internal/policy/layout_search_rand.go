package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// searchRand is a splitmix64 stream seeded by a site's seed cell: the seeded
// searches (the embedder's, the regrouper's, the tiler's siting, the interior
// search) draw from it, so the same input gives the same result on any box and
// at any thread count.
type searchRand uint64

func newSearchRand(seed domain.Cell) searchRand {
	return searchRand(uint64(uint32(seed.X))<<32 | uint64(uint32(seed.Z)) ^ 0x9e3779b97f4a7c15)
}

func (r *searchRand) next() uint64 {
	*r += 0x9e3779b97f4a7c15
	z := uint64(*r)
	z = (z ^ z>>30) * 0xbf58476d1ce4e5b9
	z = (z ^ z>>27) * 0x94d049bb133111eb
	return z ^ z>>31
}

// intn is a value in 0..n-1.
func (r *searchRand) intn(n int) int { return int(r.next() % uint64(n)) }
