// Votes plugin: the database side.
//
// Two databases on the same MySQL server, through the trusted plugin connection (IPluginContext.Data):
//   - TomYum's own database (Database:Name, `tomyum` in production) is the connection's default; the
//     plugin keeps its vote counts and each voter's osu!direct time there in `plugin_votes`, created
//     (and given new columns) when it's missing. The website reads it too (the vote page, and its
//     osu!direct button for voters).
//   - The website's database (`osu`, config osuWebDatabase) holds the accounts. osu!supporter is
//     phpbb_users.osu_subscriber + osu_subscriptionexpiry, set by the website's store; votes don't touch
//     it any more (they give osu!direct only), but the plugin still ends supporter whose time is up.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.Votes;

/// <summary>An account a vote can go to.</summary>
/// <param name="UserId">The player's user id.</param>
/// <param name="Username">The player's current name.</param>
public sealed record VoteUser(long UserId, string Username);

/// <summary>The outcome of one counted vote.</summary>
/// <param name="Votes">The player's votes, this one included.</param>
/// <param name="DirectEndsUtc">When their osu!direct ends now (UTC), or null when votes don't give any (daysPerVote 0).</param>
public sealed record VoteResult(int Votes, DateTime? DirectEndsUtc);

/// <summary>A player's votes, osu!direct and supporter, for <c>!vote</c>.</summary>
/// <param name="Votes">Votes counted so far.</param>
/// <param name="IsSupporter">Whether they have osu!supporter right now.</param>
/// <param name="SupporterEndsUtc">When it ends (UTC), or null when it has no end date or they have none.</param>
/// <param name="DirectEndsUtc">When their voted osu!direct ends (UTC), or null when they have none left.</param>
public sealed record VoteStatus(int Votes, bool IsSupporter, DateTime? SupporterEndsUtc, DateTime? DirectEndsUtc);

/// <summary>Where votes and supporter are read and written.</summary>
public interface IVoteStore
{
    /// <summary>Finds the account OSL names (as a player types it: any case, spaces or underscores).</summary>
    /// <param name="username">The name from the callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account, or null when there's none (deleted accounts included).</returns>
    Task<VoteUser?> FindUserAsync(string username, CancellationToken cancellationToken);

    /// <summary>Counts a vote and adds <paramref name="directDays"/> days of osu!direct, in one transaction.</summary>
    /// <param name="userId">The voter.</param>
    /// <param name="directDays">Days of osu!direct per vote; 0 counts the vote only.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new count and when their osu!direct ends.</returns>
    Task<VoteResult> RecordVoteAsync(long userId, int directDays, CancellationToken cancellationToken);

    /// <summary>Whether the player has voted osu!direct time left.</summary>
    /// <param name="userId">The player.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True while their osu!direct lasts.</returns>
    Task<bool> HasDirectAsync(long userId, CancellationToken cancellationToken);

    /// <summary>A player's votes and supporter.</summary>
    /// <param name="userId">The player.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Their status; 0 votes and no supporter for an unknown id.</returns>
    Task<VoteStatus> GetStatusAsync(long userId, CancellationToken cancellationToken);

    /// <summary>Takes supporter off everyone whose supporter end date has passed (nothing else in the stack does).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user ids whose supporter ended in this pass.</returns>
    Task<IReadOnlyList<long>> ExpireSupporterAsync(CancellationToken cancellationToken);
}

/// <summary><see cref="IVoteStore"/> over the trusted MySQL connection.</summary>
public sealed class MySqlVoteStore : IVoteStore
{
    /// <summary>The plugin's table, in TomYum's database (the connection's default database).</summary>
    public const string VotesTable = "plugin_votes";

    /// <summary>At most this many expired supporters are handled per pass (the rest go next pass).</summary>
    public const int ExpireBatch = 500;

    private const string CreateTableSql =
        "CREATE TABLE IF NOT EXISTS `" + VotesTable + "` ("
        + " `user_id` INT UNSIGNED NOT NULL,"
        + " `votes` INT UNSIGNED NOT NULL DEFAULT 0,"
        + " `first_voted_at` DATETIME NOT NULL,"
        + " `last_voted_at` DATETIME NOT NULL,"
        + " `direct_until` DATETIME NULL,"
        + " PRIMARY KEY (`user_id`)"
        + ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4";

    private readonly IPluginDataAccess _data;
    private readonly string _users;
    private volatile bool _schemaReady;

    /// <summary>Creates the store.</summary>
    /// <param name="data">The trusted connection (default database: TomYum's).</param>
    /// <param name="osuWebDatabase">The website's database name (checked by the plugin to be a plain name).</param>
    public MySqlVoteStore(IPluginDataAccess data, string osuWebDatabase)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(osuWebDatabase);
        _data = data;
        _users = "`" + osuWebDatabase + "`.`phpbb_users`";
    }

    /// <inheritdoc/>
    public async Task<VoteUser?> FindUserAsync(string username, CancellationToken cancellationToken)
    {
        var candidates = VoteNames.CleanCandidates(username);
        if (candidates.Count == 0)
            return null;

        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            // username_clean is osu-web's lower-cased name. An exact match wins over a spaces/underscores one.
            var command = Command(
                connection,
                null,
                $"SELECT user_id, username FROM {_users} WHERE username_clean IN (@a, @b, @c) AND user_type <> 1 "
                + "ORDER BY (username_clean = @a) DESC, user_id LIMIT 1",
                ("@a", candidates[0]),
                ("@b", candidates[Math.Min(1, candidates.Count - 1)]),
                ("@c", candidates[Math.Min(2, candidates.Count - 1)]));
            await using (command.ConfigureAwait(false))
            {
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        return null;
                    return new VoteUser(
                        Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                        Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<VoteResult> RecordVoteAsync(long userId, int directDays, CancellationToken cancellationToken)
    {
        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            // CREATE TABLE commits on its own, so it goes before the transaction.
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var exists = await ScalarAsync(connection, transaction, $"SELECT 1 FROM {_users} WHERE user_id = @id", cancellationToken, ("@id", userId))
                    .ConfigureAwait(false);
                if (exists is null)
                    throw new InvalidOperationException($"user {userId} doesn't exist");

                // The days go on top of the osu!direct time left, or start from now when it has run out.
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"INSERT INTO `{VotesTable}` (user_id, votes, first_voted_at, last_voted_at, direct_until) "
                    + "VALUES (@id, 1, UTC_TIMESTAMP(), UTC_TIMESTAMP(), IF(@days > 0, DATE_ADD(UTC_TIMESTAMP(), INTERVAL @days DAY), NULL)) "
                    + "ON DUPLICATE KEY UPDATE votes = votes + 1, last_voted_at = UTC_TIMESTAMP(), direct_until = IF(@days > 0, "
                    + "DATE_ADD(GREATEST(COALESCE(direct_until, UTC_TIMESTAMP()), UTC_TIMESTAMP()), INTERVAL @days DAY), direct_until)",
                    cancellationToken,
                    ("@id", userId),
                    ("@days", directDays)).ConfigureAwait(false);

                int votes;
                DateTime? directEnds;
                var command = Command(connection, transaction, $"SELECT votes, direct_until FROM `{VotesTable}` WHERE user_id = @id", ("@id", userId));
                await using (command.ConfigureAwait(false))
                {
                    var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                        votes = ToInt32(reader.GetValue(0));
                        directEnds = ToDateTime(reader.GetValue(1));
                    }
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new VoteResult(votes, directDays > 0 ? directEnds : null);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<bool> HasDirectAsync(long userId, CancellationToken cancellationToken)
    {
        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            var found = await ScalarAsync(
                connection,
                null,
                $"SELECT 1 FROM `{VotesTable}` WHERE user_id = @id AND direct_until > UTC_TIMESTAMP()",
                cancellationToken,
                ("@id", userId)).ConfigureAwait(false);
            return found is not null;
        }
    }

    /// <inheritdoc/>
    public async Task<VoteStatus> GetStatusAsync(long userId, CancellationToken cancellationToken)
    {
        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

            var command = Command(
                connection,
                null,
                $"SELECT u.osu_subscriber, u.osu_subscriptionexpiry, UTC_TIMESTAMP(), COALESCE(v.votes, 0), v.direct_until FROM {_users} u "
                + $"LEFT JOIN `{VotesTable}` v ON v.user_id = u.user_id WHERE u.user_id = @id",
                ("@id", userId));
            await using (command.ConfigureAwait(false))
            {
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        return new VoteStatus(0, false, null, null);

                    var subscriber = Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture);
                    var expiry = ToDateTime(reader.GetValue(1));
                    var now = ToDateTime(reader.GetValue(2)) ?? DateTime.UtcNow;
                    var votes = ToInt32(reader.GetValue(3));
                    var active = subscriber && (expiry is null || expiry > now);
                    var direct = ToDateTime(reader.GetValue(4));
                    return new VoteStatus(votes, active, active ? expiry : null, direct > now ? direct : null);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<long>> ExpireSupporterAsync(CancellationToken cancellationToken)
    {
        const string Due = "osu_subscriber = 1 AND osu_subscriptionexpiry IS NOT NULL AND osu_subscriptionexpiry <= UTC_TIMESTAMP()";

        var expired = new List<long>();
        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var due = new List<long>();
            var select = Command(connection, null, $"SELECT user_id FROM {_users} WHERE {Due} LIMIT {ExpireBatch}");
            await using (select.ConfigureAwait(false))
            {
                var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        due.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
                }
            }

            // One row at a time, checked again: a purchase or vote in between moved the end date later.
            foreach (var userId in due)
            {
                var changed = await ExecuteAsync(
                    connection,
                    null,
                    $"UPDATE {_users} SET osu_subscriber = 0 WHERE user_id = @id AND {Due}",
                    cancellationToken,
                    ("@id", userId)).ConfigureAwait(false);
                if (changed > 0)
                    expired.Add(userId);
            }
        }

        return expired;
    }

    private async Task EnsureSchemaAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (_schemaReady)
            return;

        await ExecuteAsync(connection, null, CreateTableSql, cancellationToken).ConfigureAwait(false);

        // Tables made before votes gave osu!direct don't have its column yet.
        var hasDirect = await ScalarAsync(
            connection,
            null,
            "SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = 'direct_until'",
            cancellationToken,
            ("@table", VotesTable)).ConfigureAwait(false);
        if (hasDirect is null)
            await ExecuteAsync(connection, null, $"ALTER TABLE `{VotesTable}` ADD COLUMN `direct_until` DATETIME NULL", cancellationToken).ConfigureAwait(false);

        _schemaReady = true;
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // only fixed SQL and a database name checked to be [A-Za-z0-9_]; values are parameters
        command.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static async Task<int> ExecuteAsync(
        DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        var command = Command(connection, transaction, sql, parameters);
        await using (command.ConfigureAwait(false))
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(
        DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        var command = Command(connection, transaction, sql, parameters);
        await using (command.ConfigureAwait(false))
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int ToInt32(object? value) =>
        value is null || value is DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    // osu-web stores UTC; MySqlConnector hands DATETIME back without a kind.
    private static DateTime? ToDateTime(object? value) =>
        value is DateTime time ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : null;
}

/// <summary>osu-web's name rules, for finding the account a vote is for.</summary>
public static class VoteNames
{
    /// <summary>Longest name looked up (osu-web's names are much shorter).</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// osu-web's <c>username_clean</c> for <paramref name="username"/> (PHP <c>strtolower</c>: ASCII
    /// only), then the same with underscores read as spaces and spaces as underscores. Empty for a
    /// blank or overlong name.
    /// </summary>
    /// <param name="username">The name as given.</param>
    /// <returns>Up to three distinct candidates, the exact one first.</returns>
    public static IReadOnlyList<string> CleanCandidates(string? username)
    {
        var trimmed = username?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > MaxLength)
            return Array.Empty<string>();

        var chars = trimmed.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'A' and <= 'Z')
                chars[i] = (char)(chars[i] + ('a' - 'A'));
        }

        var clean = new string(chars);
        var result = new List<string> { clean };
        foreach (var other in new[] { clean.Replace('_', ' '), clean.Replace(' ', '_') })
        {
            if (!result.Contains(other))
                result.Add(other);
        }

        return result;
    }
}
