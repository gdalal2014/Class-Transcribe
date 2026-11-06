namespace ClassScribe.Tests;

/// <summary>
/// The rules of architecture 2.4 fed planted facts: each ban fails with its check's number and the type that holds it, and
/// each thing the design names as allowed passes, so that a ban that stops working fails here at once, not at a later
/// review. The four assemblies are well formed but for the one fact each case plants.
/// </summary>
[TestFixture]
public class ArchitectureRulesTests
{
    private const string Version = "0.4.0+abc";

    /// <summary>A use of a type, or of its member with the parameter types given.</summary>
    private static TypeUse Use(string type, string? member = null, params string[] parameters) => new(type, member, parameters);

    /// <summary>A type with the uses given.</summary>
    private static TypeFacts Type(string fullName, params TypeUse[] uses) =>
        new(fullName, fullName.Contains('.') ? fullName[..fullName.LastIndexOf('.')] : null, IsGenerated: false, DeclaresPInvoke: false, IsComImport: false, uses);

    /// <summary>An assembly with the references and types given, at the common version.</summary>
    private static AssemblyFacts Assembly(string name, string[] references, params TypeFacts[] types) => new(name, Version, references, types);

    /// <summary>The four assemblies, well formed, with one extra type or reference planted in one of them.</summary>
    private static IReadOnlyList<AssemblyFacts> Four(TypeFacts? core = null, TypeFacts? application = null, TypeFacts? infrastructure = null, TypeFacts? cli = null,
        string? coreReference = null, string? applicationReference = null, string? infrastructureReference = null, string? cliReference = null)
    {
        static string[] References(string[] usual, string? extra) => extra is null ? usual : [.. usual, extra];
        static TypeFacts[] Types(TypeFacts usual, TypeFacts? extra) => extra is null ? [usual] : [usual, extra];
        return
        [
            Assembly(ArchitectureRules.Core, References(["System.Runtime", "System.Collections"], coreReference), Types(Type("ClassScribe.Core.Durations", Use("System.IO.Path", "Combine", "System.String", "System.String")), core)),
            Assembly(ArchitectureRules.Application, References(["System.Runtime", "ClassScribe.Core", "Microsoft.Extensions.Logging.Abstractions", "Microsoft.Extensions.Options", "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Primitives"], applicationReference), Types(Type("ClassScribe.Application.BuildInfo"), application)),
            Assembly(ArchitectureRules.Infrastructure, References(["System.Runtime", "ClassScribe.Core", "ClassScribe.Application", "NAudio.Core", "Refit"], infrastructureReference), Types(Type("ClassScribe.Infrastructure.Storage.ClassFolders", Use("System.IO.File", "Exists", "System.String")), infrastructure)),
            Assembly(ArchitectureRules.Cli, References(["System.Runtime", "ClassScribe.Core", "ClassScribe.Application", "ClassScribe.Infrastructure", "System.CommandLine"], cliReference), Types(Type("ClassScribe.Cli.AppHost"), cli)),
        ];
    }

    /// <summary>
    /// What sits as files beside the four in an output folder: the packages, two of them named System.*, and the tests;
    /// never the framework, which the runtime loads from the dotnet folder.
    /// </summary>
    private static readonly IReadOnlySet<string> Packages = new HashSet<string>(StringComparer.Ordinal)
    {
        "NAudio.Core", "NAudio.Wasapi", "Refit", "Refit.HttpClientFactory", "System.CommandLine", "System.Security.Cryptography.ProtectedData",
        "Microsoft.Extensions.Logging.Abstractions", "Microsoft.Extensions.Options", "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Primitives",
        "Microsoft.Extensions.Configuration.Abstractions", "Microsoft.Extensions.Hosting", "ClassScribe.Tests",
    };

    /// <summary>The violations of the four assemblies given, with the exe's constant right and the usual files beside them.</summary>
    private static IReadOnlyList<Violation> Check(IReadOnlyList<AssemblyFacts> assemblies) => ArchitectureRules.Check(assemblies, "ClassScribe.dll", Packages);

    /// <summary>Asserts one violation of <paramref name="check"/> naming <paramref name="type"/>, and no other.</summary>
    private static void AssertOne(IReadOnlyList<AssemblyFacts> assemblies, int check, string type)
    {
        var found = Check(assemblies);

        Assert.That(found.Select(violation => (violation.Check, violation.Type)), Is.EqualTo(new[] { (check, type) }), string.Join(Environment.NewLine, found));
    }

    /// <summary>Asserts that nothing is found.</summary>
    private static void AssertNone(IReadOnlyList<AssemblyFacts> assemblies)
    {
        var found = Check(assemblies);

        Assert.That(found, Is.Empty, string.Join(Environment.NewLine, found));
    }

    /// <summary>The four well-formed assemblies pass every check.</summary>
    [Test]
    public void Well_formed_assemblies_pass() => AssertNone(Four());

    /// <summary>Check 2: the disk, a path's existence and the current folder in a Core type.</summary>
    [TestCase("System.IO.File", "Exists", TestName = "File.Exists in Core fails check 2")]
    [TestCase("System.IO.Path", "Exists", TestName = "Path.Exists in Core fails check 2")]
    [TestCase("System.IO.Path", "GetFullPath", TestName = "Path.GetFullPath in Core fails check 2")]
    [TestCase("System.IO.Path", "GetRelativePath", TestName = "Path.GetRelativePath in Core fails check 2")]
    [TestCase("System.IO.Path", "GetTempPath", TestName = "Path.GetTempPath in Core fails check 2")]
    [TestCase("System.IO.Directory", "CreateDirectory", TestName = "Directory.CreateDirectory in Core fails check 2")]
    [TestCase("System.IO.FileStream", ".ctor", TestName = "new FileStream in Core fails check 2")]
    [TestCase("System.IO.FileInfo", "get_Length", TestName = "FileInfo.Length in Core fails check 2")]
    public void The_disk_is_banned_in_Core(string type, string member) =>
        AssertOne(Four(core: Type("ClassScribe.Core.Storage.Planted", Use(type, member, "System.String"))), 2, "ClassScribe.Core.Storage.Planted");

    /// <summary>Check 2: a process, a reader opened by a path, the app domain, a named event and the environment in an Application type.</summary>
    [TestCase("System.Diagnostics.Process", "GetCurrentProcess", new string[0], TestName = "Process.GetCurrentProcess in Application fails check 2")]
    [TestCase("System.IO.StreamReader", ".ctor", new[] { "System.String" }, TestName = "new StreamReader(string) in Application fails check 2")]
    [TestCase("System.AppDomain", "get_CurrentDomain", new string[0], TestName = "AppDomain.CurrentDomain in Application fails check 2")]
    [TestCase("System.Threading.EventWaitHandle", ".ctor", new[] { "System.Boolean", "System.Threading.EventResetMode", "System.String" }, TestName = "new EventWaitHandle(bool, EventResetMode, string) in Application fails check 2")]
    [TestCase("System.Threading.Mutex", "OpenExisting", new[] { "System.String" }, TestName = "Mutex.OpenExisting in Application fails check 2")]
    [TestCase("System.Threading.Semaphore", ".ctor", new[] { "System.Int32", "System.Int32", "System.String" }, TestName = "new Semaphore(int, int, string) in Application fails check 2")]
    [TestCase("System.Environment", "get_ProcessPath", new string[0], TestName = "Environment.ProcessPath in Application fails check 2")]
    [TestCase("System.Environment", "GetEnvironmentVariable", new[] { "System.String" }, TestName = "Environment.GetEnvironmentVariable in Application fails check 2")]
    [TestCase("System.AppContext", "get_BaseDirectory", new string[0], TestName = "AppContext.BaseDirectory in Application fails check 2")]
    [TestCase("System.Console", "WriteLine", new[] { "System.String" }, TestName = "Console.WriteLine in Application fails check 2")]
    [TestCase("System.Runtime.InteropServices.Marshal", "SizeOf", new[] { "System.Type" }, TestName = "Marshal.SizeOf in Application fails check 2")]
    [TestCase("Microsoft.Win32.Registry", "GetValue", new[] { "System.String", "System.String", "System.Object" }, TestName = "Registry.GetValue in Application fails check 2")]
    [TestCase("System.Net.Http.HttpClient", ".ctor", new string[0], TestName = "new HttpClient in Application fails check 2")]
    [TestCase("System.Net.HttpListener", ".ctor", new string[0], TestName = "new HttpListener in Application fails check 2")]
    [TestCase("System.Net.HttpListenerException", "get_ErrorCode", new string[0], TestName = "HttpListenerException in Application fails check 2")]
    [TestCase("System.Net.Sockets.Socket", "Connect", new[] { "System.Net.EndPoint" }, TestName = "Socket.Connect in Application fails check 2")]
    [TestCase("System.IO.Pipes.NamedPipeServerStream", ".ctor", new[] { "System.String" }, TestName = "new NamedPipeServerStream in Application fails check 2")]
    [TestCase("NAudio.Wave.WaveFormat", ".ctor", new string[0], TestName = "NAudio in Application fails check 2")]
    [TestCase("Windows.Media.Capture.MediaCapture", ".ctor", new string[0], TestName = "WinRT in Application fails check 2")]
    [TestCase("Refit.GetAttribute", ".ctor", new[] { "System.String" }, TestName = "Refit in Application fails check 2")]
    [TestCase("System.CommandLine.Command", ".ctor", new[] { "System.String" }, TestName = "System.CommandLine in Application fails check 2")]
    [TestCase("Microsoft.Extensions.Configuration.IConfiguration", "get_Item", new[] { "System.String" }, TestName = "IConfiguration in Application fails check 2")]
    [TestCase("Microsoft.Extensions.Hosting.IHost", "get_Services", new string[0], TestName = "IHost in Application fails check 2")]
    [TestCase("Microsoft.Extensions.Http.HttpClientFactoryOptions", ".ctor", new string[0], TestName = "HttpClientFactoryOptions in Application fails check 2")]
    public void The_machine_and_the_vendors_are_banned_in_Application(string type, string member, string[] parameters) =>
        AssertOne(Four(application: Type("ClassScribe.Application.Commands.Planted", Use(type, member, parameters))), 2, "ClassScribe.Application.Commands.Planted");

    /// <summary>Check 2: a type that is itself a P/Invoke declaration or a COM import, in Core or Application.</summary>
    [Test]
    public void A_PInvoke_or_a_COM_import_in_Application_fails_check_2()
    {
        var pinvoke = new TypeFacts("ClassScribe.Application.Desktop.Planted", "ClassScribe.Application.Desktop", IsGenerated: false, DeclaresPInvoke: true, IsComImport: false, []);
        var comImport = new TypeFacts("ClassScribe.Core.Audio.Planted", "ClassScribe.Core.Audio", IsGenerated: false, DeclaresPInvoke: false, IsComImport: true, []);

        AssertOne(Four(application: pinvoke), 2, "ClassScribe.Application.Desktop.Planted");
        AssertOne(Four(core: comImport), 2, "ClassScribe.Core.Audio.Planted");
    }

    /// <summary>Check 2's allow-list: what the design names as allowed in Core passes.</summary>
    [TestCase("System.IO.Path", "Combine", new[] { "System.String", "System.String" }, TestName = "Path.Combine in Core passes")]
    [TestCase("System.IO.Path", "GetFileNameWithoutExtension", new[] { "System.String" }, TestName = "Path.GetFileNameWithoutExtension in Core passes")]
    [TestCase("System.IO.Path", "TrimEndingDirectorySeparator", new[] { "System.String" }, TestName = "Path.TrimEndingDirectorySeparator in Core passes")]
    [TestCase("System.IO.Path", "DirectorySeparatorChar", new string[0], TestName = "Path.DirectorySeparatorChar in Core passes")]
    [TestCase("System.Environment", "get_CurrentManagedThreadId", new string[0], TestName = "Environment.CurrentManagedThreadId, which the compiler's iterators read, in Core passes")]
    [TestCase("System.IO.Path", null, new string[0], TestName = "The Path type named alone in Core passes")]
    [TestCase("System.IO.StreamReader", ".ctor", new[] { "System.IO.Stream" }, TestName = "new StreamReader(Stream) in Core passes")]
    [TestCase("System.IO.MemoryStream", ".ctor", new string[0], TestName = "new MemoryStream in Core passes")]
    [TestCase("System.IO.IOException", ".ctor", new[] { "System.String" }, TestName = "new IOException in Core passes")]
    [TestCase("System.Threading.SemaphoreSlim", ".ctor", new[] { "System.Int32" }, TestName = "new SemaphoreSlim(1) in Core passes")]
    [TestCase("System.Threading.EventWaitHandle", ".ctor", new[] { "System.Boolean", "System.Threading.EventResetMode" }, TestName = "new EventWaitHandle(bool, EventResetMode) in Core passes")]
    [TestCase("System.Environment", "get_NewLine", new string[0], TestName = "Environment.NewLine in Core passes")]
    [TestCase("System.AppContext", "TryGetSwitch", new[] { "System.String", "System.Boolean&" }, TestName = "AppContext.TryGetSwitch in Core passes")]
    [TestCase("System.Text.Json.Serialization.JsonExtensionDataAttribute", ".ctor", new string[0], TestName = "JsonExtensionData in Core passes")]
    [TestCase("System.Text.Json.Serialization.JsonPolymorphicAttribute", ".ctor", new string[0], TestName = "JsonPolymorphic in Core passes")]
    [TestCase("System.Text.Json.JsonElement", "GetString", new string[0], TestName = "JsonElement in Core passes")]
    [TestCase("System.Net.HttpStatusCode", null, new string[0], TestName = "HttpStatusCode in Core passes")]
    [TestCase("System.Net.WebUtility", "HtmlEncode", new[] { "System.String" }, TestName = "WebUtility.HtmlEncode in Core passes")]
    [TestCase("System.ConsoleKeyInfo", "get_Key", new string[0], TestName = "ConsoleKeyInfo in Core passes")]
    public void What_the_design_allows_in_Core_passes(string type, string? member, string[] parameters) =>
        AssertNone(Four(core: Type("ClassScribe.Core.Audio.Planted", Use(type, member, parameters))));

    /// <summary>Check 3: a logger, the JSON serializer, a context, a thread or a timer in a Core type.</summary>
    [TestCase("Microsoft.Extensions.Logging.ILogger", "Log", TestName = "ILogger in Core fails check 3")]
    [TestCase("Microsoft.Extensions.Options.IOptionsMonitor", "get_CurrentValue", TestName = "IOptionsMonitor in Core fails check 3")]
    [TestCase("System.Text.Json.JsonSerializer", "Serialize", TestName = "JsonSerializer in Core fails check 3")]
    [TestCase("System.Text.Json.Serialization.JsonSerializerContext", null, TestName = "JsonSerializerContext in Core fails check 3")]
    [TestCase("System.Threading.Thread", ".ctor", TestName = "Thread in Core fails check 3")]
    [TestCase("System.Threading.Timer", ".ctor", TestName = "Timer in Core fails check 3")]
    public void What_Core_alone_may_not_use_fails_check_3(string type, string? member) =>
        AssertOne(Four(core: Type("ClassScribe.Core.Audio.Planted", Use(type, member))), 3, "ClassScribe.Core.Audio.Planted");

    /// <summary>Check 3 is Core's alone: a logger, a timer and a thread in an Application type pass.</summary>
    [Test]
    public void A_logger_a_timer_and_a_thread_in_Application_pass() =>
        AssertNone(Four(application: Type("ClassScribe.Application.Audio.Planted", Use("Microsoft.Extensions.Logging.ILogger", "Log"), Use("System.Threading.Timer", ".ctor"), Use("System.Threading.Thread", ".ctor"), Use("System.Text.Json.JsonSerializer", "Serialize"))));

    /// <summary>Check 1: Core referencing NAudio, Application referencing Infrastructure, Infrastructure referencing the Cli, the Cli referencing the tests.</summary>
    [Test]
    public void A_reference_the_rule_forbids_fails_check_1()
    {
        AssertOne(Four(coreReference: "NAudio.Core"), 1, ArchitectureRules.Core);
        AssertOne(Four(coreReference: "Microsoft.Extensions.Logging.Abstractions"), 1, ArchitectureRules.Core);
        AssertOne(Four(applicationReference: "ClassScribe.Infrastructure"), 1, ArchitectureRules.Application);
        AssertOne(Four(applicationReference: "Refit"), 1, ArchitectureRules.Application);
        AssertOne(Four(applicationReference: "Microsoft.Extensions.Configuration.Abstractions"), 1, ArchitectureRules.Application);
        AssertOne(Four(infrastructureReference: "ClassScribe"), 1, ArchitectureRules.Infrastructure);
        AssertOne(Four(infrastructureReference: "ClassScribe.Tests"), 1, ArchitectureRules.Infrastructure);
        AssertOne(Four(cliReference: "ClassScribe.Tests"), 1, ArchitectureRules.Cli);
    }

    /// <summary>Check 1: the framework's own assemblies (which are not beside the four), Application's packages and the inner projects are allowed.</summary>
    [Test]
    public void The_references_the_rule_allows_pass()
    {
        AssertNone(Four(coreReference: "System.Text.Json"));
        AssertNone(Four(coreReference: "netstandard"));
        AssertNone(Four(applicationReference: "System.Net.Primitives"));
        AssertNone(Four(infrastructureReference: "Microsoft.Extensions.Hosting"));
        AssertNone(Four(infrastructureReference: "System.Security.Cryptography.ProtectedData"));
        AssertNone(Four(cliReference: "ClassScribe.Infrastructure"));
    }

    /// <summary>
    /// Check 1: a package named System.*, which sits as a file beside the four where the framework never does, is no part of
    /// the framework: Core referencing System.Security.Cryptography.ProtectedData (Windows' DPAPI) and Application referencing
    /// it or System.CommandLine fail, as NAudio or Refit would; with no such file beside the four the same name passes as the
    /// framework's, which is why <see cref="ArchitectureTests.The_packages_beside_the_four_are_read"/> checks the set read.
    /// </summary>
    [Test]
    public void A_package_named_like_the_framework_fails_check_1()
    {
        AssertOne(Four(coreReference: "System.Security.Cryptography.ProtectedData"), 1, ArchitectureRules.Core);
        AssertOne(Four(applicationReference: "System.Security.Cryptography.ProtectedData"), 1, ArchitectureRules.Application);
        AssertOne(Four(applicationReference: "System.CommandLine"), 1, ArchitectureRules.Application);
        Assert.That(ArchitectureRules.IsFramework("System.CommandLine", Packages), Is.False);
        Assert.That(ArchitectureRules.IsFramework("System.CommandLine", new HashSet<string>()), Is.True, "told apart by the file beside the four alone");
        Assert.That(ArchitectureRules.IsFramework("System.Text.Json", Packages), Is.True);
    }

    /// <summary>Check 5: a type with its old namespace in the Cli assembly, as a file merged into the wrong project would have.</summary>
    [Test]
    public void A_type_with_its_old_namespace_fails_check_5()
    {
        AssertOne(Four(cli: Type("ClassScribe.Commands.MenuCommand")), 5, "ClassScribe.Commands.MenuCommand");
        AssertOne(Four(core: Type("ClassScribe.Application.Audio.Planted")), 5, "ClassScribe.Application.Audio.Planted");
        AssertOne(Four(infrastructure: Type("ClassScribe.Infrastructure2.Planted")), 5, "ClassScribe.Infrastructure2.Planted");
    }

    /// <summary>Check 5 passes the exe's Program in the global namespace, the compiler's generated types and Refit's.</summary>
    [Test]
    public void Program_and_the_generated_types_pass_check_5()
    {
        AssertNone(Four(cli: Type("Program")));
        AssertNone(Four(core: new TypeFacts("<PrivateImplementationDetails>", null, IsGenerated: true, DeclaresPInvoke: false, IsComImport: false, [])));
        AssertNone(Four(core: new TypeFacts("Microsoft.CodeAnalysis.EmbeddedAttribute", "Microsoft.CodeAnalysis", IsGenerated: true, DeclaresPInvoke: false, IsComImport: false, [])));
        AssertNone(Four(infrastructure: Type("Refit.Implementation.Generated")));
        AssertOne(Four(core: Type("Program")), 5, "Program");
    }

    /// <summary>Check 6: one assembly at another version.</summary>
    [Test]
    public void Assemblies_at_different_versions_fail_check_6()
    {
        var four = Four();
        var assemblies = four.Select(assembly => assembly.Name == ArchitectureRules.Cli ? assembly with { InformationalVersion = "0.5.0+def" } : assembly).ToList();

        var found = Check(assemblies);

        Assert.That(found.Select(violation => violation.Check).Distinct(), Is.EqualTo(new[] { 6 }));
        Assert.That(found.Select(violation => violation.Assembly), Is.EquivalentTo(new[] { ArchitectureRules.Core, ArchitectureRules.Application, ArchitectureRules.Infrastructure, ArchitectureRules.Cli }));
    }

    /// <summary>Check 7: no assembly named ClassScribe, or Core's constant naming another dll.</summary>
    [Test]
    public void The_exe_name_and_the_dll_constant_are_checked_7()
    {
        var renamed = Four().Select(assembly => assembly.Name == ArchitectureRules.Cli ? assembly with { Name = "ClassScribe.Cli" } : assembly).ToList();

        Assert.That(ArchitectureRules.Check(renamed, "ClassScribe.dll", Packages).Select(violation => violation.Check), Is.EqualTo(new[] { 7 }));
        Assert.That(ArchitectureRules.Check(Four(), "ClassScribe.Cli.dll", Packages).Select(violation => (violation.Check, violation.Type, violation.Member)), Is.EqualTo(new[] { (7, "ProgramFiles", "Dll") }));
    }

    /// <summary>A violation prints its check, its assembly, its type and member, and the reason.</summary>
    [Test]
    public void A_violation_prints_in_one_line()
    {
        var violation = Check(Four(core: Type("ClassScribe.Core.Storage.Planted", Use("System.IO.File", "Exists", "System.String")))).Single();

        Assert.That(violation.ToString(), Is.EqualTo("check 2: ClassScribe.Core: ClassScribe.Core.Storage.Planted.Exists: uses System.IO.File.Exists(System.String): the disk"));
    }
}
