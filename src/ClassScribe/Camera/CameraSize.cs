using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClassScribe.Camera;

/// <summary>
/// The size of the camera's picture (amendment 39): one of the three sizes the probe measures, 480p (848x480), 720p
/// (1280x720, the default) and 1080p (1920x1080), or a width and a height typed as 1024x576. One parser,
/// <see cref="TryParse"/>, serves the setting (Recording:CameraResolution), the command line (--resolution) and the menu, so
/// that all three take and refuse the same sizes. A typed size between the three was not measured by the probe; the camera
/// records the closest it offers.
/// </summary>
/// <param name="Width">The picture's width in pixels, even, from 320 to 1920.</param>
/// <param name="Height">The picture's height in pixels, even, from 240 to 1080.</param>
public sealed partial record CameraSize(int Width, int Height)
{
    /// <summary>The word for the default size, as the setting, the menu and a run file write it.</summary>
    public const string DefaultText = "720p";

    /// <summary>
    /// The rule in words, for every refusal: what may be written, and the limits of a typed size. The encoder's frames need
    /// even sides, and a shape outside 4:3 to 16:9 is no camera's.
    /// </summary>
    public const string Rule =
        "480p, 720p or 1080p, or a width and a height such as 1024x576: the width from 320 to 1920 and the height from 240 to 1080, both even, in a shape from 4:3 to 16:9";

    /// <summary>The smallest width a typed size may have.</summary>
    public const int SmallestWidth = 320;

    /// <summary>The largest width a typed size may have.</summary>
    public const int LargestWidth = 1920;

    /// <summary>The smallest height a typed size may have.</summary>
    public const int SmallestHeight = 240;

    /// <summary>The largest height a typed size may have.</summary>
    public const int LargestHeight = 1080;

    /// <summary>480p: 848x480, about 1.2 GB an hour with the sound.</summary>
    public static CameraSize P480 { get; } = new(848, 480);

    /// <summary>720p: 1280x720, the default, about 2.3 GB an hour with the sound.</summary>
    public static CameraSize P720 { get; } = new(1280, 720);

    /// <summary>1080p: 1920x1080, about 3.7 GB an hour with the sound.</summary>
    public static CameraSize P1080 { get; } = new(1920, 1080);

    /// <summary>The size used when nothing chooses one: 720p.</summary>
    public static CameraSize Default => P720;

    /// <summary>The three sizes by their words, in the order the menu offers them.</summary>
    public static IReadOnlyList<(string Word, CameraSize Size)> Presets { get; } = [("480p", P480), ("720p", P720), ("1080p", P1080)];

    /// <summary>The size as it is written: its word for one of the three, else its width and height, such as 1024x576.</summary>
    public string Text => Presets.FirstOrDefault(preset => preset.Size == this).Word ?? string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}");

    /// <summary>
    /// Reads <paramref name="text"/> as a picture size: 480p, 720p or 1080p in any case, or a width and a height joined by x
    /// or X, such as 1024x576, within the limits of <see cref="Rule"/>; spaces around it are allowed. False for anything
    /// else, which the caller refuses with <see cref="Rule"/>.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out CameraSize? size)
    {
        size = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        foreach (var (word, preset) in Presets)
        {
            if (!word.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) continue;

            size = preset;
            return true;
        }

        var match = Typed().Match(trimmed);
        if (!match.Success
            || !int.TryParse(match.Groups["width"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(match.Groups["height"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var height))
            return false;

        if (width is < SmallestWidth or > LargestWidth || height is < SmallestHeight or > LargestHeight) return false;
        if (width % 2 != 0 || height % 2 != 0) return false;

        // From 4:3 (width = 4/3 of the height) to 16:9 (width = 16/9 of the height), the ends included, in whole numbers.
        if (3L * width < 4L * height || 9L * width > 16L * height) return false;

        size = new CameraSize(width, height);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Text;

    /// <summary>A typed size: digits, x or X, digits; 0 to 9 only, and \z ends the match at the very end.</summary>
    [GeneratedRegex(@"^(?<width>[0-9]{1,5})[xX](?<height>[0-9]{1,5})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Typed();
}
