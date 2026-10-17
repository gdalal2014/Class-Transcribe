using System.CommandLine;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The command line put together for a test as the app puts it together: the tree over the commands the test gives, with
/// the real menu over a scripted console, settings of the test's own and a clock the test chooses. What the tree writes is
/// kept, the help and the rest in <see cref="Output"/> and the mistakes in <see cref="Errors"/>, as standard output and
/// standard error would receive them.
/// </summary>
public sealed class TestCommandLine
{
    /// <summary>
    /// Puts the command line together over <paramref name="commands"/>, with <paramref name="ui"/> as the console, the
    /// settings in <paramref name="settings"/> read through <paramref name="gate"/> (or a gate of their own), recordings
    /// under <paramref name="recordingsFolder"/>, and <paramref name="time"/> as the clock (or the system's).
    /// </summary>
    public TestCommandLine(ICommandRunner commands, FakeConsoleUi ui, TestSettings settings, string recordingsFolder, TimeProvider? time = null, RunGate? gate = null)
    {
        Time = time ?? TimeProvider.System;
        Gate = gate ?? settings.Gate();
        var recording = TestOptions.Create(new RecordingOptions { OutputFolder = recordingsFolder });
        var folders = new ClassFolders(recording, Time, NullLogger<ClassFolders>.Instance);
        Menu = new MenuCommand(commands, ui, Gate, settings.File, TestOptions.Create(new ElevenLabsOptions { ApiKey = "test-key" }), folders, new FakeCaptureProvider(),
            new SourceChoice(recording), new RecordingNamePrompt(ui), Mock.Of<IDesktop>(), Time, NullLogger<MenuCommand>.Instance);
        Tree = new CommandLineTree(commands, Menu, ui, Time, Output, Errors);
    }

    /// <summary>What the tree wrote as standard output: the help, and the usage shown when the menu cannot be.</summary>
    public StringWriter Output { get; } = new();

    /// <summary>What the tree wrote as standard error: the mistakes on the command line, one per line.</summary>
    public StringWriter Errors { get; } = new();

    /// <summary>The clock the tree stamps run ids with.</summary>
    public TimeProvider Time { get; }

    /// <summary>The gate the menu reads the settings through before each choice.</summary>
    public RunGate Gate { get; }

    /// <summary>The menu the tree shows when there are no arguments.</summary>
    public MenuCommand Menu { get; }

    /// <summary>The command line under test.</summary>
    public CommandLineTree Tree { get; }

    /// <summary>
    /// The parse result <see cref="RunAsync"/> ran last, which keeps the invocation configuration it was run with; null
    /// until a command line without a mistake has been run.
    /// </summary>
    public ParseResult? LastRun { get; private set; }

    /// <summary>The mistakes written so far, one per line, without the empty lines.</summary>
    public IReadOnlyList<string> Mistakes => [.. Errors.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// Runs <paramref name="args"/> as App does, without the settings check and the report at the start: tidies them, parses
    /// them, and runs what they name unless they hold a mistake, whose exit code is returned instead.
    /// </summary>
    public async Task<int> RunAsync(params string[] args)
    {
        var outcome = Tree.Parse(Tree.Tidy(args));
        if (outcome.ExitCode is { } mistake) return mistake;

        LastRun = outcome.Parsed!;
        return await Tree.InvokeAsync(LastRun);
    }

    /// <summary>The kind of command <paramref name="args"/> name, once tidied and parsed; they must hold no mistake.</summary>
    public CommandKind KindOf(params string[] args)
    {
        var outcome = Tree.Parse(Tree.Tidy(args));
        Assert.That(outcome.ExitCode, Is.Null, $"the command line holds no mistake: {Errors}");
        return Tree.KindOf(outcome.Parsed!);
    }
}
