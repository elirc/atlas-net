namespace Atlas.Domain;

/// <summary>Thrown when a domain invariant or lifecycle rule is violated.</summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }
}
