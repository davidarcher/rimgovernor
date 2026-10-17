# Waste census contract

[Documentation](../../README.md) · [Plans and Hands](../architecture/plans-and-hands.md)

The waste census is a native corpse read the observation layer decodes into `WasteItem` rows
(`CorpseOf` and, once buried, the holding grave). It carries no concern of its own:
`MorgueWaiting` and the tomb census read it, and the Sanitation concerns
(the `admin/burial` Need) own the tomb and morgue.

## Read

The colony read's waste census (`WasteReply`) returns every visible spawned corpse with its inner
pawn's class (colonist, stranger or animal), then every corpse a visible grave holds, with that
grave. Fogged corpses are not listed.

Rotten things and waste store in the materials yard, a declared, native-filtered zone (`outdoor_safe_waste`): see [storage](../architecture/storage.md).
