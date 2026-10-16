# Waste census contract

[Documentation](../../README.md) · [Plans and Hands](../architecture/plans-and-hands.md)

The waste census is a native read the observation layer decodes into `WasteItem` rows
(`WasteState` exposed, relocated or buried; `CorpseOf`; `RotStage`). It carries no concern of its
own: `MorgueWaiting` and `RouteStranger` read it, and the Sanitation concerns
(the `admin/burial` Need) own the tomb and morgue.

## Read

The colony read's waste census (`WasteReply`) returns current-map item identities, native rot stage, protection reason,
position and containment.

- Grave occupants stay visible as protected `buried` bodies.
- Held possessions and fogged targets are never hauling candidates.
- Protected: forbidden items, quest-tagged objects, packed buildings, and native dissolution,
  gas-release or explosive hazards. Hazardous waste needs specialized containment; ordinary outdoor
  dumping cannot satisfy it.
- Eligibility is native's own judgment (destination separation, protection, player policy); the brain
  filters on it and never second-guesses it.

Rotten things and waste store in the materials yard, a declared, native-filtered zone (`outdoor_safe_waste`): see [storage](../architecture/storage.md).
