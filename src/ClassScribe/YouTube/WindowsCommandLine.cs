using System.Text;

namespace ClassScribe.YouTube;

/// <summary>Writes a command line Windows programs read back as the arguments given (the rules of CommandLineToArgvW and the C runtime).</summary>
internal static class WindowsCommandLine
{
    /// <summary>The program, quoted, then each argument, quoted where it needs it, separated by spaces.</summary>
    public static string Of(string program, IEnumerable<string> arguments) => string.Join(' ', [$"\"{program}\"", .. arguments.Select(Quote)]);

    /// <summary>
    /// <paramref name="argument"/> as it must be written on a command line to be read back as it is: as it is when it holds no
    /// space, tab, line break or quote and is not empty; otherwise in quotes, with each quote escaped by a backslash and the
    /// backslashes before a quote, or before the closing quote, doubled.
    /// </summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return argument;

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            quoted.Append(character);
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
