namespace ClassScribe.Commands;

/// <summary>Where a command of its own kind, settings or class, writes what it answers: standard output and standard error.</summary>
/// <param name="Out">Standard output: the answer itself, such as a path, a value or JSON.</param>
/// <param name="Error">Standard error: warnings and refusals that are no part of the answer.</param>
public sealed record CommandOutput(TextWriter Out, TextWriter Error);
