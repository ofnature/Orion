using System;
using System.Collections.Generic;

namespace Orion.Services.Farm;

/// <summary>One Trophy Hunt for one item: what the list run starts, watches and stops.</summary>
public interface ITrophyHunt
{
    /// <summary>Travelling to the mobs, or farming them.</summary>
    bool IsRunning { get; }

    string StatusLine { get; }

    /// <summary>Set up a hunt for an item and start it. Returns why not, or null when it started.</summary>
    string? Start(uint itemId, string name, int targetCount);

    void Stop(string reason);
}

/// <summary>An item a list run still has to hunt.</summary>
public readonly record struct TrophyWant(uint ItemId, string Name, int Target);

/// <summary>
/// Runs hunting lists: everything the bag is short of across the lists given, one item at a time
/// through Trophy Hunt (<see cref="ITrophyHunt"/>). An item that cannot be started, or whose hunt
/// stops short, is said in chat and the next is tried: one bad row does not end the run. The
/// character dying does.
/// </summary>
public sealed class TrophyListRunner : IDisposable
{
    private readonly ITrophyHunt _hunt;
    private readonly Func<uint, int> _held;
    private readonly Func<bool> _isDead;
    private readonly Func<bool> _otherRunBusy;
    private readonly Action<string> _say;

    private readonly Queue<TrophyWant> _queue = new();
    private TrophyWant? _current;
    private int _total;
    private int _filled;
    private int _skipped;

    /// <summary>
    /// A run ended. True when it worked through its items on its own with at least one filled;
    /// false when it was stopped, or filled nothing.
    /// </summary>
    public event Action<bool>? Ended;

    public bool IsRunning { get; private set; }

    /// <summary>The item being hunted now, for the list's row highlight.</summary>
    public uint? CurrentItem => _current?.ItemId;

    /// <summary>"item 2 of 5", while running.</summary>
    public string Progress => IsRunning ? $"item {_total - _queue.Count} of {_total}" : string.Empty;

    /// <param name="held">How many of an item the bags hold.</param>
    /// <param name="otherRunBusy">Something else of Orion's is driving the character.</param>
    public TrophyListRunner(
        ITrophyHunt hunt, Func<uint, int> held, Func<bool> isDead, Func<bool> otherRunBusy, Action<string> say)
    {
        _hunt = hunt;
        _held = held;
        _isDead = isDead;
        _otherRunBusy = otherRunBusy;
        _say = say;
    }

    /// <summary>
    /// What the lists still want: one entry per item, at the highest count any enabled list asks
    /// for, in the order the items first appear, leaving out what the bag already holds.
    /// </summary>
    public static List<TrophyWant> Short(IEnumerable<TrophyList> lists, Func<uint, int> held)
    {
        var order = new List<uint>();
        var wanted = new Dictionary<uint, TrophyWant>();
        foreach (var list in lists)
        {
            if (!list.Enabled)
                continue;
            foreach (var item in list.Items)
            {
                if (item.ItemId == 0 || item.TargetCount <= 0)
                    continue;
                if (!wanted.TryGetValue(item.ItemId, out var seen))
                {
                    order.Add(item.ItemId);
                    wanted[item.ItemId] = new TrophyWant(item.ItemId, item.Name, item.TargetCount);
                }
                else if (item.TargetCount > seen.Target)
                {
                    wanted[item.ItemId] = seen with { Target = item.TargetCount };
                }
            }
        }

        var result = new List<TrophyWant>();
        foreach (var id in order)
        {
            if (held(id) < wanted[id].Target)
                result.Add(wanted[id]);
        }
        return result;
    }

    /// <summary>Starts on the lists' short items. Returns why not, or null when it started.</summary>
    public string? Begin(IEnumerable<TrophyList> lists)
    {
        if (IsRunning)
            return "already running";
        if (_hunt.IsRunning || _otherRunBusy())
            return "another run is going — stop it first";

        var items = Short(lists, _held);
        if (items.Count == 0)
            return "nothing on the list is short";

        _queue.Clear();
        foreach (var item in items)
            _queue.Enqueue(item);
        _current = null;
        _total = items.Count;
        _filled = 0;
        _skipped = 0;
        IsRunning = true;
        _say($"Hunting list started: {_total} item(s) short.");
        Next();
        return IsRunning ? null : "none of the items could be started (see chat)";
    }

    /// <summary>Framework-thread tick.</summary>
    public void Tick()
    {
        if (!IsRunning || _current is not { } item || _hunt.IsRunning)
            return;

        // The hunt for it is over. Filled is the bag's word, not the hunt's.
        _current = null;
        if (_held(item.ItemId) >= item.Target)
        {
            _filled++;
        }
        else if (_isDead())
        {
            End("the character died", completed: false);
            return;
        }
        else
        {
            Skip(item, "the hunt stopped before the count was reached");
        }
        Next();
    }

    /// <summary>Starts the next item that can be started, or ends the run when none is left.</summary>
    private void Next()
    {
        while (_queue.Count > 0)
        {
            var item = _queue.Dequeue();
            if (_held(item.ItemId) >= item.Target)
            {
                _filled++; // came in while another item was hunted
                continue;
            }
            if (_hunt.Start(item.ItemId, item.Name, item.Target) is { } error)
            {
                Skip(item, error);
                continue;
            }
            _current = item;
            _say($"Hunting list: {item.Name} ×{item.Target} ({Progress}).");
            return;
        }
        End("the list is done", completed: _filled > 0);
    }

    private void Skip(TrophyWant item, string reason)
    {
        _skipped++;
        _say($"Hunting list: skipping {item.Name} — {reason}.");
    }

    public void Stop(string reason)
    {
        if (!IsRunning)
            return;
        if (_hunt.IsRunning)
            _hunt.Stop("hunting list stopped");
        End(reason, completed: false);
    }

    private void End(string reason, bool completed)
    {
        IsRunning = false;
        _queue.Clear();
        _current = null;
        _say($"Hunting list stopped — {reason}. {_filled} item(s) filled"
             + (_skipped > 0 ? $", {_skipped} skipped." : "."));
        Ended?.Invoke(completed);
    }

    public void Dispose() => Stop("plugin unloading");
}
