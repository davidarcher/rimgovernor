# Supply model

Epic [#2140](https://github.com/davidarcher/rimgovernor/issues/2140). One type,
`policy.SupplyCandidate` (`go/internal/policy/candidate.go`), describes every
way the colony obtains a good: a food channel, a loot, salvage or mining
source, a bench bill, a trader. The flow ranker and every channel migration
build on it.

## SupplyCandidate

| Field | Meaning |
| --- | --- |
| `Kind`, `ID` | One kind set (`CandidateKind`) covering food and acquisition kinds; a kind both had (hunt, trade) is one value. |
| `State` | `designated` (committed, not yet delivering), `delivering` (observed), `closed`. A fact; unknown is allowed. |
| `Yields` | A set of `{Good, PerDay, StockCap, UnitValue, Headroom}`: a deer yields nutrition and leather. `PerDay` is the steady rate; `StockCap` bounds a finite source. |
| `LeadDays` | Days until the first delivery. A one-shot source is lead 0 with a stock cap and no rate. |
| `LaborPerDay`, `UpfrontCost` | Steady work of a delivering candidate; labor-equivalent ticks and resources to establish it. |
| `Risk` | `{Kind, Weight}` hazards; summed weights discount the yield. |
| `PathDistance`, `DistanceSquared`, `NeedsHaul`, `UnitsPerTrip` | Reach and haul. |
| `Terms`, `Prey` | Explanation numbers; a squad hunt's animals. |

The type carries state, never credit: which states earn credit (today a
fishing or product channel counts when opened, a hunt or forage when
delivered) is the ranker's and ledger's rule.

## Adapters

`SupplyCandidateOfFood` / `FoodChannelOfSupply` and
`SupplyCandidateOfAcquisition` / `AcquisitionCandidateOfSupply` convert
`FoodChannel` and `AcquisitionCandidate` losslessly. `PlanFood` and
`RankResourceCandidates` still consume the old types; the snapshot golden test
proves their outputs identical through the adapters on every recorded food plan
and acquisition census. An open food channel maps to `delivering`, a closed one
to `closed`; an acquisition candidate is a lead-0 one-shot whose labor is its
upfront cost, with hunt revenge risk left inside that labor.
