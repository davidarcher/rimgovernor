# Waste census contract

[Documentation](../../README.md) · [Plans and Hands](../architecture/plans-and-hands.md)

The waste census is a native corpse read the observation layer decodes into `WasteItem` rows
(`CorpseOf` and, once buried, the holding grave). It carries no concern of its own:
`MorgueWaiting` and the tomb census read it, and the Sanitation concerns
(the `admin/burial` Need) own the tomb and morgue.

## Read

The colony read always carries its total waste census (`WasteSnapshot`), returning every visible spawned corpse with its inner
pawn's class (colonist, stranger or animal), then every corpse a visible grave holds, with that
grave. Fogged corpses are not listed. An empty census means no visible corpses. An absent census,
a corpse reference missing from the frame's entity table, or a native corpse without its inner pawn
is a contract failure. The census scans the native corpse list and visible grave contents each frame;
it does not depend on which Needs currently read it.

Rotten things and waste store in the materials yard, a declared, native-filtered zone (`outdoor_safe_waste`): see [storage](../architecture/storage.md).
