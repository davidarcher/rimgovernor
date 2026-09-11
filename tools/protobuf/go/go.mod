module github.com/davidarcher/RimGovernor/go/tools/protobufproof

go 1.27.1

require (
	github.com/davidarcher/RimGovernor/go/internal/wire v0.0.0
	google.golang.org/protobuf v1.36.11
)

replace github.com/davidarcher/RimGovernor/go/internal/wire => ../../../contracts/generated/protobuf/go
