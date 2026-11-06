using System.Reflection;

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
}
