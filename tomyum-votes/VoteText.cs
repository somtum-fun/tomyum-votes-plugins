// Votes plugin: everything players read (BanchoBot messages, !vote, notifications).

using System;
using System.Globalization;

namespace TomYum.Plugins.Votes;

/// <summary>The plugin's player-facing text.</summary>
public static class VoteText
{
    /// <summary>First BanchoBot message after a vote.</summary>
    /// <param name="votes">The player's votes, this one included.</param>
    /// <returns>The message.</returns>
    public static string ThankYou(int votes) => votes <= 1
        ? "Thank you for voting for somtum.fun! That was your first vote."
        : Invariant($"Thank you for voting for somtum.fun! You've voted {Times(votes)} now.");

    /// <summary>Second BanchoBot message after a vote: what it gave them, or null when it gave nothing.</summary>
    /// <param name="result">What the vote did.</param>
    /// <param name="daysPerVote">Days of osu!direct per vote.</param>
    /// <returns>The message, or null.</returns>
    public static string? Reward(VoteResult result, int daysPerVote)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.DirectEndsUtc is { } end
            ? Invariant($"You got {Days(daysPerVote)} of osu!direct! It now lasts until {Date(end)}. ")
              + "If you're in game, log out and back in to get it there too."
            : null;
    }

    /// <summary>The reply to <c>!vote</c>.</summary>
    /// <param name="status">The player's votes, osu!direct and supporter, or null when they can't be read.</param>
    /// <param name="voteUrl">Where to vote.</param>
    /// <param name="daysPerVote">Days of osu!direct per vote (0: votes don't give any).</param>
    /// <returns>The reply.</returns>
    public static string Command(VoteStatus? status, string voteUrl, int daysPerVote)
    {
        var lines = new System.Collections.Generic.List<string>();
        if (status is not null)
        {
            lines.Add(status.Votes == 0
                ? "You haven't voted yet."
                : Invariant($"You've voted {Times(status.Votes)}. Thank you!"));
            if (status.IsSupporter)
            {
                lines.Add(status.SupporterEndsUtc is { } end
                    ? Invariant($"Your osu!supporter lasts until {Date(end)}.")
                    : "Your osu!supporter has no end date.");
            }
            else if (status.DirectEndsUtc is { } direct)
            {
                lines.Add(Invariant($"Your osu!direct lasts until {Date(direct)}."));
            }
        }

        lines.Add(daysPerVote > 0
            ? Invariant($"Every vote gives you {Days(daysPerVote)} of osu!direct, added on top of any you already have.")
            : "Voting helps more players find somtum.fun.");
        lines.Add(Invariant($"[{voteUrl} Click here to vote!]"));
        return string.Join("\n", lines);
    }

    /// <summary>The notification when a player's supporter runs out while they're online.</summary>
    public static string SupporterEnded =>
        "Your osu!supporter has ended. Thanks for supporting somtum.fun! Type !vote to see how to get osu!direct for free.";

    /// <summary>"1 day", "3 days".</summary>
    /// <param name="days">The number of days.</param>
    /// <returns>The words.</returns>
    public static string Days(int days) => days == 1 ? "1 day" : Invariant($"{days} days");

    /// <summary>"once", "twice", "5 times".</summary>
    /// <param name="count">How many times.</param>
    /// <returns>The words.</returns>
    public static string Times(int count) => count switch
    {
        1 => "once",
        2 => "twice",
        _ => Invariant($"{count} times"),
    };

    private static string Date(DateTime utc) => utc.ToString("d MMMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
