using System.Reflection;

namespace AdamE.Oahu.Tests.Conformance;

/// <summary>Paths in the repository, found from the root that the build records in the test assembly.</summary>
/// <remarks>
/// The tests read the source vector file, not a copy in the output directory, because they also read
/// docs/wire-format.md and can rewrite the vector file. The root comes from the build rather than from walking up from
/// the output directory, so it is found wherever the output goes.
/// </remarks>
internal static class Repository
{
    public static string Root { get; } = FindRoot();

    public static string VectorFile => Path.Combine(Root, "tests", "conformance", "wire-format", "vectors.json");

    public static string WireFormatPage => Path.Combine(Root, "docs", "wire-format.md");

    private static string FindRoot()
    {
        var root = typeof(Repository).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "RepositoryRoot")
            ?.Value;
        if (root is null || !File.Exists(Path.Combine(root, "Oahu.slnx")))
            throw new InvalidOperationException($"The repository root \"{root}\" recorded by the build has no Oahu.slnx.");

        return root;
    }
}
