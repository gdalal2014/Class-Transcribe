namespace ClassScribe.YouTube;

/// <summary>A video's title and channel, as YouTube's oEmbed answer gives them.</summary>
/// <param name="Title">The title, without the spaces around it.</param>
/// <param name="Channel">The channel's name, when given.</param>
/// <param name="ChannelHandle">The channel's @handle, when its address names one.</param>
/// <param name="ChannelUrl">The channel's address, when given.</param>
public sealed record VideoTitle(string Title, string? Channel, string? ChannelHandle, string? ChannelUrl);
