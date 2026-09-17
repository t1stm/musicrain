using Gaida.Bot.Gaida;
using Gaida.Bot.Tools;

namespace Gaida.Bot.Players;

/// <summary>
/// The old <c>Bot/Audio/Queue.cs</c>, with <c>PlayableItem</c> become <see cref="Track" />. The
/// background processing loop is gone: the API resolves everything before it reaches the bot.
/// </summary>
public sealed class PlayerQueue
{
    // Shuffle and Clear replace the list, so the lock cannot be the list itself: whoever is
    // holding the old instance would be locking something nobody else can see any more.
    private readonly Lock gate = new();

    private List<Track> items = [];

    public List<Track> Items => this.items;

    public int Current { get; set; }

    public long Count
    {
        get
        {
            lock (this.gate) return this.items.Count;
        }
    }

    public bool EndOfQueue => this.Current >= this.Count;

    public int RandomSeed { get; private set; }

    public void AddToQueue(Track info)
    {
        lock (this.gate) this.items.Add(info);
    }

    public void AddToQueue(IEnumerable<Track> infos)
    {
        lock (this.gate) this.items.AddRange(infos);
    }

    public void AddToQueueNext(Track info)
    {
        lock (this.gate) this.items.Insert(Math.Clamp(this.Current + 1, 0, this.items.Count), info);
    }

    public void AddToQueueNext(IEnumerable<Track> infos)
    {
        lock (this.gate) this.items.InsertRange(Math.Clamp(this.Current + 1, 0, this.items.Count), infos);
    }

    public Track RemoveFromQueue(int index)
    {
        lock (this.gate)
        {
            var item = this.items[index];
            this.items.Remove(item);
            return item;
        }
    }

    public Track RemoveFromQueue(Track item)
    {
        lock (this.gate)
        {
            this.items.Remove(item);
            return item;
        }
    }

    public Track RemoveFromQueue(string name)
    {
        lock (this.gate)
        {
            var item = this.items.First(vi => LevenshteinDistance.ComputeStrict(vi.DisplayName, name) < 3);
            this.items.Remove(item);
            return item;
        }
    }

    public Track GetWithString(string name)
    {
        lock (this.gate)
        {
            return this.items.First(vi => LevenshteinDistance.ComputeLean(vi.DisplayName, name) < 3);
        }
    }

    public void Shuffle()
    {
        lock (this.gate)
        {
            var random = new Random();
            var queue = this.items.OrderBy(_ => random.Next()).ToList();
            var current = GetCurrent();
            if (current is not null)
            {
                queue.Remove(current);
                queue.Insert(0, current);
            }

            this.items = queue;
            this.Current = 0;
        }
    }

    public void ShuffleWithSeed(int seed)
    {
        lock (this.gate)
        {
            if (seed == -555) seed = new Random().Next(int.MaxValue);
            var queue = this.items.OrderBy(_ => new Random(seed).Next()).ToList();
            var current = GetCurrent();
            if (current is not null)
            {
                queue.Remove(current);
                queue.Insert(0, current);
            }

            this.items = queue;
            this.Current = 0;
            this.RandomSeed = seed;
        }
    }

    public void Clear()
    {
        var current = GetCurrent();
        lock (this.gate)
        {
            this.Current = 0;
            this.items = [];
            if (current is not null) this.items.Add(current);
        }
    }

    public Track? GetCurrent()
    {
        lock (this.gate)
        {
            if (this.items.Count == 0) return null;
            return this.Current >= this.items.Count || this.Current < 0 ? null : this.items[this.Current];
        }
    }

    public Track? GetNext()
    {
        lock (this.gate)
        {
            return this.Current >= this.items.Count - 1 ? null : this.items[this.Current + 1];
        }
    }

    public bool Move(int from, int to, out Track item)
    {
        item = null!;
        try
        {
            lock (this.gate)
            {
                var moved = this.items[from];
                this.items.Remove(moved);
                this.items.Insert(to, moved);
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
            lock (this.gate)
            {
                var one = this.items.FirstOrDefault(vi =>
                    LevenshteinDistance.ComputeStrict(vi.DisplayName, first.Trim()) < vi.DisplayName.Length * 0.2);
                var two = this.items.FirstOrDefault(vi =>
                    LevenshteinDistance.ComputeStrict(vi.DisplayName, second.Trim()) < vi.DisplayName.Length * 0.2);

                if (one is null || two is null)
                {
                    one = this.items.MinBy(vi => LevenshteinDistance.ComputeStrict(vi.Name, first.Trim()));
                    two = this.items.MinBy(vi => LevenshteinDistance.ComputeStrict(vi.Name, second.Trim()));
                    if (one is null || two is null) return false;
                }

                var indexOne = this.items.IndexOf(one);
                var indexTwo = this.items.IndexOf(two);
                this.items[indexOne] = two;
                this.items[indexTwo] = one;
                itemTwo = this.items[indexOne];
                itemOne = this.items[indexTwo];
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
        lock (this.gate)
        {
            if (this.items.Count < 1) return Text.TheQueueIsEmpty();

            for (var index = 0; index < this.items.Count; index++)
            {
                var item = this.items[index];
                if (index == this.Current)
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
