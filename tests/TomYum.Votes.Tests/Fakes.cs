using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;
using TomYum.Plugins.Votes;

namespace TomYum.Votes.Tests;

/// <summary>An in-memory store with the same rules as the SQL one.</summary>
internal sealed class FakeStore : IVoteStore
{
    public Dictionary<long, FakeAccount> Accounts { get; } = new();

    public Dictionary<long, int> Votes { get; } = new();

    public DateTime NowUtc { get; set; } = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    public Exception? Throw { get; set; }

    public int Calls { get; private set; }

    public FakeAccount Add(long id, string name, bool subscriber = false, DateTime? expiry = null, bool deleted = false)
    {
        var account = new FakeAccount(id, name) { Subscriber = subscriber, Expiry = expiry, Deleted = deleted };
        Accounts[id] = account;
        return account;
    }

    public Task<VoteUser?> FindUserAsync(string username, CancellationToken cancellationToken)
    {
        Calls++;
        ThrowIfSet();
        var candidates = VoteNames.CleanCandidates(username);
        foreach (var candidate in candidates)
        {
            var match = Accounts.Values.FirstOrDefault(a => !a.Deleted && VoteNames.CleanCandidates(a.Name)[0] == candidate);
            if (match is not null)
                return Task.FromResult<VoteUser?>(new VoteUser(match.Id, match.Name));
        }

        return Task.FromResult<VoteUser?>(null);
    }

    public Dictionary<long, DateTime> Direct { get; } = new();

    public Task<VoteResult> RecordVoteAsync(long userId, int directDays, CancellationToken cancellationToken)
    {
        Calls++;
        ThrowIfSet();
        _ = Accounts[userId];
        Votes[userId] = Votes.GetValueOrDefault(userId) + 1;
        var votes = Votes[userId];
        if (directDays <= 0)
            return Task.FromResult(new VoteResult(votes, null));

        var from = Direct.TryGetValue(userId, out var e) && e > NowUtc ? e : NowUtc;
        Direct[userId] = from.AddDays(directDays);
        return Task.FromResult(new VoteResult(votes, Direct[userId]));
    }

    public Task<bool> HasDirectAsync(long userId, CancellationToken cancellationToken)
    {
        Calls++;
        ThrowIfSet();
        return Task.FromResult(Direct.TryGetValue(userId, out var end) && end > NowUtc);
    }

    public Task<VoteStatus> GetStatusAsync(long userId, CancellationToken cancellationToken)
    {
        Calls++;
        ThrowIfSet();
        if (!Accounts.TryGetValue(userId, out var account))
            return Task.FromResult(new VoteStatus(0, false, null, null));
        var active = account.Subscriber && (account.Expiry is null || account.Expiry > NowUtc);
        DateTime? direct = Direct.TryGetValue(userId, out var end) && end > NowUtc ? end : null;
        return Task.FromResult(new VoteStatus(Votes.GetValueOrDefault(userId), active, active ? account.Expiry : null, direct));
    }

    public Task<IReadOnlyList<long>> ExpireSupporterAsync(CancellationToken cancellationToken)
    {
        Calls++;
        ThrowIfSet();
        var expired = Accounts.Values.Where(a => a.Subscriber && a.Expiry is { } e && e <= NowUtc).ToList();
        foreach (var account in expired)
            account.Subscriber = false;
        return Task.FromResult<IReadOnlyList<long>>(expired.Select(a => a.Id).ToList());
    }

    private void ThrowIfSet()
    {
        if (Throw is not null)
            throw Throw;
    }
}

internal sealed class FakeAccount(long id, string name)
{
    public long Id { get; } = id;

    public string Name { get; } = name;

    public bool Subscriber { get; set; }

    public DateTime? Expiry { get; set; }

    public bool Deleted { get; set; }
}

internal sealed class FakeConfig : IPluginConfig
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public FakeConfig(params (string Key, string Value)[] values)
    {
        foreach (var (key, value) in values)
            _values[key] = value;
    }

    // Values arrive as strings, as they do from env vars and YAML.
    public T? Get<T>(string key, T? defaultValue = default)
    {
        if (!_values.TryGetValue(key, out var raw))
            return defaultValue;
        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
    }

    public TOptions Bind<TOptions>() where TOptions : new() => new();
}

internal sealed class FakeLogger : IPluginLogger
{
    public List<string> Infos { get; } = new();

    public List<string> Warnings { get; } = new();

    public List<string> Errors { get; } = new();

    public void Info(string message) => Infos.Add(message);

    public void Warn(string message) => Warnings.Add(message);

    public void Error(string message, Exception? exception = null) => Errors.Add(message);
}

internal sealed class FakeRoute(
    string method,
    string pattern,
    Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> handler,
    bool requireAuth,
    string? subdomain) : IDisposable
{
    public string Method { get; } = method;

    public string Pattern { get; } = pattern;

    public Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> Handler { get; } = handler;

    public bool RequireAuth { get; } = requireAuth;

    public string? Subdomain { get; } = subdomain;

    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeHttp : IHttpRouteRegistry
{
    public List<FakeRoute> Routes { get; } = new();

    public IDisposable Map(
        string method,
        string pattern,
        Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> handler,
        bool requireAuth = false,
        string? subdomain = null)
    {
        var route = new FakeRoute(method, pattern, handler, requireAuth, subdomain);
        Routes.Add(route);
        return route;
    }
}

internal sealed class FakeCommands : ICommandRegistry
{
    public Dictionary<string, (string Description, Func<CommandInvocation, CancellationToken, Task<string?>> Handler)> Commands { get; } = new();

    public int Disposed { get; private set; }

    public IDisposable Register(string name, string description, Func<CommandInvocation, CancellationToken, Task<string?>> handler)
    {
        Commands[name] = (description, handler);
        return new Registration(this, name);
    }

    private sealed class Registration(FakeCommands owner, string name) : IDisposable
    {
        public void Dispose()
        {
            owner.Commands.Remove(name);
            owner.Disposed++;
        }
    }
}

internal sealed class FakeEvents : IEventBus
{
    private readonly List<(Type Type, Func<object, CancellationToken, Task> Handler)> _handlers = new();

    public int Subscriptions => _handlers.Count;

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : IPluginEvent
    {
        var entry = (typeof(TEvent), (Func<object, CancellationToken, Task>)((e, ct) => handler((TEvent)e, ct)));
        _handlers.Add(entry);
        return new Subscription(() => _handlers.Remove(entry));
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IPluginEvent
    {
        foreach (var (type, handler) in _handlers.ToArray())
        {
            if (type == typeof(TEvent))
                await handler(@event!, cancellationToken);
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

internal sealed class FakeMessenger : IHostMessenger
{
    public HashSet<long> Online { get; } = new();

    public List<(long UserId, string Text)> BotMessages { get; } = new();

    public List<(long UserId, string Text)> Notifications { get; } = new();

    public bool SendBotMessage(long userId, string text)
    {
        if (!Online.Contains(userId))
            return false;
        BotMessages.Add((userId, text));
        return true;
    }

    public bool SendNotification(long userId, string text)
    {
        if (!Online.Contains(userId))
            return false;
        Notifications.Add((userId, text));
        return true;
    }
}

internal sealed class FakeContext : IPluginContext
{
    public FakeContext(FakeConfig config, IHostMessenger? messenger = null, IPluginDataAccess? data = null)
    {
        Config = config;
        Messenger = messenger;
        Data = data;
    }

    public IPluginConfig Config { get; }

    public FakeLogger Log { get; } = new();

    public IPluginLogger Logger => Log;

    public FakeEvents EventBus { get; } = new();

    public IEventBus Events => EventBus;

    public FakeCommands CommandRegistry { get; } = new();

    public ICommandRegistry Commands => CommandRegistry;

    public FakeHttp Routes { get; } = new();

    public IHttpRouteRegistry Http => Routes;

    public IPluginDataAccess? Data { get; }

    public IHostMessenger? Messenger { get; }

    public TService? GetHostService<TService>()
        where TService : class
        => Messenger as TService;
}
