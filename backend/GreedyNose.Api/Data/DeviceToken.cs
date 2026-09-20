namespace GreedyNose.Api.Data;

/// <summary>The FCM registration token of one app install — where a push for this user is sent.</summary>
public sealed class DeviceToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Unique across all users: a token identifies one install, so registering it again is an update, never a second row.</summary>
    public required string Token { get; set; }

    /// <summary>FCM rotates tokens, so when we last heard this one is what tells a live token from a stale one.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
