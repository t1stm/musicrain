using Gaida.Bot.Gaida;
using Gaida.Bot.Tools;

namespace Gaida.Bot.Players;

/// <summary>
/// The old <c>Bot/Audio/Queue.cs</c>, with <c>PlayableItem</c> become <see cref="Track" />. The
/// background processing loop is gone: the API resolves everything before it reaches the bot.
/// </summary>
public sealed class Playlist
{
    // Shuffle and Clear replace the list, so the lock cannot be the list itself: whoever is
    // holding the old instance would be locking something nobody else can see any more.
    private readonly Lock _gate = new();

    private List<Track> _items = [];

    public List<Track> Items => _items;

    public int Current { get; set; }

    public long Count
    {
        get
        {
            lock (_gate) return _items.Count;
        }
    }

    public bool EndOfQueue => Current >= Count;

    public int RandomSeed { get; private set; }

    public void AddToQueue(Track info)
    {
        lock (_gate) _items.Add(info);
    }

    public void AddToQueue(IEnumerable<Track> infos)
    {
        lock (_gate) _items.AddRange(infos);
    }

    public void AddToQueueNext(Track info)
    {
        lock (_gate) _items.Insert(Math.Clamp(Current + 1, 0, _items.Count), info);
    }

    public void AddToQueueNext(IEnumerable<Track> infos)
    {
        lock (_gate) _items.InsertRange(Math.Clamp(Current + 1, 0, _items.Count), infos);
    }

    public Track RemoveFromQueue(int index)
    {
        lock (_gate)
        {
            var item = _items[index];
            _items.Remove(item);
            return item;
        }
    }

    public Track RemoveFromQueue(Track item)
    {
        lock (_gate)
        {
            _items.Remove(item);
            return item;
        }
    }

    public Track RemoveFromQueue(string name)
    {
        lock (_gate)
        {
            var item = _items.First(vi => LevenshteinDistance.ComputeStrict(vi.DisplayName, name) < 3);
            _items.Remove(item);
            return item;
        }
    }

    public Track GetWithString(string name)
    {
        lock (_gate)
        {
            return _items.First(vi => LevenshteinDistance.ComputeLean(vi.DisplayName, name) < 3);
        }
    }

    public void Shuffle()
    {
        lock (_gate)
        {
            var random = new Random();
            var queue = _items.OrderBy(_ => random.Next()).ToList();
            var current = GetCurrent();
            if (current is not null)
            {
                queue.Remove(current);
                queue.Insert(0, current);
            }

            _items = queue;
            Current = 0;
        }
    }

    public void ShuffleWithSeed(int seed)
    {
        lock (_gate)
        {
            if (seed == -555) seed = new Random().Next(int.MaxValue);
            var queue = _items.OrderBy(_ => new Random(seed).Next()).ToList();
            var current = GetCurrent();
            if (current is not null)
            {
                queue.Remove(current);
                queue.Insert(0, current);
            }

            _items = queue;
            Current = 0;
            RandomSeed = seed;
        }
    }

    public void Clear()
    {
        var current = GetCurrent();
        lock (_gate)
        {
            Current = 0;
            _items = [];
            if (current is not null) _items.Add(current);
        }
    }

    public Track? GetCurrent()
    {
        lock (_gate)
        {
            if (_items.Count == 0) return null;
            return Current >= _items.Count || Current < 0 ? null : _items[Current];
        }
    }

    public Track? GetNext()
    {
        lock (_gate)
        {
            return Current >= _items.Count - 1 ? null : _items[Current + 1];
        }
    }

    public bool Move(int from, int to, out Track item)
    {
        item = null!;
        try
        {
            lock (_gate)
            {
                var moved = _items[from];
                _items.Remove(moved);
                _items.Insert(to, moved);
                item = moved;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Move(string first, string second, out Track itemOne, out Track itemTwo)
    {
        itemOne = null!;
        itemTwo = null!;
        try
        {
            lock (_gate)
            {
                var one = _items.FirstOrDefault(vi =>
                    LevenshteinDistance.ComputeStrict(vi.DisplayName, first.Trim()) < vi.DisplayName.Length * 0.2);
                var two = _items.FirstOrDefault(vi =>
                    LevenshteinDistance.ComputeStrict(vi.DisplayName, second.Trim()) < vi.DisplayName.Length * 0.2);

                if (one is null || two is null)
                {
                    one = _items.MinBy(vi => LevenshteinDistance.ComputeStrict(vi.Name, first.Trim()));
                    two = _items.MinBy(vi => LevenshteinDistance.ComputeStrict(vi.Name, second.Trim()));
                    if (one is null || two is null) return false;
                }

                var indexOne = _items.IndexOf(one);
                var indexTwo = _items.IndexOf(two);
                _items[indexOne] = two;
                _items[indexTwo] = one;
                itemTwo = _items[indexOne];
                itemOne = _items[indexTwo];
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>What the <c>list</c> command sends as <c>queue.txt</c>, dashes and all.</summary>
    public override string ToString()
    {
        var result = "";
        lock (_gate)
        {
            if (_items.Count < 1) return Text.TheQueueIsEmpty();

            for (var index = 0; index < _items.Count; index++)
            {
                var item = _items[index];
                if (index == Current)
                {
                    result += new string('-', item.DisplayName.Length + 8) + '\n';
                    result += $"({index + 1}) - \"{item.DisplayName}\"\n";
                    result += new string('-', item.DisplayName.Length + 8) + '\n';
                }
                else
                {
                    result += $"({index + 1}) - \"{item.DisplayName}\"\n";
                }
            }
        }

        return result;
    }
}
