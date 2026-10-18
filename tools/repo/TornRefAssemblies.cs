namespace RimGovernor.Tools.Workflow;

/// <summary>
/// Clears reference assemblies that are not a PE image before a build (#3535). On D: (ReFS, Dev Drive) a build's
/// <c>ref/</c> copy of the compiler's <c>refint/</c> assembly has come out zero-filled from the header on, in every
/// build root; the compiler then fails every dependent with CS0009. Deleting that project's intermediate directory
/// makes the build regenerate it. The cause is not known: a 5,000-copy <c>File.Copy</c> loop on the same volume
/// reproduced nothing.
/// </summary>
internal static class TornRefAssemblies
{
    /// <summary>
    /// Deletes each directory directly under <paramref name="objRoot"/> holding a <c>ref/</c> or <c>refint/</c> dll
    /// whose first two bytes are not <c>MZ</c>, logging one line per directory to stderr. Returns the directories deleted.
    /// </summary>
    public static List<string> Clear(string objRoot)
    {
        var cleared = new List<string>();
        if (!Directory.Exists(objRoot))
        {
            return cleared;
        }
        foreach (var project in Directory.EnumerateDirectories(objRoot))
        {
            var torn = Directory.EnumerateFiles(project, "*.dll", SearchOption.AllDirectories)
                .FirstOrDefault(dll => Path.GetFileName(Path.GetDirectoryName(dll)) is "ref" or "refint" && !IsPe(dll));
            if (torn is null)
            {
                continue;
            }
            Console.Error.WriteLine($"RimGovernor.cmd: {torn} is not a PE image (#3535); deleting {project}");
            Directory.Delete(project, recursive: true);
            cleared.Add(project);
        }
        return cleared;
    }

    private static bool IsPe(string dll)
    {
        Span<byte> header = stackalloc byte[2];
        using var stream = File.OpenRead(dll);
        return stream.ReadAtLeast(header, 2, throwOnEndOfStream: false) == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
    }
}
