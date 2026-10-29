using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassScribe.Drive;
using ClassScribe.Drive.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The real listener for Google's redirect, on 127.0.0.1 only, with the test as the browser: it takes a free port, hands over
/// each request with its path and query, answers it as told, and frees the port when closed. Nothing leaves the computer.
/// </summary>
[TestFixture]
public class LoopbackReceiverTests
{
    /// <summary>The receiver under test.</summary>
    private static HttpListenerLoopbackReceiver Receiver() => new(NullLogger<HttpListenerLoopbackReceiver>.Instance);

    /// <summary>A browser for these tests: plain HTTP to this computer, with a short limit.</summary>
    private static HttpClient Browser() => new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// The listener's address is http://127.0.0.1 with a port of its own; a request there is handed over with its path and
    /// decoded query, and the browser gets the page it is answered with, as UTF-8 HTML.
    /// </summary>
    [Test]
    public async Task A_request_is_handed_over_and_answered_as_told()
    {
        using var listener = Receiver().Open();
        using var browser = Browser();
        Assert.That((listener.RedirectUri.Scheme, listener.RedirectUri.Host, listener.RedirectUri.AbsolutePath), Is.EqualTo(("http", "127.0.0.1", "/")));
        Assert.That(listener.RedirectUri.Port, Is.GreaterThan(0));

        var asked = browser.GetAsync(new Uri(listener.RedirectUri, "?code=4%2F0AQ-x&state=s%2Bt&scope=a%20b"));
        var request = await listener.NextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That((request.Path, request.Query("code"), request.Query("state"), request.Query("scope"), request.Query("error")), Is.EqualTo(("/", "4/0AQ-x", "s+t", "a b", (string?)null)));
        await request.AnswerAsync(200, "<p>Signed in. Ṣaḥīḥ.</p>");

        using var answer = await asked;
        Assert.That(answer.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(answer.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
        Assert.That(await answer.Content.ReadAsStringAsync(), Is.EqualTo("<p>Signed in. Ṣaḥīḥ.</p>"));
    }

    /// <summary>Each request is answered with its own status, and the next one waits its turn: a favicon, then the redirect.</summary>
    [Test]
    public async Task Each_request_gets_its_own_answer()
    {
        using var listener = Receiver().Open();
        using var browser = Browser();

        var favicon = browser.GetAsync(new Uri(listener.RedirectUri, "favicon.ico"));
        var first = await listener.NextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await first.AnswerAsync(404, "<p>Not here.</p>");
        var redirect = browser.GetAsync(new Uri(listener.RedirectUri, "?code=c&state=s"));
        var second = await listener.NextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await second.AnswerAsync(200, "<p>Done.</p>");

        Assert.That((first.Path, second.Path), Is.EqualTo(("/favicon.ico", "/")));
        Assert.That(((int)(await favicon).StatusCode, (int)(await redirect).StatusCode), Is.EqualTo((404, 200)));
    }

    /// <summary>A wait that is cancelled ends at once, and a closed listener frees its port: another listener can take it.</summary>
    [Test]
    public async Task A_cancelled_wait_ends_and_closing_frees_the_port()
    {
        var listener = Receiver().Open();
        var port = listener.RedirectUri.Port;
        using (var cancel = new CancellationTokenSource())
        {
            var waiting = listener.NextAsync(cancel.Token);
            await cancel.CancelAsync();
            Assert.That(async () => await waiting, Throws.InstanceOf<OperationCanceledException>());
        }

        listener.Dispose();

        var taken = new TcpListener(IPAddress.Loopback, port);
        Assert.That(() => taken.Start(), Throws.Nothing, "the port is free again");
        taken.Stop();
    }

    /// <summary>
    /// A browser that has gone before the page is sent, as a tab closed early (here with a reset, the harder case), leaves
    /// the sign-in nothing to catch but what the port allows: the listener passes the lost page over, or says so as an
    /// <see cref="IOException"/> or an <see cref="ObjectDisposedException"/> (the request let go), never as its own type.
    /// </summary>
    [Test]
    public async Task A_browser_gone_before_the_page_throws_nothing_the_sign_in_does_not_catch()
    {
        using var listener = Receiver().Open();
        ILoopbackRequest request;
        using (var tab = new TcpClient { LingerState = new LingerOption(true, 0) })
        {
            await tab.ConnectAsync(IPAddress.Loopback, listener.RedirectUri.Port);
            var wire = tab.GetStream();
            await wire.WriteAsync(Encoding.ASCII.GetBytes($"GET /?code=c&state=s HTTP/1.1\r\nHost: 127.0.0.1:{listener.RedirectUri.Port}\r\nConnection: close\r\n\r\n"));
            request = await listener.NextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        var read = (request.Path, request.Query("code"));
        var ended = "passed over";
        try
        {
            await request.AnswerAsync(200, "<p>Signed in.</p>").WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Deliberately not rethrown: these two types are what the port allows for a browser that has gone, and what the
            // sign-in catches; any other type fails the test.
            ended = ex.GetType().Name;
        }

        TestContext.Out.WriteLine($"The page for the browser that had gone was {ended}.");
        Assert.That(read, Is.EqualTo(("/", "c")), "the query was read before the answer, as the sign-in reads it");
    }

    /// <summary>Two listeners open at once get two ports.</summary>
    [Test]
    public void Two_listeners_get_two_ports()
    {
        using var first = Receiver().Open();
        using var second = Receiver().Open();

        Assert.That(second.RedirectUri.Port, Is.Not.EqualTo(first.RedirectUri.Port));
    }
}
