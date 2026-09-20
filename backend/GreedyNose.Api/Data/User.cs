namespace GreedyNose.Api.Data;

/// <summary>
/// Only an anchor for the foreign keys — no auth fields until there is a login. Every other table
/// carries a <c>UserId</c> and keys on it, so adding real users later needs no re-keying.
/// </summary>
public sealed class User
{
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
