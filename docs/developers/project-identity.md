# Project identity

[Documentation](../README.md) · [Subsystem contracts](contracts/README.md)

RimGovernor uses the following names across its deployment boundary.

| Surface | Identifier |
| --- | --- |
| Product and dashboard | `RimGovernor` |
| Python distribution, package and CLI | `rimgovernor` |
| Dashboard package | `rimgovernor-dashboard` |
| Default runtime data directory | `.rimgovernor/` |
| Environment variable prefix | `RIMGOVERNOR_` |
| Mutating HTTP request header | `X-RimGovernor: 1` |
| Container label prefix | `io.rimgovernor.` |
| Colony companion package | `davidarcher.rimgovernor.native` |
| Companion assemblies | `RimGovernor.Runtime.dll`, `RimGovernor.Bridge.dll` |
| Private native mod directories | `RimGovernor` |
| Prepared baseline save | `RimGovernor-tribal8-baseline.rws` |

Deployment names are an exact contract, with no alternate product-name aliases.
Install the Python package and rebuild the dashboard, companion DLLs and container
images from the same checkout. Prepared profiles must enable the matching companion
package and use the baseline filename above. Keep upstream package IDs and namespaces
as supplied by their authors.

Save keys, controller checkpoint metadata and browser storage keys also use the
RimGovernor identity. Profiles and checkpoints created with different product
identifiers are not directly compatible. Use a fresh prepared profile and runtime
directory; retain existing saves and checkpoints separately rather than overwriting
them. Rebuilding application code does not migrate those artifacts or browser drafts.

See [setup](../players/setup.md), [native input preparation](testing/docker-inputs.md)
and [save/resume](../players/save-and-resume.md) for deployment procedures.
