using ClassScribe.Core.Desktop;
using ClassScribe.Infrastructure.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// The seven checks of architecture 2.4 over the four built assemblies, read from the test's output folder with
/// System.Reflection.Metadata (<see cref="ArchitectureFacts"/>) and judged by the pure <see cref="ArchitectureRules"/>: the
/// assembly references, what Core and Application may not call, what Core alone may not, the namespaces, one version and
/// the exe's name. The reader is checked too: it must see Infrastructure's disk calls, P/Invokes and COM imports, so that
/// an empty list of violations means something.
/// </summary>
[TestFixture]
public class ArchitectureTests
{
    private IReadOnlyList<AssemblyFacts> facts = [];
    private IReadOnlySet<string> packages = new HashSet<string>();
    private IReadOnlyList<Violation> violations = [];

    /// <summary>Reads the four assemblies and the names of the files beside them once, and runs every check.</summary>
    [OneTimeSetUp]
    public void ReadTheAssemblies()
    {
        facts = AppAssemblies.Paths.Select(ArchitectureFacts.Read).ToList();
        packages = AppAssemblies.PackagesBeside();
        violations = ArchitectureRules.Check(facts, ProgramFiles.Dll, packages);
    }

    /// <summary>The four assemblies are read, innermost first, each with its types.</summary>
    [Test]
    public void The_four_assemblies_are_read()
    {
        Assert.That(facts.Select(assembly => assembly.Name), Is.EqualTo(new[] { ArchitectureRules.Core, ArchitectureRules.Application, ArchitectureRules.Infrastructure, ArchitectureRules.Cli }));
        Assert.That(facts.All(assembly => assembly.Types.Count > 10), Is.True);
    }

    /// <summary>Check 1: Core references the framework alone, Application its four packages and Core, Infrastructure never the Cli, none the tests; a package named System.* is told from the framework by the files beside the four.</summary>
    [Test]
    public void Each_assembly_references_only_what_the_rule_allows() => AssertNone(1);

    /// <summary>
    /// The files beside the four are read from the output folder, the solution's two packages named System.* among them, so
    /// that check 1 tells them from the framework, which is never there; an empty set would let every such package pass.
    /// </summary>
    [Test]
    public void The_packages_beside_the_four_are_read()
    {
        Assert.That(packages, Is.SupersetOf(new[] { "System.CommandLine", "System.Security.Cryptography.ProtectedData", "NAudio.Core", "Refit" }));
        Assert.That(packages, Does.Not.Contain("System.Runtime"));
        Assert.That(packages, Does.Not.Contain("System.Text.Json"));
        Assert.That(packages, Does.Not.Contain(ArchitectureRules.Core));
        Assert.That(packages, Does.Not.Contain(ArchitectureRules.Cli));
    }

    /// <summary>Check 2: Core and Application reach no file, process, environment, kernel object, console, network, pipe, interop, vendor or host.</summary>
    [Test]
    public void Core_and_Application_call_nothing_of_the_machine_or_the_vendors() => AssertNone(2);

    /// <summary>Check 3: Core uses no logger, options, services, JSON serializer, thread or timer.</summary>
    [Test]
    public void Core_uses_no_logging_options_services_json_serializer_thread_or_timer() => AssertNone(3);

    /// <summary>Check 4: Core keeps the JSON serialization attributes of the run files' records, which the bans leave allowed.</summary>
    [Test]
    public void Core_keeps_the_json_attributes_of_the_run_records()
    {
        var core = facts.Single(assembly => assembly.Name == ArchitectureRules.Core);
        var attributes = core.Types.SelectMany(type => type.Uses).Where(use => use.Type.StartsWith("System.Text.Json.Serialization.", StringComparison.Ordinal)).ToList();

        Assert.That(attributes.Select(use => use.Type), Does.Contain("System.Text.Json.Serialization.JsonExtensionDataAttribute"));
        Assert.That(attributes.Select(use => use.Type), Does.Contain("System.Text.Json.Serialization.JsonPolymorphicAttribute"));
        Assert.That(attributes.Select(ArchitectureRules.BannedCore), Is.All.Null);
    }

    /// <summary>Check 5: every type sits in its assembly's root namespace or below.</summary>
    [Test]
    public void Every_type_sits_in_its_assembly_root_namespace() => AssertNone(5);

    /// <summary>Check 6: the four assemblies carry one informational version.</summary>
    [Test]
    public void The_four_assemblies_carry_one_version()
    {
        AssertNone(6);
        Assert.That(facts[0].InformationalVersion, Is.Not.Empty);
    }

    /// <summary>Check 7: the Cli assembly is ClassScribe, and ProgramFiles.Dll names its dll.</summary>
    [Test]
    public void The_exe_is_ClassScribe_and_Core_names_its_dll() => AssertNone(7);

    /// <summary>The reader sees Infrastructure's disk calls, so that check 2's silence about Core and Application is worth something.</summary>
    [Test]
    public void The_reader_sees_the_disk_calls_of_Infrastructure()
    {
        var infrastructure = facts.Single(assembly => assembly.Name == ArchitectureRules.Infrastructure);
        var disk = infrastructure.Types.Where(type => type.Uses.Any(use => use.Type is "System.IO.File" or "System.IO.Directory")).Select(type => type.FullName).ToList();

        Assert.That(disk, Does.Contain(typeof(ClassFolders).FullName));
        Assert.That(disk.Count, Is.GreaterThan(10));
    }

    /// <summary>The reader sees Infrastructure's P/Invokes and COM imports, so that check 2's silence about them is worth something.</summary>
    [Test]
    public void The_reader_sees_the_interop_of_Infrastructure()
    {
        var infrastructure = facts.Single(assembly => assembly.Name == ArchitectureRules.Infrastructure);

        Assert.That(infrastructure.Types.Count(type => type.DeclaresPInvoke), Is.GreaterThan(3));
        Assert.That(infrastructure.Types.Count(type => type.IsComImport), Is.GreaterThan(3));
        Assert.That(infrastructure.Types.Any(type => type.Uses.Any(use => use.Type == "System.Runtime.InteropServices.Marshal")), Is.True);
    }

    /// <summary>The reader sees a member's parameters, by which the banned constructors are told from the allowed ones.</summary>
    [Test]
    public void The_reader_sees_the_parameters_of_the_members_used()
    {
        var uses = facts.SelectMany(assembly => assembly.Types).SelectMany(type => type.Uses).ToList();

        Assert.That(uses.Any(use => use.Type == "System.IO.Path" && use.Member == "Combine" && use.Parameters.SequenceEqual(["System.String", "System.String"])), Is.True);
        Assert.That(uses.Any(use => use.Type == "System.IO.File" && use.Member == "Exists" && use.Parameters.SequenceEqual(["System.String"])), Is.True, "Infrastructure asks whether a file is there");
        Assert.That(uses.Any(use => use.Type == "System.IO.FileStream" && use.Member == ".ctor" && use.Parameters.Count > 0 && use.Parameters[0] == "System.String"), Is.True, "Infrastructure opens a file by its path");
    }

    /// <summary>Fails with every violation of <paramref name="check"/>, one per line.</summary>
    private void AssertNone(int check)
    {
        var found = violations.Where(violation => violation.Check == check).Select(violation => violation.ToString()).ToList();
        Assert.That(found, Is.Empty, string.Join(Environment.NewLine, found));
    }
}
