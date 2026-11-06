namespace ClassScribe.YouTube;

/// <summary>One word of a list of links, with where it stands, such as "line 3 of risala-links.txt" or "the command line".</summary>
/// <param name="Text">The word as written.</param>
/// <param name="Where">Where it stands, in words that follow "on" or start a sentence once capitalised.</param>
public sealed record LinkWord(string Text, string Where);
