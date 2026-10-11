using System;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MySqlConnector;

using TomYum.Plugins.Abstractions;
using TomYum.Plugins.Votes;

using Xunit;

namespace TomYum.Votes.Tests;

/// <summary>Runs only when TOMYUM_VOTES_TEST_MYSQL is set (a connection string to a throwaway MySQL server).</summary>
public sealed class MySqlFactAttribute : FactAttribute
{
    public const string Variable = "TOMYUM_VOTES_TEST_MYSQL";

    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"set {Variable} to a throwaway MySQL server's connection string to run this";
    }
}

/// <summary>
/// The SQL against a real MySQL. Each test makes its own two databases (a stand-in for TomYum's and
/// osu-web's, with phpbb_users' supporter and name columns) and drops them afterwards.
/// NEVER point TOMYUM_VOTES_TEST_MYSQL at the production server.
/// </summary>
public sealed class MySqlVoteStoreTests
{
    [MySqlFact]
    public async Task Finds_players_like_osu_web()
    {
        await using var db = await TestDatabases.CreateAsync();
        await db.ExecAsync("INSERT INTO {osu}.phpbb_users VALUES (5, 'blueskychan_', 'blueskychan_', 0, 0, NULL), (8, 'Some Player', 'some player', 0, 0, NULL), (9, 'gone', 'gone', 1, 0, NULL)");
        var store = db.Store();

        Assert.Equal(5, (await store.FindUserAsync("BlueSkyChan_", default))?.UserId);
        Assert.Equal(8, (await store.FindUserAsync("some_player", default))?.UserId);
        Assert.Equal("Some Player", (await store.FindUserAsync("SOME PLAYER", default))?.Username);
        Assert.Null(await store.FindUserAsync("gone", default));
        Assert.Null(await store.FindUserAsync("nobody", default));
    }

    [MySqlFact]
    public async Task Votes_count_and_stack_osu_direct_and_leave_supporter_alone()
    {
        await using var db = await TestDatabases.CreateAsync();
        await db.ExecAsync(
            "INSERT INTO {osu}.phpbb_users VALUES "
            + "(1, 'new', 'new', 0, 0, NULL), "
            + "(2, 'bought', 'bought', 0, 1, UTC_TIMESTAMP() + INTERVAL 30 DAY)");
        var store = db.Store();
        var bought = await db.ScalarDateAsync("SELECT osu_subscriptionexpiry FROM {osu}.phpbb_users WHERE user_id = 2");

        var first = await store.RecordVoteAsync(1, 1, default);
        var second = await store.RecordVoteAsync(1, 1, default);
        var supporter = await store.RecordVoteAsync(2, 2, default);
        var countOnly = await store.RecordVoteAsync(1, 0, default);

        var now = await db.ScalarDateAsync("SELECT UTC_TIMESTAMP()");
        Assert.Equal(1, first.Votes);
        Assert.Equal(2, second.Votes);
        Assert.Equal(3, countOnly.Votes);
        Assert.Null(countOnly.DirectEndsUtc);
        Assert.InRange((second.DirectEndsUtc!.Value - now).TotalHours, 47.9, 48.1);
        Assert.InRange((supporter.DirectEndsUtc!.Value - now).TotalHours, 47.9, 48.1);

        // Votes don't touch supporter.
        Assert.Equal(0L, Convert.ToInt64(await db.ScalarAsync("SELECT osu_subscriber FROM {osu}.phpbb_users WHERE user_id = 1"), CultureInfo.InvariantCulture));
        Assert.Equal(bought, await db.ScalarDateAsync("SELECT osu_subscriptionexpiry FROM {osu}.phpbb_users WHERE user_id = 2"));

        // The plugin's table is in the connection's own database, not the website's.
        Assert.Equal(3L, Convert.ToInt64(await db.ScalarAsync("SELECT votes FROM {tomyum}.plugin_votes WHERE user_id = 1"), CultureInfo.InvariantCulture));

        Assert.True(await store.HasDirectAsync(1, default));
        Assert.False(await store.HasDirectAsync(404, default));
        var status = await store.GetStatusAsync(1, default);
        Assert.Equal(3, status.Votes);
        Assert.False(status.IsSupporter);
        Assert.NotNull(status.DirectEndsUtc);
        Assert.True((await store.GetStatusAsync(2, default)).IsSupporter);
        Assert.Equal(new VoteStatus(0, false, null, null), await store.GetStatusAsync(404, default));
    }

    [MySqlFact]
    public async Task An_older_table_gets_the_osu_direct_column()
    {
        await using var db = await TestDatabases.CreateAsync();
        await db.ExecAsync("INSERT INTO {osu}.phpbb_users VALUES (1, 'a', 'a', 0, 0, NULL); "
            + "CREATE TABLE {tomyum}.plugin_votes (user_id INT UNSIGNED NOT NULL PRIMARY KEY, votes INT UNSIGNED NOT NULL DEFAULT 0, "
            + "first_voted_at DATETIME NOT NULL, last_voted_at DATETIME NOT NULL); "
            + "INSERT INTO {tomyum}.plugin_votes VALUES (1, 4, UTC_TIMESTAMP(), UTC_TIMESTAMP())");
        var store = db.Store();

        var result = await store.RecordVoteAsync(1, 1, default);

        Assert.Equal(5, result.Votes);
        Assert.NotNull(result.DirectEndsUtc);
    }

    [MySqlFact]
    public async Task Votes_at_the_same_moment_all_count()
    {
        await using var db = await TestDatabases.CreateAsync();
        await db.ExecAsync("INSERT INTO {osu}.phpbb_users VALUES (1, 'a', 'a', 0, 0, NULL)");
        var store = db.Store();
        await store.RecordVoteAsync(1, 1, default);
        var before = await db.ScalarDateAsync("SELECT direct_until FROM {tomyum}.plugin_votes WHERE user_id = 1");

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => store.RecordVoteAsync(1, 1, default))));

        Assert.Equal(11, (await store.GetStatusAsync(1, default)).Votes);
        Assert.Equal(before.AddDays(10), await db.ScalarDateAsync("SELECT direct_until FROM {tomyum}.plugin_votes WHERE user_id = 1"));
    }

    [MySqlFact]
    public async Task Expiry_only_ends_supporter_whose_end_date_has_passed()
    {
        await using var db = await TestDatabases.CreateAsync();
        await db.ExecAsync(
            "INSERT INTO {osu}.phpbb_users VALUES "
            + "(1, 'a', 'a', 0, 1, UTC_TIMESTAMP() - INTERVAL 1 MINUTE), "
            + "(2, 'b', 'b', 0, 1, UTC_TIMESTAMP() + INTERVAL 1 DAY), "
            + "(3, 'c', 'c', 0, 1, NULL), "
            + "(4, 'd', 'd', 0, 0, UTC_TIMESTAMP() - INTERVAL 1 DAY)");
        var store = db.Store();

        Assert.Equal(new long[] { 1 }, await store.ExpireSupporterAsync(default));
        Assert.Empty(await store.ExpireSupporterAsync(default));

        Assert.Equal("0,1,1,0", await db.ScalarAsync("SELECT GROUP_CONCAT(osu_subscriber ORDER BY user_id) FROM {osu}.phpbb_users"));
        // The end date stays (osu-web shows "has supported" from it).
        Assert.NotNull(await db.ScalarAsync("SELECT osu_subscriptionexpiry FROM {osu}.phpbb_users WHERE user_id = 1"));
    }

    private sealed class TestDatabases : IPluginDataAccess, IAsyncDisposable
    {
        private readonly string _server;
        private readonly string _tomyum;
        private readonly string _osu;

        private TestDatabases(string server, string suffix)
        {
            _server = server;
            _tomyum = "votes_test_tomyum_" + suffix;
            _osu = "votes_test_osu_" + suffix;
        }

        public static async Task<TestDatabases> CreateAsync()
        {
            var server = Environment.GetEnvironmentVariable(MySqlFactAttribute.Variable)!;
            var db = new TestDatabases(server, Guid.NewGuid().ToString("N")[..8]);
            await db.ExecAsync("CREATE DATABASE {tomyum}; CREATE DATABASE {osu}; "
                + "CREATE TABLE {osu}.phpbb_users (user_id MEDIUMINT UNSIGNED NOT NULL PRIMARY KEY, username VARCHAR(255) NOT NULL, "
                + "username_clean VARCHAR(255) NOT NULL UNIQUE, user_type TINYINT NOT NULL DEFAULT 0, "
                + "osu_subscriber TINYINT(1) NOT NULL DEFAULT 0, osu_subscriptionexpiry DATETIME NULL)");
            return db;
        }

        public MySqlVoteStore Store() => new(this, _osu);

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = new MySqlConnection(new MySqlConnectionStringBuilder(_server) { Database = _tomyum }.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }

        public async Task ExecAsync(string sql)
        {
            await using var connection = new MySqlConnection(_server);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(Expand(sql), connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new MySqlConnection(_server);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(Expand(sql), connection);
            var value = await command.ExecuteScalarAsync();
            return value is DBNull ? null : value;
        }

        public async Task<DateTime> ScalarDateAsync(string sql) =>
            DateTime.SpecifyKind((DateTime)(await ScalarAsync(sql))!, DateTimeKind.Utc);

        public async ValueTask DisposeAsync() => await ExecAsync("DROP DATABASE IF EXISTS {tomyum}; DROP DATABASE IF EXISTS {osu}");

        private string Expand(string sql) => sql.Replace("{tomyum}", "`" + _tomyum + "`", StringComparison.Ordinal)
            .Replace("{osu}", "`" + _osu + "`", StringComparison.Ordinal);
    }
}
