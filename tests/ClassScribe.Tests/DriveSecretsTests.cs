using System.Runtime.Versioning;
using System.Text;
using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Refit;

namespace ClassScribe.Tests;

/// <summary>
/// No secret of a Google sign-in ever reaches a log line, the window (beyond the sign-in's own address), a file of
/// ClassScribe's own folder, the settings a recorder is handed, or the store in plain text: the client secret, the
/// authorization codes, the PKCE verifiers, the states, the refresh tokens and the access tokens. The whole of a sign-in, a
/// status that renews the token, and a sign-out run through the real app, with every log at Trace, Microsoft.Extensions.Http's
/// included, against the in-memory Google; the store is Windows' real data protection in a temporary folder.
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class DriveSecretsTests
{
    private TempFolder _folder = null!;
    private TestRuntime _runtime = null!;
    private GoogleWorld _world = null!;
    private CapturingLoggerProvider _logs = null!;

    /// <summary>
    /// The app's folder with settings that log everything and a recordings folder of its own, ClassScribe's folders in
    /// temporary folders, and the in-memory Google.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        Assume.That(OperatingSystem.IsWindows(), "Windows' data protection");
        _folder = new TempFolder();
        _runtime = new TestRuntime();
        _world = new GoogleWorld();
        _logs = new CapturingLoggerProvider();
        File.WriteAllText(_folder.File("appsettings.json"), new System.Text.Json.Nodes.JsonObject
        {
            ["Recording"] = new System.Text.Json.Nodes.JsonObject { ["OutputFolder"] = _folder.File("Classes") },
            ["Logging"] = new System.Text.Json.Nodes.JsonObject
            {
                ["LogLevel"] = new System.Text.Json.Nodes.JsonObject { ["Default"] = "Trace", ["Microsoft"] = "Trace", ["System"] = "Trace", ["System.Net.Http"] = "Trace" }
            }
        }.ToJsonString());
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _world?.Dispose();
        _runtime?.Dispose();
        _folder?.Dispose();
    }

    /// <summary>
    /// The app as Program builds it, over the test's folders, the in-memory Google as every HTTP client's handler, the
    /// browser and listener the test plays, the console it reads, its clock, and the provider that keeps every log line.
    /// </summary>
    private IHost BuildApp() =>
        AppHost.Build(new SettingsPlaces(_folder.Path, userFolder: null), "test-key", services =>
        {
            services.AddSingleton(_runtime.Folder);
            services.AddSingleton(new RoamingFolder(_folder.File("Roaming")));
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_world.Time));
            services.Replace(ServiceDescriptor.Singleton<IConsoleUi>(_world.Ui));
            services.Replace(ServiceDescriptor.Singleton<IBrowser>(_world.Browser));
            services.Replace(ServiceDescriptor.Singleton<ILoopbackReceiver>(_world.Receiver));
            services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = _world.Google));
            services.AddSingleton<ILoggerProvider>(_logs);
        });

    /// <summary>Runs a command line through the app, failing the test rather than waiting if it hangs.</summary>
    private static Task<int> RunAsync(IHost app, params string[] args) => AppHost.RunAsync(app.Services, args).WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>
    /// A sign-in with the client file, a status an hour later that renews the access token, and a sign-out: every secret
    /// Google handed out or was handed is in no log line, no file of ClassScribe's own folder, nor the settings snapshot; the
    /// window shows the sign-in's address and nothing else of them; the store holds none in plain text.
    /// </summary>
    [Test]
    public async Task No_secret_reaches_a_log_a_file_or_the_settings()
    {
        using var app = BuildApp();

        Assert.That(await RunAsync(app, "drive", "signin", "--client", _world.WriteClientFile()), Is.EqualTo(0));
        _world.Time.Advance(TimeSpan.FromHours(1));
        Assert.That(await RunAsync(app, "drive", "status"), Is.EqualTo(0));
        var store = File.ReadAllBytes(Path.Combine(_folder.File("Roaming"), "google-drive.dat"));
        Assert.That(await RunAsync(app, "drive", "signout"), Is.EqualTo(0));

        var secrets = _world.Google.Secrets;
        Assert.That(_world.Google.Refreshes, Is.EqualTo(1), "the status renewed the token");
        Assert.That(secrets, Has.Count.GreaterThanOrEqualTo(7), "the client secret, a code, a state, a verifier, a refresh token and two access tokens");
        Assert.That(_logs.Lines, Has.Some.Matches<(string Category, LogLevel Level, string Text)>(line => line.Level == LogLevel.Debug && line.Category.StartsWith("ClassScribe.Drive", StringComparison.Ordinal)),
            "the Drive classes' Debug lines were kept");

        foreach (var secret in secrets)
        {
            var shown = secret[..Math.Min(12, secret.Length)];
            Assert.That(_logs.Lines.Where(line => line.Text.Contains(secret, StringComparison.Ordinal)).Select(line => line.Category), Is.Empty, $"{shown}... in a log line");
            Assert.That(app.Services.GetRequiredService<SettingsReloader>().Snapshot().Values, Has.None.Contains(secret), $"{shown}... in the settings");
            foreach (var file in Directory.GetFiles(_runtime.Temp.Path, "*", SearchOption.AllDirectories))
                Assert.That(File.ReadAllText(file), Does.Not.Contain(secret), $"{shown}... in {Path.GetFileName(file)}");
            Assert.That(store.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)), Is.EqualTo(-1), $"{shown}... in the store in plain text");
        }

        var state = System.Web.HttpUtility.ParseQueryString(_world.Browser.Opened.Single().Query)["state"]!;
        Assert.That(secrets.Where(secret => secret != state && _world.Ui.Screen.Contains(secret, StringComparison.Ordinal)), Is.Empty,
            "the window shows the sign-in's address, with its state, and no other secret");
    }

    /// <summary>
    /// An upload through the real app, with a piece lost on the way so that the session is asked how much it has: no secret,
    /// the upload session's address included, is in a log line, in a file of the recording, or in a file of ClassScribe's own
    /// folder once the upload is done, its manifest included.
    /// </summary>
    [Test]
    public async Task An_upload_leaves_no_secret_in_a_log_or_a_file()
    {
        using var app = BuildApp();
        Assert.That(await RunAsync(app, "drive", "signin", "--client", _world.WriteClientFile()), Is.EqualTo(0));
        var folder = Directory.CreateDirectory(_folder.File(Path.Combine("Classes", "Mantiq", "2026-10-17"))).FullName;
        var files = RecordingFiles.ForNew(folder, "Mantiq", new DateTimeOffset(2026, 10, 17, 14, 4, 42, TimeSpan.Zero));
        GoogleWorld.WriteAudio(files.Segment(1), 300_000);
        File.WriteAllText(files.Log, "a line\n");
        _world.Google.DropNextAfter(1000);

        var upload = AppHost.RunAsync(app.Services, ["drive", "upload", folder]);
        await _world.Time.AdvanceUntilAsync(() => upload.IsCompleted, TimeSpan.FromSeconds(1), timeoutSeconds: 30);

        Assert.That(await upload, Is.EqualTo(0));
        Assert.That(_world.Google.Requests.Count(request => request.IsStatusQuery), Is.EqualTo(1), "the lost piece made the upload ask its session");
        var sessions = _world.Google.Sessions.Keys.ToList();
        Assert.That(sessions, Has.Count.EqualTo(2));
        foreach (var secret in _world.Google.Secrets)
        {
            var shown = secret[..Math.Min(12, secret.Length)];
            Assert.That(_logs.Lines.Where(line => line.Text.Contains(secret, StringComparison.Ordinal)).Select(line => line.Category), Is.Empty, $"{shown}... in a log line");
            foreach (var file in Directory.GetFiles(_runtime.Temp.Path, "*", SearchOption.AllDirectories).Concat(Directory.GetFiles(folder)))
                Assert.That(File.ReadAllText(file), Does.Not.Contain(secret), $"{shown}... in {Path.GetFileName(file)}");
        }
    }

    /// <summary>
    /// Microsoft.Extensions.Http logs nothing of the Google clients' requests, not even their addresses, with its logs at
    /// Trace; a client of the same factory without that setting does log, which shows the logging was on.
    /// </summary>
    [Test]
    public async Task The_Google_clients_log_none_of_their_requests()
    {
        using var app = BuildApp();
        await RunAsync(app, "drive", "signin", "--client", _world.WriteClientFile());

        using (var probe = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe"))
        using (await probe.GetAsync("https://www.googleapis.com/drive/v3/about")) { }

        var httpLines = _logs.Lines.Where(line => line.Category.StartsWith("System.Net.Http.HttpClient.", StringComparison.Ordinal)).Select(line => line.Category).ToList();
        Assert.That(httpLines, Has.Some.StartsWith("System.Net.Http.HttpClient.probe."), "the factory's logging is on");
        Assert.That(httpLines, Has.None.Contains(UniqueName.ForType<IDriveApi>()).And.None.Contains(UniqueName.ForType<IGoogleOAuthApi>()));
        Assert.That(_world.Google.Requests.Count(request => request.Path.StartsWith("/drive/", StringComparison.Ordinal)), Is.GreaterThanOrEqualTo(2), "the Drive client was used");
    }
}
