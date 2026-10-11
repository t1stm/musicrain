using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
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

    /// <summary>How many edits a typed name may be off by and still find a queued track.</summary>
    private const int NameEdits = 3;

    /// <summary>For a move, the edits allowed as a share of the track's display name.</summary>
    private const double MoveEditShare = 0.2;
    private List<Track> _items = [];

    /// <summary>A copy, so a command reading it cannot be torn by the play loop changing the queue.</summary>
    public IReadOnlyList<Track> Items
    {
        get
        {
            lock (_gate) return [.. _items];
        }
    }

    public int Current { get; set; }

    public int Count
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

    public int IndexOf(Track item)
    {
        lock (_gate) return _items.IndexOf(item);
    }

    public Track GetWithString(string name)
    {
        lock (_gate)
        {
            return _items.First(vi => LevenshteinDistance.ComputeLean(vi.DisplayName, name) < NameEdits);
        }
    }

    public void Shuffle()
    {
        lock (_gate)
        {
            List<Track> queue = [.. _items];
            Random.Shared.Shuffle(CollectionsMarshal.AsSpan(queue));
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

    /// <summary>The seed that asks for a random one.</summary>
    public const int AnySeed = -555;

    public void ShuffleWithSeed(int seed)
    {
        lock (_gate)
        {
            if (seed == AnySeed) seed = Random.Shared.Next(int.MaxValue);
            Track[] shuffled = [.. _items];
            new Random(seed).Shuffle(shuffled);

            var queue = shuffled.ToList();
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
            return Current + 1 < 0 || Current >= _items.Count - 1 ? null : _items[Current + 1];
        }
    }

    public bool Move(int from, int to, [NotNullWhen(true)] out Track? item)
    {
        lock (_gate)
        {
            if (from < 0 || from >= _items.Count || to < 0 || to >= _items.Count)
            {
                item = null;
                return false;
            }

            item = _items[from];
            _items.Remove(item);
            _items.Insert(to, item);
            return true;
        }
    }

    public bool Move(string first, string second, [NotNullWhen(true)] out Track? itemOne,
        [NotNullWhen(true)] out Track? itemTwo)
    {
        itemOne = null;
        itemTwo = null;

        lock (_gate)
        {
            var one = _items.FirstOrDefault(vi =>
                LevenshteinDistance.ComputeStrict(vi.DisplayName, first.Trim()) < vi.DisplayName.Length * MoveEditShare);
            var two = _items.FirstOrDefault(vi =>
                LevenshteinDistance.ComputeStrict(vi.DisplayName, second.Trim()) < vi.DisplayName.Length * MoveEditShare);

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

    /// <summary>What the <c>list</c> command sends as <c>queue.txt</c>, dashes and all.</summary>
    public override string ToString()
    {
        var result = new StringBuilder();
        lock (_gate)
        {
            if (_items.Count < 1) return Text.TheQueueIsEmpty;

            for (var index = 0; index < _items.Count; index++)
            {
                var item = _items[index];
                var line = $"({index + 1}) - \"{item.DisplayName}\"\n";
                if (index != Current)
                {
                    result.Append(line);
                    continue;
                }

                var rule = new string('-', item.DisplayName.Length + 8) + '\n';
                result.Append(rule).Append(line).Append(rule);
            }
        }

        return result.ToString();
    }
}
