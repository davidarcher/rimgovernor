# Source and dependency notices

## Go dependencies

Pinned versions and hashes are in `go/go.mod` and `go/go.sum`. Retained notices
in `third_party/` cover the MCP Go SDK, modernc SQLite, SQLite and sqlite-vec.
Include applicable dependency notices with distributed binaries; the Go packaging
gate remains in [G01.11](docs/BACKLOG.md).

## Dashboard

The dashboard includes source derived from IlyaChichkov/rimapi-dashboard at
`152454bbcc8ab7d2b3e6cfff797f6a1df03d36b1`. Its MIT notice is retained in
[third_party/rimapi-dashboard-LICENSE](third_party/rimapi-dashboard-LICENSE).

## Native integration and formatters

- [Colony bridge source notice](integrations/colony-bridge/PROVENANCE.md)
- [UI formatter source notice](controller/rimgovernor/vendor/PROVENANCE.md)
- [Headless adapter source notice](integrations/headless-rim/PROVENANCE.md) and GPL-3.0 license

Game files, artwork, GABS and installed SDK assemblies are supplied separately.
