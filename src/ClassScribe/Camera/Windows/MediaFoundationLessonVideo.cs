using System.Runtime.Versioning;

namespace ClassScribe.Camera.Windows;

/// <summary>A lesson's video made with Media Foundation (see <see cref="ILessonVideoMedia"/>): not written yet in this commit.</summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationLessonVideo : ILessonVideoMedia
{
    /// <inheritdoc />
    public VideoSourceFacts Describe(string path) => throw new NotSupportedException("Making the lesson's video is not written yet.");

    /// <inheritdoc />
    public void Write(LessonVideoPlan plan, Audio.MixedSound sound, string path, Action<double> progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Making the lesson's video is not written yet.");

    /// <inheritdoc />
    public string? Check(string path, LessonVideoCheck check) => throw new NotSupportedException("Making the lesson's video is not written yet.");
}
