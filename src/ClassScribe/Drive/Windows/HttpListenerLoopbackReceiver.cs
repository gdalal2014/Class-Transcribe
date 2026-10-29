using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Drive.Windows;

/// <summary>
/// The listener for Google's redirect, on .NET's <see cref="HttpListener"/> at http://127.0.0.1:&lt;port&gt;/. A free port is
/// found by binding port 0 on the loopback address and letting it go; should another program take it in between, another is
/// tried, up to <see cref="Attempts"/> times. Checked on this laptop: a normal user (no administrator token) can listen on
/// http://127.0.0.1:&lt;port&gt;/ without a URL reservation. Should a future Windows or security product refuse it, a
/// <see cref="TcpListener"/> works as well, behind the same <see cref="ILoopbackReceiver"/>.
/// </summary>
public sealed class HttpListenerLoopbackReceiver(ILogger<HttpListenerLoopbackReceiver> logger) : ILoopbackReceiver
{
    /// <summary>How many ports are tried before giving up.</summary>
    public const int Attempts = 5;

    /// <inheritdoc />
    public ILoopbackListener Open()
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener { IgnoreWriteExceptions = true };
            try
            {
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                logger.LogDebug("Listening for Google's answer on 127.0.0.1:{Port}", port);
                return new Listener(listener, port);
            }
            catch (HttpListenerException ex) when (attempt < Attempts)
            {
                // Deliberately not rethrown: another program took the port between the check and the start; another port is
                // tried, up to five times, and the last failure is thrown as it is.
                listener.Close();
                logger.LogDebug(ex, "Port {Port} on 127.0.0.1 could not be taken; trying another", port);
            }
            catch (Exception ex)
            {
                listener.Close();
                logger.LogError(ex, "Could not listen on 127.0.0.1 for Google's answer");
                throw;
            }
        }
    }

    /// <summary>A port of 127.0.0.1 that is free now: bound with port 0, read, and let go.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>One open listener. <see cref="HttpListener.GetContextAsync"/> cannot be cancelled, so a wait that is cancelled leaves it pending for the next wait, and closing the listener ends it.</summary>
    private sealed class Listener(HttpListener listener, int port) : ILoopbackListener
    {
        /// <summary>The request being waited for, kept across a cancelled wait; null when none is.</summary>
        private Task<HttpListenerContext>? _pending;

        /// <inheritdoc />
        public Uri RedirectUri { get; } = new($"http://127.0.0.1:{port}/");

        /// <inheritdoc />
        public async Task<ILoopbackRequest> NextAsync(CancellationToken cancellationToken)
        {
            _pending ??= listener.GetContextAsync();
            var context = await _pending.WaitAsync(cancellationToken);
            _pending = null;
            return new Request(context);
        }

        /// <summary>Closes the listener, which frees the port, and lets a request still waited for end quietly.</summary>
        public void Dispose()
        {
            listener.Close();

            // The wait ends with the listener closed; its failure is observed here, so it is never reported as unobserved.
            _pending?.ContinueWith(static ended => _ = ended.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    /// <summary>One request from the browser.</summary>
    private sealed class Request(HttpListenerContext context) : ILoopbackRequest
    {
        /// <inheritdoc />
        public string Path => context.Request.Url?.AbsolutePath ?? "/";

        /// <inheritdoc />
        public string? Query(string name) => context.Request.QueryString[name];

        /// <inheritdoc />
        public async Task AnswerAsync(int status, string html)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(html);
                var response = context.Response;
                response.StatusCode = status;
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                response.KeepAlive = false;
                response.Headers["Cache-Control"] = "no-store";
                await response.OutputStream.WriteAsync(bytes);
                response.Close();
            }
            catch (HttpListenerException ex)
            {
                // Handed on as the port says it: a browser that has gone is an IOException to the sign-in, which names no
                // listener of its own. (With IgnoreWriteExceptions the listener passes a page the browser did not take over
                // and lets the request go, throwing nothing here; the port allows that, this IOException, and an
                // ObjectDisposedException for a request let go; LoopbackReceiverTests pins that nothing else comes out.)
                throw new IOException(ex.Message, ex);
            }
        }
    }
}
