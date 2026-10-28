namespace ClassScribe.Settings;

/// <summary>
/// The saved classes under Classes in the settings (layout-config 4.1 and 4.2; architecture 4.3), as the settings in force
/// hold them at each use, as the gate last accepted them. Each class is checked on its own (see <see cref="SavedClass.Check"/>):
/// a problem in one class makes only that class unusable, while the other classes and the rest of the file go on. Names are
/// looked up ignoring case, exactly, never by a prefix. What it gives are the saved classes' records (<see cref="SavedClassEntry"/>,
/// <see cref="SavedClass"/>) and a class's fields as text. A recorder's settings hold no saved classes, so a run keeps the
/// class it was started as in its request.
/// </summary>
public interface ISavedClasses
{
    /// <summary>Every saved class, in the order of their names, as the settings give them.</summary>
    IReadOnlyList<SavedClassEntry> All();

    /// <summary>The saved class named <paramref name="name"/>, ignoring case and spaces around it; null when there is none.</summary>
    SavedClassEntry? Find(string name);

    /// <summary>
    /// The saved class named <paramref name="name"/>, ignoring case and spaces around it, as the settings file holds it now,
    /// staged and checked as the reloader does, each class on its own: for a hint on the command line, given before the
    /// gate reads the file for a recording, while the settings in force hold none of the file's values. Null when there is
    /// none, when there is no file to read, and when the file cannot be read now or is not valid JSON.
    /// </summary>
    SavedClassEntry? FindInFile(string name);

    /// <summary>
    /// The fields of the saved class named <paramref name="name"/>, ignoring case, as the settings hold them, by name in any
    /// case and fields this build does not know included, for options given over it to be laid on; null when there is none.
    /// </summary>
    Dictionary<string, string?>? FieldsOf(string name);

    /// <summary>
    /// The saved classes a typed name may mean, as menu choices 1 and 2 offer them: the class of that name, else those whose
    /// course it is, ignoring case.
    /// </summary>
    IReadOnlyList<SavedClassEntry> Matching(string typed);
}
