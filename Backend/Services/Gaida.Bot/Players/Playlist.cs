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
    public List<Track> Items { get; private set; } = [];

    public int Current { get; set; }

    public long Count
    {
        get
        {
            lock (_gate) return Items.Count;
        }
    }

    public bool EndOfQueue => Current >= Count;

    public int RandomSeed { get; private set; }

    public void AddToQueue(Track info)
    {
        lock (_gate) Items.Add(info);
    }

    public void AddToQueue(IEnumerable<Track> infos)
    {
        lock (_gate) Items.AddRange(infos);
    }

    public void AddToQueueNext(Track info)
    {
        lock (_gate) Items.Insert(Math.Clamp(Current + 1, 0, Items.Count), info);
    }

    public void AddToQueueNext(IEnumerable<Track> infos)
    {
        lock (_gate) Items.InsertRange(Math.Clamp(Current + 1, 0, Items.Count), infos);
    }

    public Track RemoveFromQueue(int index)
    {
        lock (_gate)
        {
            var item = Items[index];
            Items.Remove(item);
            return item;
        }
    }

    public Track RemoveFromQueue(Track item)
    {
        lock (_gate)
        {
            Items.Remove(item);
            return item;
        }
    }

    public Track GetWithString(string name)
    {
        lock (_gate)
        {
            return Items.First(vi => LevenshteinDistance.ComputeLean(vi.DisplayName, name) < 3);
        }
    }

    public void Shuffle()
    {
        lock (_gate)
        {
            var random = new Random();
            var queue = Items.OrderBy(_ => random.Next()).ToList();
            var current = GetCurrent();
            if (current is not null)
            {
                queue.Remove(current);
                queue.Insert(0, current);
            }

            Items = queue;
            Current = 0;
        }
    }

    public void ShuffleWithSeed(int seed)
    {
        lock (_gate)
        {
            if (seed == -555) seed = new Random().Next(int.MaxValue);
            var queue = Items.OrderBy(_ => new Random(seed).Next()).ToList();
            var current = GetCurrent();
            if (current is not null)
            {
                queue.Remove(current);
                queue.Insert(0, current);
            }

            Items = queue;
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
            Items = [];
            if (current is not null) Items.Add(current);
        }
    }

    public Track? GetCurrent()
    {
        lock (_gate)
        {
            if (Items.Count == 0) return null;
            return Current >= Items.Count || Current < 0 ? null : Items[Current];
        }
    }

    public Track? GetNext()
    {
        lock (_gate)
        {
            return Current >= Items.Count - 1 ? null : Items[Current + 1];
        }
    }

    public bool Move(int from, int to, out Track item)
    {
        item = null!;
        try
        {
            lock (_gate)
            {
                var moved = Items[from];
                Items.Remove(moved);
                Items.Insert(to, moved);
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
                var one = Items.FirstOrDefault(vi =>
                    LevenshteinDistance.ComputeStrict(vi.DisplayName, first.Trim()) < vi.DisplayName.Length * 0.2);
                var two = Items.FirstOrDefault(vi =>
                    LevenshteinDistance.ComputeStrict(vi.DisplayName, second.Trim()) < vi.DisplayName.Length * 0.2);

                if (one is null || two is null)
                {
                    one = Items.MinBy(vi => LevenshteinDistance.ComputeStrict(vi.Name, first.Trim()));
                    two = Items.MinBy(vi => LevenshteinDistance.ComputeStrict(vi.Name, second.Trim()));
                    if (one is null || two is null) return false;
                }

                var indexOne = Items.IndexOf(one);
                var indexTwo = Items.IndexOf(two);
                Items[indexOne] = two;
                Items[indexTwo] = one;
                itemTwo = Items[indexOne];
                itemOne = Items[indexTwo];
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
            if (Items.Count < 1) return Text.TheQueueIsEmpty();

            for (var index = 0; index < Items.Count; index++)
            {
                var item = Items[index];
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
