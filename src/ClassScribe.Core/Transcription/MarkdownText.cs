using System.Text;

namespace ClassScribe.Transcription;

/// <summary>Text from elsewhere, such as a video's title, made safe to put into a Markdown transcript as it reads.</summary>
public static class MarkdownText
{
    /// <summary>The characters that would start emphasis, code, a link or HTML, or end a link's text, in Markdown.</summary>
    private const string Special = "\\`*_[]<>|";

    /// <summary>
    /// <paramref name="text"/> with a backslash before each of Markdown's own characters (\ ` * _ [ ] &lt; &gt; |), so that a title
    /// such as "Part *1* [draft]" reads as written in a heading and in a link's text, and line breaks made spaces, so that it
    /// stays on its line.
    /// </summary>
    public static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length + 8);
        foreach (var character in text)
        {
            if (character is '\r' or '\n')
            {
                escaped.Append(' ');
                continue;
            }

            if (Special.Contains(character)) escaped.Append('\\');
            escaped.Append(character);
        }

        return escaped.ToString();
    }
}
