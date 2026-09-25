using System.Collections.Concurrent;

namespace DigitalBanking.Chat.Api.Application;

public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _connections = new();

    public bool Connect(Guid userId, string connectionId)
    {
        var connections = _connections.GetOrAdd(userId, _ => []);
        lock (connections)
        {
            var wasOffline = connections.Count == 0;
            connections.Add(connectionId);
            return wasOffline;
        }
    }

    public bool Disconnect(Guid userId, string connectionId)
    {
        if (!_connections.TryGetValue(userId, out var connections))
        {
            return true;
        }

        lock (connections)
        {
            connections.Remove(connectionId);
            if (connections.Count > 0)
            {
                return false;
            }
        }

        _connections.TryRemove(userId, out _);
        return true;
    }

    public bool IsOnline(Guid userId) => _connections.ContainsKey(userId);

    public IReadOnlyList<Guid> OnlineUserIds() => _connections.Keys.ToList();
}
