using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;
using TomYum.Plugins.Votes;

using Xunit;

namespace TomYum.Votes.Tests;

/// <summary>The plugin's wiring, the callback's answers (as bancho.py's), !vote and the expiry pass.</summary>
public sealed class VotesPluginTests
{
    private const string Key = "osl-secret-key";

    [Fact]
    public async Task Registers_the_callback_on_osu_and_c_and_the_vote_command()
    {
        var (plugin, context, _, _) = await StartAsync();

        Assert.Equal(new[] { "osu", "c" }, context.Routes.Routes.Select(r => r.Subdomain));
        Assert.All(context.Routes.Routes, r =>
        {
            Assert.Equal("GET", r.Method);
            Assert.Equal("/vote-callback", r.Pattern);
            Assert.False(r.RequireAuth);
        });
        Assert.True(context.CommandRegistry.Commands.ContainsKey("vote"));

        await plugin.ShutdownAsync();
        Assert.All(context.Routes.Routes, r => Assert.True(r.Disposed));
        Assert.Empty(context.CommandRegistry.Commands);
    }

    [Fact]
    public async Task Subdomains_star_serves_every_host()
    {
        var (plugin, context, _, _) = await StartAsync(("subdomains", "*"));
        Assert.Null(Assert.Single(context.Routes.Routes).Subdomain);
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task Subdomains_can_be_changed()
    {
        var (plugin, context, _, _) = await StartAsync(("subdomains", " osu , vote ,osu"));
        Assert.Equal(new[] { "osu", "vote" }, context.Routes.Routes.Select(r => r.Subdomain));
        await plugin.ShutdownAsync();
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("")]
    [InlineData("osl-secret-key ")]
    [InlineData("OSL-SECRET-KEY")]
    [InlineData(null)]
    public async Task A_wrong_key_is_401_and_touches_nothing(string? key)
    {
        var (plugin, context, store, _) = await StartAsync();
        store.Add(5, "blueskychan_");

        var response = await CallAsync(context, "blueskychan_", key);

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("Invalid API key.", Body(response));
        Assert.Equal(0, store.Calls);
        Assert.Contains(context.Log.Warnings, w => w.Contains("wrong key", StringComparison.Ordinal));
        await plugin.ShutdownAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("anything")]
    public async Task Without_an_api_key_every_callback_is_refused(string key)
    {
        var (plugin, context, store, _) = await StartAsync(("apiKey", ""));
        store.Add(5, "blueskychan_");

        var response = await CallAsync(context, "blueskychan_", key);

        Assert.Equal(401, response.StatusCode);
        Assert.Equal(0, store.Calls);
        Assert.Contains(context.Log.Warnings, w => w.Contains("apiKey isn't set", StringComparison.Ordinal));
        await plugin.ShutdownAsync();
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("deleted")]
    public async Task An_unknown_player_is_404(string? username)
    {
        var (plugin, context, store, _) = await StartAsync();
        store.Add(5, "blueskychan_");
        store.Add(6, "deleted", deleted: true);

        var response = await CallAsync(context, username, Key);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("User not found.", Body(response));
        Assert.Empty(store.Votes);
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task A_vote_counts_gives_a_day_and_banchobot_says_thanks()
    {
        var (plugin, context, store, messenger) = await StartAsync();
        store.Add(5, "blueskychan_");
        messenger.Online.Add(5);

        var response = await CallAsync(context, "BlueSkyChan_", Key);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(1, store.Votes[5]);
        Assert.True(store.Accounts[5].Subscriber);
        Assert.Equal(store.NowUtc.AddDays(1), store.Accounts[5].Expiry);
        Assert.Collection(
            messenger.BotMessages,
            m => Assert.Equal((5L, "Thank you for voting for osu!somtum! That was your first vote."), m),
            m =>
            {
                Assert.Equal(5, m.UserId);
                Assert.StartsWith("You got 1 day of osu!supporter! It now lasts until 5 October 2026, 12:00 UTC.", m.Text, StringComparison.Ordinal);
            });
        Assert.Contains(context.Log.Infos, i => i.Contains("vote from blueskychan_ (5), 1 in total", StringComparison.Ordinal));
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task Votes_stack_on_bought_supporter_and_days_per_vote_is_used()
    {
        var (plugin, context, store, messenger) = await StartAsync(("daysPerVote", "3"));
        var bought = store.NowUtc.AddDays(30);
        store.Add(9, "Lilily", subscriber: true, expiry: bought);
        messenger.Online.Add(9);

        Assert.Equal(200, (await CallAsync(context, "lilily", Key)).StatusCode);
        Assert.Equal(200, (await CallAsync(context, "lilily", Key)).StatusCode);

        Assert.Equal(2, store.Votes[9]);
        Assert.Equal(bought.AddDays(6), store.Accounts[9].Expiry);
        Assert.Contains(messenger.BotMessages, m => m.Text == "Thank you for voting for osu!somtum! You've voted twice now.");
        Assert.Contains(messenger.BotMessages, m => m.Text.StartsWith("You got 3 days of osu!supporter!", StringComparison.Ordinal));
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task Supporter_with_no_end_date_is_left_alone()
    {
        var (plugin, context, store, messenger) = await StartAsync();
        store.Add(2, "peppy", subscriber: true, expiry: null);
        messenger.Online.Add(2);

        Assert.Equal(200, (await CallAsync(context, "peppy", Key)).StatusCode);

        Assert.Null(store.Accounts[2].Expiry);
        Assert.Equal(1, store.Votes[2]);
        Assert.Contains(messenger.BotMessages, m => m.Text.Contains("no end date", StringComparison.Ordinal));
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task Days_per_vote_zero_only_counts()
    {
        var (plugin, context, store, messenger) = await StartAsync(("daysPerVote", "0"));
        store.Add(5, "blueskychan_");
        messenger.Online.Add(5);

        Assert.Equal(200, (await CallAsync(context, "blueskychan_", Key)).StatusCode);

        Assert.False(store.Accounts[5].Subscriber);
        Assert.Single(messenger.BotMessages);
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task An_offline_voter_still_gets_the_vote()
    {
        var (plugin, context, store, messenger) = await StartAsync();
        store.Add(5, "blueskychan_");

        Assert.Equal(200, (await CallAsync(context, "blueskychan_", Key)).StatusCode);

        Assert.Equal(1, store.Votes[5]);
        Assert.Empty(messenger.BotMessages);
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task Without_database_access_it_is_503_after_the_key_check()
    {
        var context = new FakeContext(new FakeConfig(("apiKey", Key)), new FakeMessenger(), data: null);
        var plugin = new VotesPlugin();
        await plugin.InitializeAsync(context);

        Assert.Equal(401, (await CallAsync(context, "x", "wrong")).StatusCode);
        Assert.Equal(503, (await CallAsync(context, "x", Key)).StatusCode);
        Assert.Contains(context.Log.Errors, e => e.Contains("Plugins:Trusted", StringComparison.Ordinal));

        // !vote still gives the link.
        var reply = await RunVoteAsync(context, userId: 5);
        Assert.Contains("[https://osu-server-list.com/server/somtum/vote Click here to vote!]", reply, StringComparison.Ordinal);
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task A_bad_database_name_turns_counting_off()
    {
        var context = new FakeContext(new FakeConfig(("apiKey", Key), ("osuWebDatabase", "osu`; DROP")), null, new NoData());
        var plugin = new VotesPlugin();
        await plugin.InitializeAsync(context);

        Assert.Equal(503, (await CallAsync(context, "x", Key)).StatusCode);
        Assert.Contains(context.Log.Errors, e => e.Contains("isn't a plain database name", StringComparison.Ordinal));
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task A_database_error_is_500_and_logged()
    {
        var (plugin, context, store, _) = await StartAsync();
        store.Throw = new InvalidOperationException("db down");

        var response = await CallAsync(context, "blueskychan_", Key);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains(context.Log.Errors, e => e.Contains("counting a vote", StringComparison.Ordinal));
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task Vote_command_shows_votes_supporter_and_link()
    {
        var (plugin, context, store, _) = await StartAsync(("voteUrl", "https://example.com/vote"));
        store.Add(5, "blueskychan_", subscriber: true, expiry: store.NowUtc.AddDays(2));
        store.Votes[5] = 7;

        var reply = await RunVoteAsync(context, userId: 5);

        Assert.Equal(
            "You've voted 7 times. Thank you!\n"
            + "Your osu!supporter lasts until 6 October 2026, 12:00 UTC.\n"
            + "Every vote gives you 1 day of osu!supporter, added on top of any you already have.\n"
            + "[https://example.com/vote Click here to vote!]",
            reply);
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task Vote_command_for_a_new_voter()
    {
        var (plugin, context, store, _) = await StartAsync();
        store.Add(5, "blueskychan_");

        var reply = await RunVoteAsync(context, userId: 5);

        Assert.StartsWith("You haven't voted yet.\nEvery vote gives you 1 day", reply, StringComparison.Ordinal);
        Assert.EndsWith("[https://osu-server-list.com/server/somtum/vote Click here to vote!]", reply, StringComparison.Ordinal);
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task Vote_command_still_gives_the_link_when_the_database_fails()
    {
        var (plugin, context, store, _) = await StartAsync();
        store.Throw = new InvalidOperationException("db down");

        var reply = await RunVoteAsync(context, userId: 5);

        Assert.StartsWith("Every vote gives you", reply, StringComparison.Ordinal);
        Assert.Single(context.Log.Warnings);
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task Expiry_takes_ended_supporter_off_and_tells_players_online()
    {
        var (plugin, context, store, messenger) = await StartAsync();
        store.Add(5, "ended", subscriber: true, expiry: store.NowUtc.AddMinutes(-1));
        store.Add(6, "ended offline", subscriber: true, expiry: store.NowUtc.AddDays(-3));
        store.Add(7, "still going", subscriber: true, expiry: store.NowUtc.AddDays(3));
        store.Add(2, "forever", subscriber: true, expiry: null);
        messenger.Online.UnionWith(new long[] { 5, 7, 2 });

        Assert.Equal(2, await plugin.ExpireOnceAsync());

        Assert.False(store.Accounts[5].Subscriber);
        Assert.False(store.Accounts[6].Subscriber);
        Assert.True(store.Accounts[7].Subscriber);
        Assert.True(store.Accounts[2].Subscriber);
        Assert.Equal(new[] { (5L, VoteText.SupporterEnded) }, messenger.Notifications);
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task The_expiry_loop_runs_at_start_and_stops_on_shutdown()
    {
        var store = new FakeStore();
        store.Add(5, "ended", subscriber: true, expiry: store.NowUtc.AddMinutes(-1));
        var context = new FakeContext(new FakeConfig(("apiKey", Key)), new FakeMessenger());
        var plugin = new VotesPlugin(store);
        await plugin.InitializeAsync(context);

        for (var i = 0; i < 100 && store.Accounts[5].Subscriber; i++)
            await Task.Delay(20);

        Assert.False(store.Accounts[5].Subscriber);
        await plugin.ShutdownAsync();
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "ab", false)]
    [InlineData("abc", null, false)]
    [InlineData("", "", false)]
    [InlineData(null, "", false)]
    [InlineData(null, null, false)]
    public void Key_comparison(string? expected, string? given, bool matches) =>
        Assert.Equal(matches, VotesPlugin.KeyMatches(expected, given));

    private static async Task<(VotesPlugin Plugin, FakeContext Context, FakeStore Store, FakeMessenger Messenger)> StartAsync(
        params (string Key, string Value)[] config)
    {
        var values = new List<(string, string)> { ("apiKey", Key), ("expireSupporter", "false") };
        values.AddRange(config);
        var store = new FakeStore();
        var messenger = new FakeMessenger();
        var context = new FakeContext(new FakeConfig(values.ToArray()), messenger);
        var plugin = new VotesPlugin(store);
        await plugin.InitializeAsync(context);
        return (plugin, context, store, messenger);
    }

    private static Task<PluginHttpResponse> CallAsync(FakeContext context, string? username, string? key)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (username is not null)
            query["username"] = username;
        if (key is not null)
            query["key"] = key;
        var request = new PluginHttpRequest(
            "GET",
            "/vote-callback",
            new Dictionary<string, string>(),
            query,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = "osu.blueskychan.dev", ["CF-Connecting-IP"] = "203.0.113.7" },
            null,
            null);
        return context.Routes.Routes[0].Handler(request, CancellationToken.None);
    }

    private static Task<string?> RunVoteAsync(FakeContext context, long? userId) =>
        context.CommandRegistry.Commands["vote"].Handler(
            new CommandInvocation("vote", Array.Empty<string>(), "!vote", userId, null),
            CancellationToken.None);

    private static string Body(PluginHttpResponse response) => Encoding.UTF8.GetString(response.Body.Span);

    private sealed class NoData : IPluginDataAccess
    {
        public Task<System.Data.Common.DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("not used");
    }
}
