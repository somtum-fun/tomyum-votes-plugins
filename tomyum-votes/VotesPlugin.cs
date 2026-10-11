// Votes plugin (tomyum.votes): votes from osu-server-list.com (OSL), like bancho.py's voteCallback.
//
//   - GET /vote-callback?username=&key= : OSL calls it when someone votes. 401 for a wrong key, 404
//     for an unknown player, 200 when counted. Each vote adds daysPerVote days of osu!direct on top of
//     any time left (not supporter: no other supporter perks), and BanchoBot thanks the player online.
//   - At login (TomYum's ClientPerksEvent), a voter with osu!direct time left gets osu!direct in game;
//     the website shows them its osu!direct button from the same table.
//   - !vote : the player's votes, their osu!direct (or supporter) end date and the voting link.
//   - Every few minutes, bought supporter whose end date has passed is taken off (nothing else in the
//     stack does it), and players online get a notification.
//
// Needs trusted database access (Plugins:Trusted). Human docs: README.md at the repo root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.Votes;

/// <summary>The osu-server-list.com votes plugin.</summary>
#pragma warning disable CA1001 // _cts is disposed in ShutdownAsync, the end of a plugin's life
public sealed class VotesPlugin : IPlugin
#pragma warning restore CA1001
{
    /// <summary>The callback's path (bancho.py's, so OSL's setting only needs the new host).</summary>
    public const string CallbackPath = "/vote-callback";

    /// <summary>Where players vote, unless <c>voteUrl</c> is set.</summary>
    public const string DefaultVoteUrl = "https://osu-server-list.com/server/somtum/vote";

    /// <summary>The host names' first labels the callback answers on, unless <c>subdomains</c> is set.</summary>
    public const string DefaultSubdomains = "osu,c";

    private static readonly Regex DatabaseName = new("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant);

    private readonly IVoteStore? _injectedStore;
    private readonly List<IDisposable> _registrations = new();
    private IVoteStore? _store;
    private IHostMessenger? _messenger;
    private IPluginLogger? _logger;
    private string _apiKey = string.Empty;
    private string _voteUrl = DefaultVoteUrl;
    private int _daysPerVote = 1;
    private TimeSpan _expireInterval = TimeSpan.FromMinutes(5);
    private CancellationTokenSource? _cts;
    private Task? _expiry;

    /// <summary>Creates the plugin; the host uses this one.</summary>
    public VotesPlugin()
    {
    }

    /// <summary>Creates the plugin over <paramref name="store"/> instead of the database (tests).</summary>
    /// <param name="store">Where votes and supporter go.</param>
    public VotesPlugin(IVoteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _injectedStore = store;
    }

    /// <inheritdoc/>
    public PluginManifest Manifest { get; } = new(
        Id: "tomyum.votes",
        Name: "Votes (osu-server-list.com)",
        Version: "1.1.0",
        Authors: "Phapoom Saksri",
        Description: "osu-server-list.com votes: GET /vote-callback, !vote, and osu!direct days for voting.");

    /// <summary>The host-name labels the callback was mapped on (empty: every host).</summary>
    public IReadOnlyList<string> Subdomains { get; private set; } = Array.Empty<string>();

    /// <inheritdoc/>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger = context.Logger;
        _messenger = context.GetHostService<IHostMessenger>();

        _apiKey = (context.Config.Get("apiKey", string.Empty) ?? string.Empty).Trim();
        var voteUrl = (context.Config.Get("voteUrl", DefaultVoteUrl) ?? string.Empty).Trim();
        _voteUrl = voteUrl.Length > 0 ? voteUrl : DefaultVoteUrl;
        _daysPerVote = Math.Clamp(context.Config.Get("daysPerVote", 1), 0, 3650);
        _expireInterval = TimeSpan.FromMinutes(Math.Max(1, context.Config.Get("expireIntervalMinutes", 5)));
        var expireSupporter = context.Config.Get("expireSupporter", true);

        _store = _injectedStore ?? CreateStore(context);

        var subdomains = context.Config.Get("subdomains", DefaultSubdomains) ?? string.Empty;
        Subdomains = subdomains.Trim() == "*"
            ? Array.Empty<string>()
            : subdomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        if (Subdomains.Count == 0)
        {
            _registrations.Add(context.Http.Map("GET", CallbackPath, HandleCallbackAsync));
        }
        else
        {
            foreach (var label in Subdomains)
                _registrations.Add(context.Http.Map("GET", CallbackPath, HandleCallbackAsync, requireAuth: false, subdomain: label));
        }

        _registrations.Add(context.Commands.Register(
            "vote",
            "vote for the server and get osu!direct",
            HandleCommandAsync));

        if (_store is not null)
            _registrations.Add(context.Events.Subscribe<ClientPerksEvent>(HandleClientPerksAsync));

        if (_apiKey.Length == 0)
            _logger.Warn("tomyum.votes: apiKey isn't set, so every vote callback is refused. Set Plugins:tomyum.votes:apiKey to OSL's key.");
        if (_messenger is null)
            _logger.Warn("tomyum.votes: the host doesn't publish IHostMessenger, so voters don't get BanchoBot's thank-you.");

        if (_store is not null && expireSupporter)
        {
            _cts = new CancellationTokenSource();
            _expiry = Task.Run(() => ExpireLoopAsync(_cts.Token), CancellationToken.None);
        }

        var hosts = Subdomains.Count == 0 ? "every host" : string.Join(", ", Subdomains.Select(s => s + ".<domain>"));
        _logger.Info(
            $"tomyum.votes: GET {CallbackPath} on {hosts}; {VoteText.Days(_daysPerVote)} of osu!direct per vote; "
            + (expireSupporter && _store is not null ? $"ended supporter is taken off every {_expireInterval.TotalMinutes} min." : "supporter isn't expired by this plugin."));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        foreach (var registration in _registrations)
            registration.Dispose();
        _registrations.Clear();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_expiry is not null)
            {
                try
                {
                    await _expiry.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _cts.Dispose();
            _cts = null;
            _expiry = null;
        }
    }

    /// <summary>
    /// Whether <paramref name="given"/> is the configured key, in constant time (both are hashed first,
    /// so neither the content nor the length leaks). An empty configured key matches nothing.
    /// </summary>
    /// <param name="expected">The configured key.</param>
    /// <param name="given">The key from the request.</param>
    /// <returns>True when they're equal and the configured key isn't empty.</returns>
    public static bool KeyMatches(string? expected, string? given)
    {
        if (string.IsNullOrEmpty(expected) || given is null)
            return false;

        var a = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(given));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>One expiry pass: takes ended supporter off and tells the players online.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many players' supporter ended.</returns>
    public async Task<int> ExpireOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_store is null)
            return 0;

        var expired = await _store.ExpireSupporterAsync(cancellationToken).ConfigureAwait(false);
        foreach (var userId in expired)
        {
            _logger?.Info($"tomyum.votes: osu!supporter of user {userId} has ended.");
            _messenger?.SendNotification(userId, VoteText.SupporterEnded);
        }

        return expired.Count;
    }

    private MySqlVoteStore? CreateStore(IPluginContext context)
    {
        var database = (context.Config.Get("osuWebDatabase", "osu") ?? string.Empty).Trim();
        if (!DatabaseName.IsMatch(database))
        {
            _logger?.Error($"tomyum.votes: osuWebDatabase '{database}' isn't a plain database name; votes can't be counted.");
            return null;
        }

        if (context.Data is null)
        {
            _logger?.Error(
                "tomyum.votes: no database access, so votes can't be counted (callbacks answer 503). "
                + "Add \"tomyum.votes\" to Plugins:Trusted and enable the database.");
            return null;
        }

        return new MySqlVoteStore(context.Data, database);
    }

    private async Task<PluginHttpResponse> HandleCallbackAsync(PluginHttpRequest request, CancellationToken cancellationToken)
    {
        request.Query.TryGetValue("key", out var key);
        if (!KeyMatches(_apiKey, key))
        {
            _logger?.Warn($"tomyum.votes: vote callback with a wrong key (from {ClientIp(request)}).");
            return Text(401, "Invalid API key.");
        }

        if (_store is null)
            return Text(503, "Votes can't be counted right now.");

        request.Query.TryGetValue("username", out var username);
        try
        {
            var user = await _store.FindUserAsync(username ?? string.Empty, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                _logger?.Warn($"tomyum.votes: vote for unknown player '{Shorten(username)}'.");
                return Text(404, "User not found.");
            }

            var result = await _store.RecordVoteAsync(user.UserId, _daysPerVote, cancellationToken).ConfigureAwait(false);
            _logger?.Info(
                $"tomyum.votes: vote from {user.Username} ({user.UserId}), {result.Votes} in total"
                + (result.DirectEndsUtc is { } end ? $"; osu!direct until {end:yyyy-MM-dd HH:mm} UTC." : "."));

            if (_messenger is not null)
            {
                _messenger.SendBotMessage(user.UserId, VoteText.ThankYou(result.Votes));
                if (VoteText.Reward(result, _daysPerVote) is { } reward)
                    _messenger.SendBotMessage(user.UserId, reward);
            }

            return new PluginHttpResponse(200, "text/plain; charset=utf-8", ReadOnlyMemory<byte>.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Error($"tomyum.votes: counting a vote for '{Shorten(username)}' failed.", ex);
            return Text(500, "Something went wrong.");
        }
    }

    /// <summary>At login: a voter with osu!direct time left gets osu!direct in game for the session.</summary>
    private async Task HandleClientPerksAsync(ClientPerksEvent perks, CancellationToken cancellationToken)
    {
        if (_store is null)
            return;

        try
        {
            if (await _store.HasDirectAsync(perks.UserId, cancellationToken).ConfigureAwait(false))
                perks.OsuDirect = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never in the way of a login: they just don't get osu!direct this time.
            _logger?.Warn($"tomyum.votes: reading osu!direct of user {perks.UserId} failed: {ex.Message}");
        }
    }

    private async Task<string?> HandleCommandAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        VoteStatus? status = null;
        if (_store is not null && invocation.InvokerUserId is { } userId)
        {
            try
            {
                status = await _store.GetStatusAsync(userId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The link still helps; the count is left out.
                _logger?.Warn($"tomyum.votes: reading votes of user {userId} failed: {ex.Message}");
            }
        }

        return VoteText.Command(status, _voteUrl, _daysPerVote);
    }

    private async Task ExpireLoopAsync(CancellationToken cancellationToken)
    {
        var failing = false;
        using var timer = new PeriodicTimer(_expireInterval);
        do
        {
            try
            {
                await ExpireOnceAsync(cancellationToken).ConfigureAwait(false);
                if (failing)
                    _logger?.Info("tomyum.votes: checking supporter end dates works again.");
                failing = false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Logged once per outage, tried again next tick.
                if (!failing)
                    _logger?.Warn($"tomyum.votes: checking supporter end dates failed; trying again every {_expireInterval.TotalMinutes} min. {ex.Message}");
                failing = true;
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private static string ClientIp(PluginHttpRequest request)
    {
        foreach (var header in new[] { "CF-Connecting-IP", "X-Real-IP", "X-Forwarded-For" })
        {
            if (request.Headers.TryGetValue(header, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Split(',')[0].Trim();
        }

        return "unknown";
    }

    private static string Shorten(string? text)
    {
        // From the query string: no line breaks or other control characters in the log.
        var value = new string((text ?? string.Empty).Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return value.Length > VoteNames.MaxLength ? value[..VoteNames.MaxLength] + "…" : value;
    }

    private static PluginHttpResponse Text(int status, string text) =>
        new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
}
