using RimGovernor.Host;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3308: RimWorld's per-def cross-ref and per-patch DeepProfiler stages filled the launch buffer before the bridge
/// and brain phases were recorded. The labels are the literals in the game's <c>DirectXmlCrossRefLoader</c> and
/// <c>PatchOperation</c>; the coarse ones are the load stages the waterfall exists to show.
/// </summary>
public sealed class LaunchStageFilterTests
{
    [Theory]
    [InlineData("RegisterObjectWantsCrossRef (object,string,string)")]
    [InlineData("RegisterObjectWantsCrossRef (object,string,XmlNode)")]
    [InlineData("RegisterObjectWantsCrossRef (object, FieldInfo, string)")]
    [InlineData("RegisterObjectWantsCrossRef")]
    [InlineData("RegisterListWantsCrossRef")]
    [InlineData("RegisterDictionaryWantsCrossRef")]
    [InlineData("Verse.PatchOperationReplace Worker")]
    [InlineData("Verse.PatchOperationSequence Worker")]
    [InlineData("SomeMod.PatchOperationCustom Worker")]
    [InlineData("Verse.BuildableDef -> Void <PostLoad>b__78_0()")]
    [InlineData("RimWorld.PlantProperties+<>c__DisplayClass111_0 -> Void <PostLoadSpecial>b__1()")]
    [InlineData("Verse.Sound.SubSoundDef -> Void <ResolveReferences>b__37_0()")]
    [InlineData("Verse.DamageGraphicData -> Void <ResolveReferencesSpecial>b__24_0()")]
    [InlineData("Verse.ShaderParameter+<>c__DisplayClass7_0 -> Void <LoadDataFromXmlCustom>b__0()")]
    public void PerDefAndPerPatchStagesAreSkipped(string label) =>
        Assert.True(LaunchStageFilter.IsPerDefNoise(label));

    [Theory]
    [InlineData("LoadModXML()")]
    [InlineData("ApplyPatches()")]
    [InlineData("ParseAndProcessXML()")]
    [InlineData("Resolve references.")]
    [InlineData("Error check all defs.")]
    [InlineData("Static constructor calls")]
    [InlineData("Atlas baking.")]
    [InlineData("Copy all Defs from mods to global databases.")]
    [InlineData("ExecuteToExecuteWhenFinished()")]
    [InlineData("ResolveAllWantedCrossReferences")]
    [InlineData("Verse.PlayDataLoader+<>c -> Void <DoPlayLoad>b__4_3()")]
    [InlineData("Verse.ShaderWarmup -> Void KickPrewarmOnce()")]
    [InlineData("")]
    public void CoarseLoadStagesAreKept(string label) =>
        Assert.False(LaunchStageFilter.IsPerDefNoise(label));
}
