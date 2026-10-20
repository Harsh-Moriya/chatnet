using System.Collections;
using System.Diagnostics;

namespace ChatNET.API.Tests.Infrastructure;

// Where the in-memory trace exporter puts the spans it receives. The exporter runs on whichever
// thread finished the span while the test reads from its own, so every access goes through a lock.
public sealed class ExportedActivities : ICollection<Activity>
{
    private readonly object _gate = new();
    private readonly List<Activity> _items = [];

    public int Count
    {
        get
        {
            lock (_gate)
                return _items.Count;
        }
    }

    public bool IsReadOnly => false;

    public void Add(Activity item)
    {
        lock (_gate)
            _items.Add(item);
    }

    public IReadOnlyList<Activity> Snapshot()
    {
        lock (_gate)
            return _items.ToArray();
    }

    // Spans are exported when they end, which can be a moment after the response reaches the client.
    public async Task<IReadOnlyList<Activity>> WaitForAsync(Predicate<Activity> match, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (DateTime.UtcNow < until)
        {
            var snapshot = Snapshot();
            if (snapshot.Any(a => match(a)))
                return snapshot;

            await Task.Delay(25);
        }

        return Snapshot();
    }

    public void Clear()
    {
        lock (_gate)
            _items.Clear();
    }

    public bool Contains(Activity item) => Snapshot().Contains(item);

    public void CopyTo(Activity[] array, int arrayIndex) => Snapshot().ToArray().CopyTo(array, arrayIndex);

    public bool Remove(Activity item)
    {
        lock (_gate)
            return _items.Remove(item);
    }

    public IEnumerator<Activity> GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
