package policy

import "fmt"

// The laboratory template (#806): research benches in one row against the
// back wall with their interaction cells on the open floor in front. The
// benches are the piece being placed when it is a research bench (the 3x2
// simple or the 5x2 hi-tech bench, #820), else the simple bench. Nothing
// else: a lab holds no filth-producing piece, and its sterile floor is the
// flooring policy's clean tier (flooring.go), not a template piece.

func init() {
	RegisterInteriorTemplate(RoomRoleLaboratory, InteriorTemplate{Name: "laboratory", Plan: planLaboratory})
}

func planLaboratory(f InteriorFrame, piece InteriorPieceDef) ([]InteriorPiece, bool) {
	def := BenchRowDef(piece, pieceFamilyResearch, ResearchBenchDefinition)
	benches, _, _, ok := BenchRow(f, def, 1, 0, func(i int) string { return fmt.Sprintf("bench.%d", i+1) })
	for i := range benches {
		benches[i].Row = "benches"
	}
	return benches, ok
}
