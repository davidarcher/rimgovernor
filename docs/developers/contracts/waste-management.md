# Waste census contract

[Documentation](../../README.md) · [Plans and Hands](../architecture/plans-and-hands.md)

The waste census is a native read the Go observation layer decodes into `WasteItem` rows
(`WasteState` exposed, relocated or buried; `CorpseOf`; `RotStage`). It carries no concern of its
own: `MorgueWaiting` and `RouteStranger` read it, and the Sanitation concerns
(`MaintainBurial`, `MaintainIncineration`) own the tomb, morgue, incinerator and burn.

## Read

`Observations.ReadWaste` returns current-map item identities, native rot stage, protection reason,
position and containment.

- Grave occupants stay visible as protected `buried` bodies.
- Held possessions and fogged targets are never hauling candidates.
- Protected: forbidden items, quest-tagged objects, packed buildings, and native dissolution,
  gas-release or explosive hazards. Hazardous waste needs specialized containment; ordinary outdoor
  dumping cannot satisfy it.
- Eligibility is native's own judgment (destination separation, protection, player policy); Go
  filters on it and never second-guesses it.

The waste dump is a declared, native-filtered zone: see [spatial contracts](spatial-contracts.md).
