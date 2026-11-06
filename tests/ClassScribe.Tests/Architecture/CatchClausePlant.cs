namespace ClassScribe.Tests;

/// <summary>
/// A type whose one mention of <see cref="DivideByZeroException"/> is a catch clause without a variable, which leaves the
/// type's name in the method's exception regions alone, in no IL operand and no local: by it
/// <see cref="ArchitectureTests.The_reader_sees_the_type_a_catch_clause_names"/> pins that the architecture test's reader
/// looks there, so that a use case catching a vendor's or the network's exception cannot slip past check 2. Nothing calls it.
/// </summary>
public static class CatchClausePlant
{
    /// <summary>Runs <paramref name="action"/>, answering false when it divides by zero.</summary>
    public static bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (DivideByZeroException)
        {
            // Deliberately not rethrown: the plant exists for this catch clause alone, which the reader must see.
            return false;
        }
    }
}
