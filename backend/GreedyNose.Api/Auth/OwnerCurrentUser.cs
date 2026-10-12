using GreedyNose.Api.Data;

namespace GreedyNose.Api.Auth;

/// <summary>
/// The only user there is today: the owner (the seeded user). Per-user sign-in replaces this class
/// with one that reads the request. The endpoints that serve the app go through <see cref="ICurrentUser"/>;
/// the consent start page still uses <see cref="SeedData.UserId"/>; the worker takes its user from the consent (Story 3.1).
/// </summary>
public sealed class OwnerCurrentUser : ICurrentUser
{
    public Guid UserId => SeedData.UserId;
}
