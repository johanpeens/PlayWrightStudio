using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlaywrightStudio.Services;

/// <summary>
/// One page that has the studio's script in it and has called home.
///
/// The old design launched a browser and owned the page. Now the page arrives on its own - the
/// user browsed to it - and the studio waits to be told which one to work with. That inversion
/// is the whole point: nothing here starts a process.
/// </summary>
public sealed class PageSession
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public PageSession(string id, WebSocket socket)
    {
        Id = id;
        _socket = socket;
    }

    public string Id { get; }
    public string Url { get; internal set; } = "";
    public string Title { get; internal set; } = "";
    public DateTimeOffset ConnectedUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Set when the studio opened this tab itself, so it can recognise the tab it just asked
    /// for among all the others the user has open.
    /// </summary>
    public string? Token { get; internal set; }

    /// <summary>The circuit that has taken this page, if any. Null means it is free.</summary>
    public string? ClaimedBy { get; internal set; }

    public bool IsClaimed => ClaimedBy is not null;

    /// <summary>Raised for every message the page sends up. The payload is the raw json.</summary>
    public event Action<string>? Message;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _waiting = new();
    private int _nextRequest;

    /// <summary>
    /// A socket is one-way traffic, but running a step needs an answer. Each request carries an
    /// id the page echoes back, so replies find the caller that is waiting for them.
    /// </summary>
    internal void Deliver(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idProp) &&
                root.TryGetProperty("type", out var typeProp) &&
                (typeProp.GetString() ?? "").EndsWith("Result", StringComparison.Ordinal))
            {
                var id = idProp.GetString() ?? "";
                if (_waiting.TryRemove(id, out var waiter))
                {
                    // Clone: the JsonDocument is disposed the moment this scope ends.
                    waiter.TrySetResult(root.Clone());
                    return;
                }
            }
        }
        catch (JsonException)
        {
            // Not json we understand - let a subscriber decide what to make of it.
        }

        Message?.Invoke(json);
    }

    /// <summary>Sends something the page must answer, and waits for that answer.</summary>
    public async Task<JsonElement?> RequestAsync(string type, object body, TimeSpan timeout,
                                                 CancellationToken ct = default)
    {
        var id = "r" + Interlocked.Increment(ref _nextRequest);
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = waiter;

        try
        {
            var envelope = JsonSerializer.SerializeToNode(body)?.AsObject();
            if (envelope is null) return null;
            envelope["type"] = type;
            envelope["id"] = id;

            await SendAsync(envelope.ToJsonString(), ct);

            using var timer = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, ct);
            await using (linked.Token.Register(() => waiter.TrySetCanceled()))
            {
                return await waiter.Task;
            }
        }
        catch (OperationCanceledException)
        {
            // The page never answered - it navigated, or the tab went away mid-step.
            return null;
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    /// <summary>Fails everything in flight, for when the page disappears.</summary>
    internal void CancelPending()
    {
        foreach (var key in _waiting.Keys)
            if (_waiting.TryRemove(key, out var waiter)) waiter.TrySetCanceled();
    }

    /// <summary>Pushes a message down to the page. Serialised - a socket allows one send at a time.</summary>
    public async Task SendAsync(string json, CancellationToken ct = default)
    {
        if (_socket.State != WebSocketState.Open) return;

        await _sendGate.WaitAsync(ct);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (Exception)
        {
            // A page that navigated away mid-send is normal, not an error worth surfacing.
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public override string ToString() => $"{Title} ({Url})";
}

/// <summary>
/// Every page currently holding a socket open to the studio. Pages come and go as the user
/// browses; a recording claims one and releases it when finished.
/// </summary>
public sealed class PageSessions
{
    private readonly ConcurrentDictionary<string, PageSession> _pages = new();
    private readonly ILogger<PageSessions> _log;

    public PageSessions(ILogger<PageSessions> log) => _log = log;

    /// <summary>Fires when a page connects, disconnects, or changes url.</summary>
    public event Action? Changed;

    public IReadOnlyList<PageSession> All() =>
        _pages.Values.OrderBy(p => p.ConnectedUtc).ToList();

    /// <summary>Pages nobody is recording with - what the user picks from.</summary>
    public IReadOnlyList<PageSession> Free() =>
        _pages.Values.Where(p => !p.IsClaimed).OrderBy(p => p.ConnectedUtc).ToList();

    public PageSession? Get(string id) => _pages.TryGetValue(id, out var p) ? p : null;

    public PageSession Add(string id, WebSocket socket)
    {
        var page = new PageSession(id, socket);
        _pages[id] = page;
        _log.LogInformation("Page connected: {Id}", id);
        Raise();
        return page;
    }

    public void Remove(string id)
    {
        if (_pages.TryRemove(id, out var page))
        {
            page.CancelPending();
            _log.LogInformation("Page gone: {Id} {Url}", id, page.Url);
            Raise();
        }
    }

    /// <summary>
    /// Takes a page for a recording or a run. Returns false if somebody else got there first,
    /// which is how two users are kept off the same tab.
    /// </summary>
    public bool TryClaim(string id, string owner)
    {
        var page = Get(id);
        if (page is null) return false;

        lock (page)
        {
            if (page.IsClaimed && page.ClaimedBy != owner) return false;
            page.ClaimedBy = owner;
        }

        Raise();
        return true;
    }

    public void Release(string id)
    {
        var page = Get(id);
        if (page is null) return;
        lock (page) page.ClaimedBy = null;
        Raise();
    }

    /// <summary>Releases everything a circuit was holding, for when that circuit goes away.</summary>
    public void ReleaseAllFor(string owner)
    {
        foreach (var page in _pages.Values.Where(p => p.ClaimedBy == owner))
            lock (page) page.ClaimedBy = null;
        Raise();
    }

    internal void Touch(PageSession page, string url, string title, string? token = null)
    {
        if (!string.IsNullOrEmpty(token) && page.Token != token) page.Token = token;
        if (page.Url == url && page.Title == title) return;
        page.Url = url;
        page.Title = title;
        Raise();
    }

    /// <summary>
    /// Waits for the tab the studio just opened to call home. Polls rather than using an event,
    /// because the page may already have connected before we start looking.
    /// </summary>
    public async Task<PageSession?> WaitForTokenAsync(string token, TimeSpan timeout,
                                                      CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var match = _pages.Values.FirstOrDefault(p => p.Token == token);
            if (match is not null) return match;
            await Task.Delay(150, ct);
        }
        return null;
    }

    private void Raise() => Changed?.Invoke();
}
