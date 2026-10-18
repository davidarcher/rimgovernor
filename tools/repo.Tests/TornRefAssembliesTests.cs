namespace RimGovernor.Tools.Workflow.Tests;

/// <summary>A zero-filled reference assembly in an artifacts root clears that project's obj before the build (#3535).</summary>
public sealed class TornRefAssembliesTests
{
    [Fact]
    public void ZeroFilledRefAssemblyDeletesOnlyItsProjectsObj()
    {
        var obj = Path.Combine(Path.GetTempPath(), "rg-torn-" + Guid.NewGuid().ToString("N"), "artifacts", "obj");
        try
        {
            var torn = Path.Combine(obj, "Torn", "release");
            var intact = Path.Combine(obj, "Intact", "release");
            Directory.CreateDirectory(Path.Combine(torn, "ref"));
            Directory.CreateDirectory(Path.Combine(torn, "refint"));
            Directory.CreateDirectory(Path.Combine(intact, "ref"));
            File.WriteAllBytes(Path.Combine(torn, "ref", "Torn.dll"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(torn, "refint", "Torn.dll"), [(byte)'M', (byte)'Z', 0, 0]);
            File.WriteAllBytes(Path.Combine(intact, "ref", "Intact.dll"), [(byte)'M', (byte)'Z', 0, 0]);

            var cleared = TornRefAssemblies.Clear(obj);

            Assert.Equal([Path.Combine(obj, "Torn")], cleared);
            Assert.False(Directory.Exists(Path.Combine(obj, "Torn")));
            Assert.True(File.Exists(Path.Combine(intact, "ref", "Intact.dll")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(obj))!, recursive: true);
        }
    }
}
