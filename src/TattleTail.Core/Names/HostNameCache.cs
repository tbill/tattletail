using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace TattleTail.Core;

/// <summary>Where a host name came from.</summary>
public enum HostNameSource
{
    /// <summary>A fixed label such as "localhost".</summary>
    Special,

    /// <summary>The name a program looked up in DNS before connecting (most accurate).</summary>
    DnsLookup,

    /// <summary>A reverse-DNS (PTR) lookup we made ourselves. Often a less friendly server name.</summary>
    ReverseDns,
}

/// <summary>
/// Remembers which website name belongs to which IP address.
/// </summary>
/// <remarks>
/// Names are learned two ways:
/// <list type="number">
/// <item>Watching DNS lookups as programs make them. If Chrome looks up "github.com" and gets
/// 140.82.112.3, a later connection from Chrome to 140.82.112.3 is to "github.com".
/// One IP can serve many sites (CDNs), so names learned for a specific program win.</item>
/// <item>For addresses we never saw looked up (connections opened before TattleTail started,
/// or browsers using encrypted DNS), an optional reverse-DNS lookup.</item>
/// </list>
/// </remarks>
public sealed class HostNameCache
{
    private static readonly TimeSpan FailedLookupRetry = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<IPAddress, (string Name, HostNameSource Source)> _byAddress = new();
    private readonly ConcurrentDictionary<(int ProcessId, IPAddress Address), string> _byProcessAndAddress = new();
    private readonly ConcurrentDictionary<IPAddress, DateTime> _pendingOrFailed = new();
    private readonly Func<IPAddress, Task<string?>> _reverseLookup;
    private readonly SemaphoreSlim _lookupSlots = new(4);
    private readonly Func<DateTime> _clock;
    private int _version;

    public HostNameCache()
        : this(DefaultReverseLookup, () => DateTime.UtcNow)
    {
    }

    internal HostNameCache(Func<IPAddress, Task<string?>> reverseLookup, Func<DateTime> clock)
    {
        _reverseLookup = reverseLookup;
        _clock = clock;
    }

    /// <summary>Whether to make reverse-DNS lookups for addresses we have no name for.</summary>
    public bool ReverseLookupEnabled { get; set; } = true;

    /// <summary>Goes up by one every time a name is learned, so callers can cheaply spot changes.</summary>
    public int Version => Volatile.Read(ref _version);

    public void LearnFromDnsLookup(int processId, string name, IEnumerable<IPAddress> addresses)
    {
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0)
        {
            return;
        }

        foreach (var raw in addresses)
        {
            var address = IpAddresses.Normalize(raw);
            _byAddress[address] = (name, HostNameSource.DnsLookup);
            if (processId > 0)
            {
                _byProcessAndAddress[(processId, address)] = name;
            }

            _pendingOrFailed.TryRemove(address, out _);
        }

        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// Returns the best known name for <paramref name="address"/>, or null if none is known yet.
    /// Pass the process id to prefer the name that particular program looked up.
    /// </summary>
    public string? GetName(IPAddress address, int processId = 0)
    {
        address = IpAddresses.Normalize(address);

        if (processId > 0 && _byProcessAndAddress.TryGetValue((processId, address), out var processName))
        {
            return processName;
        }

        if (_byAddress.TryGetValue(address, out var entry))
        {
            return entry.Name;
        }

        var special = IpAddresses.SpecialName(address);
        if (special is not null)
        {
            return special;
        }

        RequestReverseLookup(address);
        return null;
    }

    public HostNameSource? GetSource(IPAddress address) =>
        _byAddress.TryGetValue(IpAddresses.Normalize(address), out var entry) ? entry.Source : null;

    private void RequestReverseLookup(IPAddress address)
    {
        if (!ReverseLookupEnabled || IpAddresses.IsLinkLocal(address))
        {
            return;
        }

        var now = _clock();
        if (_pendingOrFailed.TryGetValue(address, out var since) && now - since < FailedLookupRetry)
        {
            return;
        }

        _pendingOrFailed[address] = now;
        _ = ReverseLookupAsync(address);
    }

    private async Task ReverseLookupAsync(IPAddress address)
    {
        await _lookupSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            var name = await _reverseLookup(address).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(name) && name != address.ToString())
            {
                // Never replace a name that came from a real DNS lookup.
                _byAddress.TryAdd(address, (name.TrimEnd('.'), HostNameSource.ReverseDns));
                _pendingOrFailed.TryRemove(address, out _);
                Interlocked.Increment(ref _version);
            }
        }
        catch
        {
            // Leave it in _pendingOrFailed so we retry later.
        }
        finally
        {
            _lookupSlots.Release();
        }
    }

    private static async Task<string?> DefaultReverseLookup(IPAddress address)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var entry = await Dns.GetHostEntryAsync(address.ToString(), AddressFamily.Unspecified, timeout.Token)
                .ConfigureAwait(false);
            return entry.HostName;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
