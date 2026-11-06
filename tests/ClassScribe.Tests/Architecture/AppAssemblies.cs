using System.Reflection;
using ClassScribe.Application;
using ClassScribe.Cli;
using ClassScribe.Core;
using ClassScribe.Infrastructure;

namespace ClassScribe.Tests;

/// <summary>
/// The four assemblies ClassScribe is built as (architecture 1.1), innermost first, for the tests that look at all of them:
/// the architecture test (2.4) and the settings guard that scans every constructor for <c>IOptions&lt;T&gt;</c> (6.4).
/// </summary>
public static class AppAssemblies
{
    /// <summary>Core, Application, Infrastructure and Cli, innermost first.</summary>
    public static IReadOnlyList<Assembly> All { get; } =
        [typeof(Durations).Assembly, typeof(BuildInfo).Assembly, typeof(InfrastructureServices).Assembly, typeof(AppHost).Assembly];

    /// <summary>The path of each assembly's file in the test's output folder, innermost first.</summary>
    public static IReadOnlyList<string> Paths => All.Select(assembly => assembly.Location).ToList();

    /// <summary>
    /// The names of the assemblies that sit as files beside the four in the test's output folder: every package the solution
    /// uses (NAudio, Refit, System.CommandLine, System.Security.Cryptography.ProtectedData and the rest) and the test host's
    /// own, never the shared framework, which the runtime loads from the dotnet folder. By them the architecture test's check 1
    /// tells a package named System.* from the framework.
    /// </summary>
    public static IReadOnlySet<string> PackagesBeside()
    {
        var folder = Path.GetDirectoryName(Paths[0])!;
        var four = All.Select(assembly => assembly.GetName().Name).ToHashSet(StringComparer.Ordinal);
        return Directory.EnumerateFiles(folder, "*.dll")
            .Select(file => Path.GetFileNameWithoutExtension(file))
            .Where(name => !four.Contains(name))
            .ToHashSet(StringComparer.Ordinal);
    }
}
