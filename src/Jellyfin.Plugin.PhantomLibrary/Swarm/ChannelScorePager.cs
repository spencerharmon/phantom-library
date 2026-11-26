using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.PhantomLibrary.Swarm;

/// <summary>One persisted channel BaseItem as seen by the score sweep (no hydration).</summary>
public sealed record ChannelScoreRow(Guid Id, string? ExternalId, float? CriticRating);

/// <summary>
/// One keyset partition of the score sweep: the persisted BaseItems of one Jellyfin
/// item type under one phantom channel (channel items carry the channel as
/// <c>TopParentId</c>).
/// </summary>
public sealed record ChannelScorePhase(string Name, Guid ChannelId, string ItemType);

/// <summary>
/// Bounded keyset reader over the persisted phantom channel BaseItems
/// (p05-baseitem-writeback). One call returns at most <c>limit</c> rows with
/// <c>Id &gt; afterId</c> in <c>Id</c> order, so a full sweep is resumable from the last
/// id and no single call ever touches more than one batch.
/// </summary>
public interface IChannelScorePager
{
    /// <summary>Human-readable access path the reader uses (logged once per sweep).</summary>
    string AccessPath { get; }

    /// <summary>Next page of <paramref name="phase"/> after <paramref name="afterId"/> (null = from the start).</summary>
    Task<IReadOnlyList<ChannelScoreRow>> NextPageAsync(ChannelScorePhase phase, Guid? afterId, int limit, CancellationToken ct);
}

/// <summary>
/// <see cref="IChannelScorePager"/> over Jellyfin's own database (SQLite by default,
/// PostgreSQL via the Jellyfin.Pgsql provider). Reads three columns per row with a
/// keyset seek on Jellyfin's <c>IX_BaseItems_Type_TopParentId_Id</c> index:
/// <c>Type = ? AND TopParentId = ? AND Id &gt; ? ORDER BY Id LIMIT ?</c>. The
/// <c>ChannelId</c> equality is a residual guard so a non-channel item can never be
/// touched even if it shared a top parent. Writes still go through
/// <c>ILibraryManager.UpdateItemsAsync</c>; this class only reads.
/// </summary>
public sealed class JellyfinChannelScorePager : IChannelScorePager
{
    /// <summary>The exact keyset statement (logged so the rig can EXPLAIN it).</summary>
    public const string KeysetSql =
        "SELECT \"Id\", \"ExternalId\", \"CriticRating\" FROM \"BaseItems\" "
        + "WHERE \"Type\" = @type AND \"TopParentId\" = @top AND \"ChannelId\" = @top AND \"Id\" > @after "
        + "ORDER BY \"Id\" LIMIT @limit";

    private readonly IDbContextFactory<JellyfinDbContext> _contextFactory;

    public JellyfinChannelScorePager(IDbContextFactory<JellyfinDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc />
    public string AccessPath => "keyset(Type,TopParentId,Id) via IX_BaseItems_Type_TopParentId_Id: " + KeysetSql;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChannelScoreRow>> NextPageAsync(ChannelScorePhase phase, Guid? afterId, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(phase);
        var rows = new List<ChannelScoreRow>(Math.Max(0, limit));
        if (limit <= 0)
        {
            return rows;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var conn = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // SQLite (EF default) stores Guids as upper-case 'D' TEXT; the PostgreSQL
            // provider stores native uuid. Bind each in its own representation so the
            // comparison stays a sargable index seek on either backend.
            var textGuids = !conn.GetType().FullName!.StartsWith("Npgsql", StringComparison.Ordinal);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = KeysetSql;
            Add(cmd, "@type", phase.ItemType);
            Add(cmd, "@top", textGuids ? GuidText(phase.ChannelId) : phase.ChannelId);
            var after = afterId ?? Guid.Empty;
            Add(cmd, "@after", textGuids ? GuidText(after) : after);
            Add(cmd, "@limit", limit);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var idValue = r.GetValue(0);
                var id = idValue is Guid g ? g : Guid.Parse(Convert.ToString(idValue, CultureInfo.InvariantCulture)!);
                var external = r.IsDBNull(1) ? null : r.GetString(1);
                float? rating = r.IsDBNull(2) ? null : Convert.ToSingle(r.GetValue(2), CultureInfo.InvariantCulture);
                rows.Add(new ChannelScoreRow(id, external, rating));
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return rows;
    }

    private static string GuidText(Guid g) => g.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant();

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
