namespace PLCSIM.UnitTest.Common.Identifiers;

/// <summary>
/// Identifies a prepared UnitTest communication session.
/// </summary>
public readonly record struct CommunicationSessionId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}
