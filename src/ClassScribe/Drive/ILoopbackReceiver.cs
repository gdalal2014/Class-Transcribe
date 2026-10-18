namespace ClassScribe.Drive;

/// <summary>
/// Opens the listener for Google's redirect at the end of a sign-in: on the loopback interface only, at
/// http://127.0.0.1:&lt;a free port&gt;/, as Google's guide for desktop apps and RFC 8252 advise ("listen on the loopback network
/// interface only"; 127.0.0.1 rather than localhost, which may reach the listener over IPv6). It is opened only for the
/// sign-in and closed as soon as Google's answer is in.
/// </summary>
public interface ILoopbackReceiver
{
    /// <summary>Opens a listener on a free port of 127.0.0.1. Throws when no port can be had.</summary>
    ILoopbackListener Open();
}

/// <summary>One listener for Google's redirect, open until disposed, which frees its port.</summary>
public interface ILoopbackListener : IDisposable
{
    /// <summary>The address Google sends the browser back to: http://127.0.0.1:&lt;port&gt;/.</summary>
    Uri RedirectUri { get; }

    /// <summary>Waits for the next request to the listener; cancelled by <paramref name="cancellationToken"/>.</summary>
    Task<ILoopbackRequest> NextAsync(CancellationToken cancellationToken);
}

/// <summary>One request to the listener, from the browser, with its path and query, waiting for its answer.</summary>
public interface ILoopbackRequest
{
    /// <summary>The path asked for, such as / for Google's redirect, or /favicon.ico.</summary>
    string Path { get; }

    /// <summary>The value of the query parameter <paramref name="name"/>, decoded; null when it is not there.</summary>
    string? Query(string name);

    /// <summary>Answers the browser with <paramref name="status"/> and the HTML page <paramref name="html"/>, and ends the request.</summary>
    Task AnswerAsync(int status, string html);
}
