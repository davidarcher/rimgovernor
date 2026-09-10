# Native forecast contracts

[Documentation](../README.md) · [Controller contracts](controller-contracts.md)

Forecasts are read-only and use native definitions and rates. `native_forecasts.py`
shares them between deterministic facts and player fact inspection.

| Projection | Observed inputs | Bound |
| --- | --- | --- |
| Food and animal feed | Fed food fall, nutrition per eligible eater, food policy, safe reachability, holder and rot deadline | Demand-weighted allocation at current temperature/access; no grazing, future hauling or harvest credit. Downed pawns need assistance, which is not presumed. |
| Harvest and labor | Growing-zone crop, fertility, growth stalls, native yield, sow/harvest work and pending construction work remaining | Native work units; no pawn-hour conversion, season guarantee or completion date. Filtered construction censuses remain unknown. |
| Medical | Native total bleeding and untreated blood-loss death estimate | Current untreated estimate; no inferred disease prognosis or treatment completion. |
| Mood | Current mood, thought target and pawn-specific native break thresholds | Current risk band and target pressure; no probability or time-to-break. |
| Power | Per-network observed watts and stored watt-days | Reserve duration at current deficit; no assumed future generation or grid connectivity. Failed readings remain unavailable. |

Food stock is apportioned only among eaters permitted by native diet, policy and
safe access. Held food belongs to its observed holder. Earliest-expiry allocation
uses native rot deadlines at the current temperature. The food gate uses the
lowest colonist runway after reserving competing animal shares; unknown combined
demand cannot certify a safe runway. Future harvesting, changing temperature,
feeding jobs and food sharing are not guaranteed. Harvest ETA is an optimistic
lower bound, and crop work does not reserve future production.

Crop and construction work remain separate totals. Native recipe work/capacity
observations remain available for project planning; bill counts are not labor hours.

Strategic mood signals use each pawn's observed native minor-break threshold.
A ten-point recovery margin prevents repeated signals near that threshold; it is
controller hysteresis, not a forecast. Missing thresholds retain established risk.
Power reserve risk likewise persists across unavailable reads and serialization;
recovery requires readable current networks.

See [native forecast acceptance](../how-to/forecast-acceptance.md) for the bounded
Docker probe and the distinction between forecast validation and pawn outcomes.
