using System.Net;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Refit;

namespace ClassScribe.Tests;

/// <summary>
/// The read of the account's credits, through the Refit interface and the stub transport: the request, the numbers read
/// from a good answer, a refusal read rather than thrown, every other failure reported as unavailable with its reason, the
/// read's own time limit, the caller's cancel, the setting that turns it off, and a log that never holds the key.
/// </summary>
[TestFixture]
public class ElevenLabsAccountTests
{
    private const string Key = "dummy-key-for-a-local-test";

    /// <summary>When the period resets: 1 November 2026 at noon UTC.</summary>
    private static readonly DateTimeOffset Reset = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>ElevenLabs' subscription answer, as its reference gives it, with the fields this app reads and others it passes over.</summary>
    private static readonly string Subscription =
        $$"""{"tier":"creator","character_count":43362,"character_limit":100000,"next_character_count_reset_unix":{{Reset.ToUnixTimeSeconds()}},"status":"active","can_extend_character_limit":true,"max_credit_limit_extension":"unlimited","current_overage":{"amount":"1.23","currency":"usd"},"billing_period":"monthly_period","currency":"usd","invoices":[]}""";

    private StubHttpHandler _handler = null!;
    private RecordingLogger<ElevenLabsAccount> _log = null!;

    /// <summary>The stub transport and the log to look at.</summary>
    [SetUp]
    public void SetUp()
    {
        _handler = new StubHttpHandler();
        _log = new RecordingLogger<ElevenLabsAccount>();
    }

    /// <summary>Whatever a test did, the key never reached the log.</summary>
    [TearDown]
    public void TearDown() => Assert.That(_log.Messages, Has.None.Contains(Key), "the key is never logged");

    /// <summary>The reader under test: the Refit interface over the stub transport, with the settings and clock given.</summary>
    private ElevenLabsAccount Create(ElevenLabsOptions? options = null, TimeProvider? time = null)
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("https://api.elevenlabs.io/") };
        var api = RestService.ForGenerated<IElevenLabsApi>(http, ElevenLabsJson.Default, ElevenLabsApiSettings.Create());
        return new ElevenLabsAccount(api, TestOptions.Create(options ?? new ElevenLabsOptions { ApiKey = Key }), time ?? TimeProvider.System, _log);
    }

    /// <summary>Queues an answer for the subscription endpoint, the only one the reader calls.</summary>
    private void Answer(HttpStatusCode status, string body) => _handler.EnqueueFor(StubHttpHandler.SubscriptionPath, status, body);

    /// <summary>The account is read with one GET to the subscription endpoint, with the key in its header and no body, and every number is read from the answer.</summary>
    [Test]
    public async Task The_account_is_read_with_a_GET_and_the_numbers_are_parsed()
    {
        Answer(HttpStatusCode.OK, Subscription);

        var reading = await Create().ReadAsync(CancellationToken.None);

        var request = _handler.Requests.Single();
        Assert.That((request.Method, request.Uri!.AbsoluteUri, request.ApiKey, request.Body), Is.EqualTo((HttpMethod.Get, "https://api.elevenlabs.io/v1/user/subscription", Key, "")));
        Assert.That(reading, Is.EqualTo(new AccountReading(AccountReadingKind.Read, 43362, 100000, Reset, 1.23m, "usd")));
    }

    /// <summary>
    /// A 401 or a 403 is a refusal, read and not thrown, with ElevenLabs' reason kept: the detail's status (the older field),
    /// its code (the current one), or the detail itself when it is plain words; nothing when the body says nothing readable.
    /// </summary>
    [TestCase(HttpStatusCode.Unauthorized, """{"detail":{"status":"missing_permissions","message":"The API key you used is missing the permission user_read to execute this operation."}}""", "missing_permissions")]
    [TestCase(HttpStatusCode.Forbidden, """{"detail":{"code":"insufficient_permissions","status":"authorization_error"}}""", "insufficient_permissions")]
    [TestCase(HttpStatusCode.Forbidden, """{"detail":"Forbidden"}""", "Forbidden")]
    [TestCase(HttpStatusCode.Unauthorized, "<html>Unauthorized</html>", null)]
    public async Task A_refusal_is_read_not_thrown(HttpStatusCode status, string body, string? reason)
    {
        Answer(status, body);

        var reading = await Create().ReadAsync(CancellationToken.None);

        Assert.That(reading, Is.EqualTo(new AccountReading(AccountReadingKind.Refused, Status: (int)status, Reason: reason)));
        Assert.That(_handler.Requests, Has.Count.EqualTo(1), "never tried again");
    }

    /// <summary>Any other failure is unavailable, with the reason in words: another status, a broken connection, or an answer that is not ElevenLabs'.</summary>
    [Test]
    public async Task Other_failures_are_unavailable_with_the_reason()
    {
        Answer(HttpStatusCode.InternalServerError, "busy");
        _handler.EnqueueFailureFor(StubHttpHandler.SubscriptionPath, new HttpRequestException("Connection reset."));
        Answer(HttpStatusCode.OK, "not json at all");
        Answer(HttpStatusCode.OK, """{"tier":"creator"}""");
        var account = Create();

        var serverError = await account.ReadAsync(CancellationToken.None);
        var broken = await account.ReadAsync(CancellationToken.None);
        var notJson = await account.ReadAsync(CancellationToken.None);
        var noCount = await account.ReadAsync(CancellationToken.None);

        Assert.That(serverError, Is.EqualTo(new AccountReading(AccountReadingKind.Unavailable, Status: 500, Reason: "HTTP 500")));
        Assert.That(broken, Is.EqualTo(new AccountReading(AccountReadingKind.Unavailable, Reason: "the connection failed")));
        Assert.That(notJson, Is.EqualTo(new AccountReading(AccountReadingKind.Unavailable, Status: 200, Reason: "the answer could not be read")));
        Assert.That(noCount, Is.EqualTo(new AccountReading(AccountReadingKind.Unavailable, Status: 200, Reason: "the answer could not be read")));
        Assert.That(_log.Problems, Is.Empty, "noted at Debug, never a warning");
    }

    /// <summary>A read has 10 seconds, the answer included: an answer whose body never arrives ends as unavailable when the clock reaches the limit.</summary>
    [Test]
    public async Task A_read_that_runs_into_its_limit_is_unavailable()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        _handler.EnqueueStalledBodyFor(StubHttpHandler.SubscriptionPath, HttpStatusCode.OK);
        var read = Create(time: time).ReadAsync(CancellationToken.None);
        await Wait.UntilAsync(() => _handler.Requests.Count == 1);

        time.Advance(ElevenLabsAccount.Limit);

        var reading = await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(reading, Is.EqualTo(new AccountReading(AccountReadingKind.Unavailable, Reason: "no answer within 10 seconds")));
    }

    /// <summary>The caller's own cancel is the one thing that ends a read as a cancellation, so that Ctrl+C is reported as one.</summary>
    [Test]
    public void The_callers_cancel_stays_a_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        _handler.EnqueueFailureFor(StubHttpHandler.SubscriptionPath, () =>
        {
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        });
        var account = Create();

        Assert.CatchAsync<OperationCanceledException>(async () => await account.ReadAsync(cancellation.Token));
    }

    /// <summary>With ElevenLabs:ReadAccountUsage off, or without a key, nothing is asked.</summary>
    [Test]
    public async Task Switched_off_or_without_a_key_nothing_is_asked()
    {
        var off = await Create(new ElevenLabsOptions { ApiKey = Key, ReadAccountUsage = false }).ReadAsync(CancellationToken.None);
        var noKey = await Create(new ElevenLabsOptions { ApiKey = " " }).ReadAsync(CancellationToken.None);

        Assert.That((off.Kind, noKey.Kind), Is.EqualTo((AccountReadingKind.Off, AccountReadingKind.Off)));
        Assert.That(_handler.Requests, Is.Empty);
    }

    /// <summary>An answer with the fields in other shapes still reads: the overage as a number, no limit, no reset, no overage.</summary>
    [Test]
    public async Task An_answer_in_other_shapes_still_reads()
    {
        Answer(HttpStatusCode.OK, """{"character_count":"12","character_limit":30000,"current_overage":{"amount":2.5,"currency":"usd"}}""");
        Answer(HttpStatusCode.OK, """{"character_count":12}""");
        var account = Create();

        var numbers = await account.ReadAsync(CancellationToken.None);
        var bare = await account.ReadAsync(CancellationToken.None);

        Assert.That(numbers, Is.EqualTo(new AccountReading(AccountReadingKind.Read, 12, 30000, null, 2.5m, "usd")));
        Assert.That(bare, Is.EqualTo(new AccountReading(AccountReadingKind.Read, 12)));
    }
}
