namespace GreedyNose.Api.Auth;

/// <summary>
/// The one place that answers "which user is this request for?". Endpoints take this instead of
/// naming a user themselves, so per-user sign-in later changes the implementation, not the endpoints.
/// </summary>
public interface ICurrentUser
{
    /// <summary>Never <see cref="Guid.Empty"/>; intended to be the id of an existing user row.</summary>
    Guid UserId { get; }
}
