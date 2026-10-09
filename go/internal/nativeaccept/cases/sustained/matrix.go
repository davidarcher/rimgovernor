package sustained

import (
	_ "embed"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/variantgen"
)

// Manifest defines the sustained colony variants. tools/variantsavegen-* generates their
// saves; sustained/matrix-* loads them.
//
//go:embed manifests/issue-1-matrix.json
var manifestJSON []byte

// Variants is the decoded, validated manifest.
var Variants = func() []variantgen.Variant {
	variants, err := variantgen.Manifest(manifestJSON)
	if err != nil {
		panic(fmt.Sprintf("manifests/issue-1-matrix.json: %v", err))
	}
	return variants
}()

// Short is a matrix save's case-name suffix: the save name without its
// RimGovernor-matrix- prefix, sanitized.
func Short(save string) string {
	return variantgen.Sanitize(strings.TrimPrefix(save, "RimGovernor-matrix-"))
}

func init() {
	for _, v := range Variants {
		name := Short(v.Save)
		cases.Register(food("sustained/matrix-"+name, v.Save, "sustained-matrix-"+name, MatrixWindowTicks))
	}
}
