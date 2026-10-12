using GreedyNose.Api.EnableBanking;

namespace GreedyNose.Api.Tests;

/// <summary>An in-memory <see cref="IConsentPersistence"/>: one snapshot per (user, bank), like the real table. Can be told to fail.</summary>
internal sealed class FakeConsentPersistence : IConsentPersistence
{
    private readonly Dictionary<(Guid, string), ConsentSnapshot> _rows = [];

    public Exception? SaveFails { get; set; }

    public Exception? LoadFails { get; set; }

    public int SaveCalls { get; private set; }

    public Task SaveAsync(ConsentSnapshot snapshot, CancellationToken ct = default)
    {
        SaveCalls++;
        if (SaveFails is not null)
        {
            throw SaveFails;
        }

        _rows[(snapshot.UserId, snapshot.AspspName ?? "")] = snapshot;
        return Task.CompletedTask;
    }

    public Task<string?> LoadSessionIdAsync(Guid userId, string aspspName, CancellationToken ct = default) =>
        Task.FromResult(_rows.TryGetValue((userId, aspspName), out var row) ? row.SessionId : null);

    public Task<IReadOnlyList<ConsentSnapshot>> LoadAsync(CancellationToken ct = default)
    {
        if (LoadFails is not null)
        {
            throw LoadFails;
        }

        return Task.FromResult<IReadOnlyList<ConsentSnapshot>>([.. _rows.Values]);
    }
}
