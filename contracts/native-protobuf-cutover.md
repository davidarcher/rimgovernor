# Native Protobuf cutover (history)

The native mod once exported fixed Protobuf capabilities: each tool took one `request` ProtoJSON string through the
SDK's raw object parameter and returned one `payload` ProtoJSON string, and the tools and Go acceptance cases shared
one generated wire format (`contracts/proto`, with C# and Go bindings).

#3100 collapsed that into plain C# records. What changed and what did not:

- The wire shapes are `sealed record`s in `brain/src/RimGovernor.Brain.Contracts`
  (namespace `RimGovernor.Contracts.Wire`), serialized by source-generated `System.Text.Json`. The shape rules are in
  [schema-generation.md](schema-generation.md).
- Tool names and `/native/<tool>` HTTP routes are unchanged; the request and reply bodies are JSON with the same
  field names, enums as their proto value names, int64 as numbers and choices as `type`-discriminated objects.
- Every tool now has a typed core (`Handle<Name>(request)` returning the reply record) and a matching
  `INativeFacade` method, so the in-process brain calls the same code without serializing. See
  [Native facade](../docs/developers/architecture/native-facade.md) and
  [HTTP channel](../docs/developers/architecture/http-channel.md).
- `Brain.Wire` and the link-compile of the generated protobuf C# are gone. The `.proto` files, the Go bindings,
  `tools/protobuf` and `go/internal/protobufgen` stay only until #3102 retires the Go side; the Go acceptance
  cases that decode the fixture grid as protobuf bytes (`grid_things`, `grid_delta`) cannot run against the C# mod
  until then.
- Reserved-field, field-number and old-wire compatibility rules no longer exist: current game state is disposable.

Acceptance of identity, authority readback, placement and wire behavior through the tools remains the native
acceptance tracked in [issue #38](https://github.com/davidarcher/rimgovernor/issues/38) and the C# harness work
(#3085, #3087, #3090, #3093).
