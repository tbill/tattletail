namespace TattleTail.Core;

/// <summary>
/// One run of a program, from the moment it starts until it exits.
/// </summary>
/// <remarks>
/// Windows hands a process id back out once its program has exited, like a hotel giving a room
/// number to the next guest. Traffic is therefore counted against the guest (this value), not
/// the room number, so a new program never inherits an old program's name or totals.
/// </remarks>
public readonly record struct ProcessInstance(int Id);

/// <summary>
/// Keeps track of which program currently owns each process id, and remembers the name of
/// programs that have exited.
/// </summary>
/// <remarks>
/// It learns about programs two ways. The kernel's process events (see
/// <see cref="EtwTrafficMonitor"/>) say exactly when a program starts and stops and what it is
/// called, which also names programs that exit before anyone could look them up. Everything
/// else is looked up from the operating system the first time its details are needed.
/// All members are thread-safe.
/// </remarks>
public sealed class ProcessRegistry
{
    public const string UnknownName = "(exited)";

    private sealed class Entry
    {
        public required int ProcessId { get; init; }
        public string? EventName { get; set; }
        public ProcessDetails? Details { get; set; }
        public bool Exited { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, ProcessInstance> _current = new();
    private readonly Dictionary<ProcessInstance, Entry> _entries = new();
    private readonly Func<int, ProcessDetails?> _lookup;
    private int _lastId;

    public ProcessRegistry()
        : this(ProcessNameResolver.Lookup)
    {
    }

    /// <param name="lookup">Asks the operating system about a process id; null if nothing is running under it.</param>
    public ProcessRegistry(Func<int, ProcessDetails?> lookup)
    {
        _lookup = lookup;
    }

    /// <summary>The program currently using <paramref name="processId"/>. Fast: no operating system calls.</summary>
    public ProcessInstance GetInstance(int processId)
    {
        lock (_gate)
        {
            return _current.TryGetValue(processId, out var instance) ? instance : Add(processId, null);
        }
    }

    /// <summary>A new program started under <paramref name="processId"/>; whatever had that id before is gone.</summary>
    public void ProcessStarted(int processId, string? imageName)
    {
        lock (_gate)
        {
            if (_current.TryGetValue(processId, out var previous))
            {
                _entries[previous].Exited = true;
            }

            Add(processId, imageName);
        }
    }

    /// <summary>A program that was already running was reported (the list Windows sends when listening begins).</summary>
    public void ProcessSeen(int processId, string? imageName)
    {
        lock (_gate)
        {
            if (_current.TryGetValue(processId, out var instance))
            {
                _entries[instance].EventName ??= CleanName(imageName);
            }
            else
            {
                Add(processId, imageName);
            }
        }
    }

    /// <summary>
    /// The program using <paramref name="processId"/> exited. Its id keeps pointing at it until a
    /// new program starts there, so the last packets of its connections are still counted for it.
    /// </summary>
    public void ProcessStopped(int processId, string? imageName = null)
    {
        lock (_gate)
        {
            if (_current.TryGetValue(processId, out var instance))
            {
                var entry = _entries[instance];
                entry.Exited = true;
                entry.EventName ??= CleanName(imageName);
            }
        }
    }

    /// <summary>
    /// For when process events aren't available: treats every program whose id isn't in
    /// <paramref name="liveProcessIds"/> as exited, so a reused id is looked up afresh.
    /// </summary>
    public void MarkExitedExcept(IReadOnlySet<int> liveProcessIds)
    {
        lock (_gate)
        {
            foreach (var (processId, instance) in _current.Where(kv => !liveProcessIds.Contains(kv.Key)).ToList())
            {
                _entries[instance].Exited = true;
                _current.Remove(processId);
            }
        }
    }

    public bool HasExited(ProcessInstance instance)
    {
        lock (_gate)
        {
            return !_entries.TryGetValue(instance, out var entry) || entry.Exited;
        }
    }

    /// <summary>Name, path and process id of <paramref name="instance"/>. The first call may ask the operating system.</summary>
    public ProcessDetails GetDetails(ProcessInstance instance)
    {
        int processId;
        bool exited;
        lock (_gate)
        {
            if (!_entries.TryGetValue(instance, out var entry))
            {
                return new ProcessDetails(0, UnknownName, null);
            }

            if (entry.Details is not null)
            {
                return entry.Details;
            }

            processId = entry.ProcessId;
            exited = entry.Exited;
        }

        // Once a program has exited its id may already belong to something else, so don't ask.
        // The lookup can be slow, which is why it happens outside the lock.
        var found = exited ? null : _lookup(processId);

        lock (_gate)
        {
            if (!_entries.TryGetValue(instance, out var entry))
            {
                return found ?? new ProcessDetails(processId, UnknownName, null);
            }

            if (entry.Details is null)
            {
                if (found is null)
                {
                    entry.Exited = true;
                }

                entry.Details = found ?? new ProcessDetails(processId, entry.EventName ?? UnknownName, null);
            }

            return entry.Details;
        }
    }

    /// <summary>Forgets exited programs that nothing refers to any more.</summary>
    public void Retain(IReadOnlySet<ProcessInstance> inUse)
    {
        lock (_gate)
        {
            foreach (var (instance, entry) in _entries.Where(kv => kv.Value.Exited && !inUse.Contains(kv.Key)).ToList())
            {
                _entries.Remove(instance);
                if (_current.TryGetValue(entry.ProcessId, out var current) && current == instance)
                {
                    _current.Remove(entry.ProcessId);
                }
            }
        }
    }

    private ProcessInstance Add(int processId, string? imageName)
    {
        var instance = new ProcessInstance(++_lastId);
        _entries[instance] = new Entry { ProcessId = processId, EventName = CleanName(imageName) };
        _current[processId] = instance;
        return instance;
    }

    /// <summary>"C:\Apps\Curl.EXE" and "curl.exe" both become "curl", matching what Windows calls the process.</summary>
    private static string? CleanName(string? imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName))
        {
            return null;
        }

        var name = imageName.Trim();
        name = name[(name.LastIndexOfAny(['\\', '/']) + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Length == 0 ? null : name;
    }
}
