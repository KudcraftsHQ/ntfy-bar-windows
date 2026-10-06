namespace NtfyBar.Core;

public enum IngestOutcome
{
    /// <summary>Already seen (de-duplicated by id).</summary>
    Duplicate,
    /// <summary>Message on the sync topic: never stored, never notified.</summary>
    SyncTopic,
    Added,
}

public sealed record IngestResult(IngestOutcome Outcome, bool Notify = false, bool SyncSignal = false);

/// <summary>Message list, read state, de-duplication and resume position. Mirrors ntfy-bar's
/// <c>AppModel.ingest</c>. Not thread-safe: call from one thread (the UI thread).</summary>
public sealed class MessageStore
{
    public const int MaxEntries = 2000;
    public const int MaxSeenIds = 4000;
    /// <summary>Messages older than (launch − grace) are backlog: listed, but never notified.</summary>
    public static readonly TimeSpan NotifyGrace = TimeSpan.FromSeconds(60);

    private readonly List<Entry> _entries;
    private readonly List<string> _seenOrder;
    private readonly HashSet<string> _seen;
    private readonly DateTimeOffset _launch;
    private readonly Dictionary<string, long> _quietBefore = new();

    public string? LastMessageId { get; private set; }
    public long? LastMessageTime { get; private set; }
    public IReadOnlyList<Entry> Entries => _entries;

    public MessageStore(PersistedState state, DateTimeOffset launchTime)
    {
        _entries = state.Entries.OrderByDescending(e => e.Message.Time).ToList();
        _seenOrder = state.SeenIds.ToList();
        _seen = new HashSet<string>(_seenOrder);
        _seen.UnionWith(_entries.Select(e => e.Id));
        LastMessageId = state.LastMessageId;
        LastMessageTime = state.LastMessageTime;
        _launch = launchTime;
    }

    /// <param name="updateCursor">False for backfill polls: they must not move the stream's resume position.</param>
    public IngestResult Ingest(NtfyMessage m, AppSettings settings, bool updateCursor = true)
    {
        if (settings.StreamSyncTopic is { } st && m.Topic == st)
            return new IngestResult(IngestOutcome.SyncTopic, SyncSignal: StreamParser.IsSyncSignal(m));

        if (updateCursor && m.Time >= (LastMessageTime ?? 0))
        {
            LastMessageTime = m.Time;
            LastMessageId = m.Id;
        }
        if (_seen.Contains(m.Id)) return new IngestResult(IngestOutcome.Duplicate);
        MarkSeen(m.Id);

        var index = _entries.FindIndex(e => e.Message.Time <= m.Time);
        _entries.Insert(index < 0 ? _entries.Count : index, new Entry { Message = m, Read = false });
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);

        var fresh = m.Date >= _launch - NotifyGrace
            && !(_quietBefore.TryGetValue(m.Topic, out var quiet) && m.Time < quiet);
        var notify = fresh && !settings.IsMuted(m.Topic) && settings.IsEnabled(m.Topic);
        return new IngestResult(IngestOutcome.Added, Notify: notify);
    }

    /// <summary>A topic new to this client: its existing history (anything published before
    /// <paramref name="addedAt"/>) is listed but never notified, even if the stream replays it.</summary>
    public void QuietHistory(string topic, DateTimeOffset addedAt) =>
        _quietBefore[topic] = addedAt.ToUnixTimeSeconds();

    private void MarkSeen(string id)
    {
        _seen.Add(id);
        _seenOrder.Add(id);
        if (_seenOrder.Count <= MaxSeenIds) return;
        var drop = _seenOrder.Count - MaxSeenIds;
        foreach (var old in _seenOrder.Take(drop)) _seen.Remove(old);
        _seenOrder.RemoveRange(0, drop);
        _seen.UnionWith(_entries.Select(e => e.Id));
    }

    public int UnreadCount(AppSettings settings) =>
        _entries.Count(e => !e.Read && !settings.IsMuted(e.Message.Topic) && settings.IsEnabled(e.Message.Topic));

    public int UnreadCount(AppSettings settings, string topic) =>
        _entries.Count(e => !e.Read && e.Message.Topic == topic);

    public bool MarkRead(IEnumerable<string> ids)
    {
        var set = ids as ISet<string> ?? new HashSet<string>(ids);
        var changed = false;
        foreach (var e in _entries)
        {
            if (!e.Read && set.Contains(e.Id)) { e.Read = true; changed = true; }
        }
        return changed;
    }

    public bool MarkAllRead() => MarkRead(_entries.Select(e => e.Id).ToHashSet());

    public void Clear(string? topic = null)
    {
        if (topic is null) _entries.Clear();
        else _entries.RemoveAll(e => e.Message.Topic == topic);
    }

    public Entry? Find(string id) => _entries.FirstOrDefault(e => e.Id == id);

    /// <summary>The newest icon seen for a topic (rows without one borrow it, as in ntfy-bar).</summary>
    public string? LatestIcon(string topic) =>
        _entries.FirstOrDefault(e => e.Message.Topic == topic && e.Message.IconUrl is not null)?.Message.Icon;

    public PersistedState ToState() => new()
    {
        Entries = _entries.ToList(),
        LastMessageId = LastMessageId,
        LastMessageTime = LastMessageTime,
        SeenIds = _seenOrder.ToList(),
    };
}
