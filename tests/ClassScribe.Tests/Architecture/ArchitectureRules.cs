namespace ClassScribe.Tests;

/// <summary>One place where the architecture's rules do not hold (architecture 2.4): the check's number, the assembly, the type, the member when one is named, and why.</summary>
/// <param name="Check">The check's number in the table of architecture 2.4, 1 to 7.</param>
/// <param name="Assembly">The assembly the violation is in.</param>
/// <param name="Type">The type that holds it, or the assembly's name for a check on the assembly itself.</param>
/// <param name="Member">The member named, or null.</param>
/// <param name="Reason">Why it fails, in words.</param>
public sealed record Violation(int Check, string Assembly, string Type, string? Member, string Reason)
{
    /// <summary>The violation in one line, as the test prints it.</summary>
    public override string ToString() => $"check {Check}: {Assembly}: {Type}{(Member is null ? "" : "." + Member)}: {Reason}";
}

/// <summary>
/// The rules of architecture 2.4 as a pure function over the facts of the built assemblies: which assemblies each may
/// reference (1), what Core and Application may not call (2), what Core alone may not (3; 4 names what it may), the
/// namespaces (5), one version (6) and the exe's name (7). An allow-list gives a reason for each entry, as
/// "Deliberately not rethrown" does for catches.
/// </summary>
public static class ArchitectureRules
{
    /// <summary>The Core assembly's name.</summary>
    public const string Core = "ClassScribe.Core";

    /// <summary>The Application assembly's name.</summary>
    public const string Application = "ClassScribe.Application";

    /// <summary>The Infrastructure assembly's name.</summary>
    public const string Infrastructure = "ClassScribe.Infrastructure";

    /// <summary>The Cli assembly's name: the exe keeps ClassScribe (architecture 1.3).</summary>
    public const string Cli = "ClassScribe";

    /// <summary>The test assembly's name, which none of the four may reference.</summary>
    public const string Tests = "ClassScribe.Tests";

    /// <summary>The root namespace of each of the four assemblies (architecture 1.3).</summary>
    public static IReadOnlyDictionary<string, string> RootNamespaces { get; } = new Dictionary<string, string>
    {
        [Core] = "ClassScribe.Core",
        [Application] = "ClassScribe.Application",
        [Infrastructure] = "ClassScribe.Infrastructure",
        [Cli] = "ClassScribe.Cli",
    };

    /// <summary>The packages Application may reference besides Core and the framework (check 1).</summary>
    public static IReadOnlyList<string> ApplicationPackages { get; } =
        ["Microsoft.Extensions.Logging.Abstractions", "Microsoft.Extensions.Options", "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Primitives"];

    /// <summary>The members of System.IO.Path that are string work and stay allowed in Core and Application (check 2).</summary>
    public static IReadOnlyList<string> AllowedPathMembers { get; } =
    [
        "Combine", "Join", "GetFileName", "GetFileNameWithoutExtension", "GetExtension", "ChangeExtension", "HasExtension",
        "GetDirectoryName", "GetPathRoot", "IsPathRooted", "IsPathFullyQualified", "TrimEndingDirectorySeparator",
        "EndsInDirectorySeparator", "GetInvalidFileNameChars", "GetInvalidPathChars",
        "DirectorySeparatorChar", "AltDirectorySeparatorChar", "PathSeparator", "VolumeSeparatorChar",
    ];

    /// <summary>The types of the disk that Core and Application may not use (check 2).</summary>
    private static readonly string[] DiskTypes =
    [
        "System.IO.File", "System.IO.Directory", "System.IO.FileInfo", "System.IO.DirectoryInfo", "System.IO.FileSystemInfo",
        "System.IO.FileStream", "System.IO.RandomAccess", "System.IO.DriveInfo", "System.IO.FileSystemWatcher",
    ];

    /// <summary>The namespaces, by prefix, that Core and Application may not reach (check 2): the machine, the vendors and the hosts.</summary>
    private static readonly string[] BannedPrefixes =
    [
        "Microsoft.Win32.", "System.Net.Http.", "System.Net.HttpListener", "System.Net.Sockets.", "System.IO.Pipes.",
        "NAudio.", "Windows.", "Refit.", "System.CommandLine.", "Microsoft.Extensions.Configuration.", "Microsoft.Extensions.Hosting.",
        "Microsoft.Extensions.Http.",
    ];

    /// <summary>
    /// The uses check 2 allows although a ban names their type, each with its reason, as "Deliberately not rethrown" gives
    /// one for a catch. It holds what the compiler itself emits into Core and Application.
    /// </summary>
    public static IReadOnlyList<(string Type, string Member, string Reason)> AllowedInner { get; } =
    [
        ("System.Environment", "get_CurrentManagedThreadId", "the compiler's iterator state machines (every method with yield) compare it with the thread that made the enumerator, to reuse the instance"),
    ];

    /// <summary>
    /// The namespaces of generated types that check 5 passes over, with the reason: the compiler's embedded attributes and
    /// Refit's generated clients have no say in which project they are in.
    /// </summary>
    public static IReadOnlyList<(string Namespace, string Reason)> GeneratedNamespaces { get; } =
    [
        ("Microsoft.CodeAnalysis", "the compiler's embedded attribute (EmbeddedAttribute), emitted into every assembly"),
        ("System.Runtime.CompilerServices", "the compiler's embedded attributes (nullable, ref safety, interpolated strings), emitted into every assembly"),
        ("Refit.Implementation", "Refit's source generator writes the clients here (architecture 1.5)"),
        ("RefitInternalGenerated", "Refit's source generator writes its attribute here"),
    ];

    /// <summary>Checks the four assemblies' facts, with <paramref name="programDll"/> the constant Core names the exe's dll by (check 7).</summary>
    public static IReadOnlyList<Violation> Check(IReadOnlyList<AssemblyFacts> assemblies, string programDll)
    {
        var found = new List<Violation>();
        foreach (var assembly in assemblies)
        {
            CheckReferences(assembly, found);
            if (assembly.Name is Core or Application)
            {
                foreach (var type in assembly.Types) CheckInner(assembly.Name, type, found);
            }

            if (assembly.Name == Core)
            {
                foreach (var type in assembly.Types) CheckCore(type, found);
            }

            CheckNamespaces(assembly, found);
        }

        CheckVersions(assemblies, found);
        CheckExe(assemblies, programDll, found);
        return found;
    }

    /// <summary>Check 1: what each assembly may reference.</summary>
    private static void CheckReferences(AssemblyFacts assembly, List<Violation> found)
    {
        foreach (var reference in assembly.References)
        {
            var allowed = assembly.Name switch
            {
                Core => IsFramework(reference),
                Application => IsFramework(reference) || reference == Core || ApplicationPackages.Contains(reference),
                Infrastructure => reference != Cli && reference != Tests,
                Cli => reference != Tests,
                _ => true,
            };
            if (!allowed) found.Add(new Violation(1, assembly.Name, assembly.Name, null, $"references {reference}"));
        }
    }

    /// <summary>True for an assembly of the shared framework: System and its parts, netstandard and mscorlib.</summary>
    public static bool IsFramework(string assemblyName) =>
        assemblyName == "System" || assemblyName.StartsWith("System.", StringComparison.Ordinal)
        || assemblyName is "netstandard" or "mscorlib" or "Microsoft.CSharp" or "Microsoft.Win32.Primitives";

    /// <summary>Check 2: what a type of Core or Application may not use.</summary>
    private static void CheckInner(string assembly, TypeFacts type, List<Violation> found)
    {
        if (type.DeclaresPInvoke) found.Add(new Violation(2, assembly, type.FullName, null, "declares a method with a P/Invoke import"));
        if (type.IsComImport) found.Add(new Violation(2, assembly, type.FullName, null, "is a COM import"));
        foreach (var use in type.Uses)
        {
            var reason = BannedInner(use);
            if (reason is not null) found.Add(new Violation(2, assembly, type.FullName, use.Member, $"uses {use}: {reason}"));
        }
    }

    /// <summary>Why a use is banned in Core and Application, or null when it is allowed (check 2).</summary>
    public static string? BannedInner(TypeUse use)
    {
        var type = use.Type;
        if (AllowedInner.Any(allowed => allowed.Type == type && allowed.Member == use.Member)) return null;
        if (DiskTypes.Contains(type)) return "the disk";
        if (type is "System.IO.StreamReader" or "System.IO.StreamWriter" && use.Member == ".ctor" && use.Parameters.Count > 0 && use.Parameters[0] == "System.String") return "a reader or writer opened by a path";
        if (type == "System.IO.Path" && use.Member is not null && !AllowedPathMembers.Contains(use.Member)) return "Path beyond its string work";
        if (type is "System.Diagnostics.Process" or "System.Diagnostics.ProcessStartInfo") return "a process";
        if (type == "System.Environment" && use.Member is not null && use.Member != "get_NewLine") return "the environment";
        if (type == "System.AppContext" && use.Member == "get_BaseDirectory") return "the program's folder";
        if (type == "System.AppDomain") return "the app domain";
        if (type is "System.Threading.EventWaitHandle" or "System.Threading.Mutex" or "System.Threading.Semaphore")
        {
            if (use.Member == ".ctor" && use.Parameters.Contains("System.String")) return "a named kernel object";
            if (use.Member is "OpenExisting" or "TryOpenExisting") return "a named kernel object";
        }

        if (type == "System.Console") return "the console";
        if (type == "System.Runtime.InteropServices.Marshal") return "interop";
        if (type == "System.CommandLine") return "the command line's parser";
        foreach (var prefix in BannedPrefixes)
        {
            if (type.StartsWith(prefix, StringComparison.Ordinal)) return $"{prefix.TrimEnd('.')} is outside Core and Application";
        }

        return null;
    }

    /// <summary>Check 3: what a type of Core alone may not use.</summary>
    private static void CheckCore(TypeFacts type, List<Violation> found)
    {
        foreach (var use in type.Uses)
        {
            var reason = BannedCore(use);
            if (reason is not null) found.Add(new Violation(3, Core, type.FullName, use.Member, $"uses {use}: {reason}"));
        }
    }

    /// <summary>Why a use is banned in Core, or null when it is allowed (check 3; check 4 is what this leaves allowed).</summary>
    public static string? BannedCore(TypeUse use)
    {
        var type = use.Type;
        if (type.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)) return "Core has no logger, options or services";
        if (type is "System.Text.Json.JsonSerializer" or "System.Text.Json.Serialization.JsonSerializerContext") return "Core writes no JSON of its own";
        if (type is "System.Threading.Thread" or "System.Threading.Timer") return "Core starts no thread and no timer";
        return null;
    }

    /// <summary>Check 5: every type of an assembly sits in its root namespace or below, but the generated ones and the exe's Program.</summary>
    private static void CheckNamespaces(AssemblyFacts assembly, List<Violation> found)
    {
        if (!RootNamespaces.TryGetValue(assembly.Name, out var root)) return;
        foreach (var type in assembly.Types)
        {
            if (type.IsGenerated) continue;
            if (type.Namespace is null && assembly.Name == Cli && type.FullName == "Program") continue;
            if (type.Namespace is not null && GeneratedNamespaces.Any(generated => type.Namespace == generated.Namespace)) continue;
            if (type.Namespace == root || (type.Namespace is not null && type.Namespace.StartsWith(root + ".", StringComparison.Ordinal))) continue;
            found.Add(new Violation(5, assembly.Name, type.FullName, null, $"is outside {root}"));
        }
    }

    /// <summary>Check 6: the four assemblies carry one informational version.</summary>
    private static void CheckVersions(IReadOnlyList<AssemblyFacts> assemblies, List<Violation> found)
    {
        var versions = assemblies.Select(assembly => assembly.InformationalVersion).Distinct(StringComparer.Ordinal).ToList();
        if (versions.Count > 1)
        {
            foreach (var assembly in assemblies) found.Add(new Violation(6, assembly.Name, assembly.Name, null, $"carries the version '{assembly.InformationalVersion}' where the others differ"));
        }
    }

    /// <summary>Check 7: the Cli assembly is named ClassScribe, and Core's constant names its dll.</summary>
    private static void CheckExe(IReadOnlyList<AssemblyFacts> assemblies, string programDll, List<Violation> found)
    {
        if (assemblies.All(assembly => assembly.Name != Cli))
        {
            found.Add(new Violation(7, Cli, Cli, null, "no assembly is named ClassScribe: the exe would not be ClassScribe.exe"));
            return;
        }

        if (programDll != Cli + ".dll") found.Add(new Violation(7, Core, "ProgramFiles", "Dll", $"names '{programDll}', not '{Cli}.dll', which a recorder or camera process under dotnet starts"));
    }
}
