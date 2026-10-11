using System;

using TomYum.Plugins.Votes;

using Xunit;

namespace TomYum.Votes.Tests;

public sealed class VoteTextTests
{
    [Theory]
    [InlineData("blueskychan_", new[] { "blueskychan_", "blueskychan " })]
    [InlineData("  Some Player ", new[] { "some player", "some_player" })]
    [InlineData("ABC", new[] { "abc" })]
    [InlineData("Ünï", new[] { "Ünï" })]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData(null, new string[0])]
    public void Names_are_looked_up_like_osu_web_cleans_them(string? input, string[] expected) =>
        Assert.Equal(expected, VoteNames.CleanCandidates(input));

    [Fact]
    public void Overlong_names_are_not_looked_up() =>
        Assert.Empty(VoteNames.CleanCandidates(new string('a', VoteNames.MaxLength + 1)));

    [Theory]
    [InlineData(1, "once")]
    [InlineData(2, "twice")]
    [InlineData(3, "3 times")]
    [InlineData(1000, "1000 times")]
    public void Times(int count, string expected) => Assert.Equal(expected, VoteText.Times(count));

    [Theory]
    [InlineData(1, "1 day")]
    [InlineData(2, "2 days")]
    public void Days(int days, string expected) => Assert.Equal(expected, VoteText.Days(days));

    [Fact]
    public void Reward_messages()
    {
        var end = new DateTime(2026, 11, 1, 8, 5, 0, DateTimeKind.Utc);
        Assert.Equal(
            "You got 1 day of osu!direct! It now lasts until 1 November 2026, 08:05 UTC. If you're in game, log out and back in to get it there too.",
            VoteText.Reward(new VoteResult(3, end), 1));
        Assert.Null(VoteText.Reward(new VoteResult(3, null), 0));
    }

    [Fact]
    public void Command_without_status_or_reward()
    {
        Assert.Equal(
            "Voting helps more players find somtum.fun.\n[https://x.test/vote Click here to vote!]",
            VoteText.Command(null, "https://x.test/vote", 0));
    }

    [Fact]
    public void Command_for_supporter_with_no_end_date()
    {
        Assert.StartsWith(
            "You've voted once. Thank you!\nYour osu!supporter has no end date.\n",
            VoteText.Command(new VoteStatus(1, true, null, null), "https://x.test/vote", 1),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Command_for_a_voter_with_osu_direct_left()
    {
        var end = new DateTime(2026, 11, 1, 8, 5, 0, DateTimeKind.Utc);
        Assert.StartsWith(
            "You've voted twice. Thank you!\nYour osu!direct lasts until 1 November 2026, 08:05 UTC.\nEvery vote gives you 1 day of osu!direct",
            VoteText.Command(new VoteStatus(2, false, null, end), "https://x.test/vote", 1),
            StringComparison.Ordinal);
    }
}
