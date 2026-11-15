using System.Net;
using TattleTail.Core;
using Xunit;

namespace TattleTail.Core.Tests;

public class DnsQueryResultsParserTests
{
    [Fact]
    public void ParsesAddressesAndSkipsAliases()
    {
        var addresses = DnsQueryResultsParser.ParseAddresses(
            "type:  5 github.map.fastly.net;::ffff:140.82.112.3;2606:50c0:8000::154;");

        Assert.Equal([IPAddress.Parse("140.82.112.3"), IPAddress.Parse("2606:50c0:8000::154")], addresses);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("type:  5 only.an.alias;")]
    public void EmptyOrAliasOnly_GivesNoAddresses(string? results) =>
        Assert.Empty(DnsQueryResultsParser.ParseAddresses(results));
}

public class HostNameCacheTests
{
    private static HostNameCache Create(Func<IPAddress, Task<string?>>? reverse = null) =>
        new(reverse ?? (_ => Task.FromResult<string?>(null)), () => DateTime.UtcNow);

    [Fact]
    public void DnsLookup_NamesTheAddress()
    {
        var cache = Create();
        cache.LearnFromDnsLookup(10, "github.com.", [IPAddress.Parse("::ffff:140.82.112.3")]);

        Assert.Equal("github.com", cache.GetName(IPAddress.Parse("140.82.112.3")));
        Assert.Equal(HostNameSource.DnsLookup, cache.GetSource(IPAddress.Parse("140.82.112.3")));
    }

    [Fact]
    public void SharedAddress_PrefersTheNameThatProgramLookedUp()
    {
        // Two sites behind the same CDN address.
        var cdn = IPAddress.Parse("151.101.1.69");
        var cache = Create();
        cache.LearnFromDnsLookup(1, "stackoverflow.com", [cdn]);
        cache.LearnFromDnsLookup(2, "reddit.com", [cdn]);

        Assert.Equal("stackoverflow.com", cache.GetName(cdn, 1));
        Assert.Equal("reddit.com", cache.GetName(cdn, 2));
        Assert.Equal("reddit.com", cache.GetName(cdn, 3)); // unknown program: latest name
    }

    [Fact]
    public void SpecialAddresses_GetFixedLabels()
    {
        var cache = Create();
        Assert.Equal("localhost", cache.GetName(IPAddress.Loopback));
        Assert.Equal("localhost", cache.GetName(IPAddress.IPv6Loopback));
        Assert.Equal("(multicast)", cache.GetName(IPAddress.Parse("224.0.0.251")));
    }

    [Fact]
    public async Task UnknownAddress_FallsBackToReverseDns()
    {
        var ip = IPAddress.Parse("8.8.8.8");
        var cache = Create(_ => Task.FromResult<string?>("dns.google."));

        Assert.Null(cache.GetName(ip)); // first ask starts the lookup
        await WaitUntil(() => cache.GetName(ip) is not null);

        Assert.Equal("dns.google", cache.GetName(ip));
        Assert.Equal(HostNameSource.ReverseDns, cache.GetSource(ip));
    }

    [Fact]
    public async Task ReverseDns_NeverOverridesARealLookup()
    {
        var ip = IPAddress.Parse("140.82.112.3");
        var release = new TaskCompletionSource<string?>();
        var cache = Create(_ => release.Task);

        cache.GetName(ip);
        cache.LearnFromDnsLookup(1, "github.com", [ip]);
        release.SetResult("lb-140-82-112-3-iad.github.com");
        await Task.Delay(50);

        Assert.Equal("github.com", cache.GetName(ip));
    }

    [Fact]
    public void ReverseDns_CanBeTurnedOff()
    {
        var calls = 0;
        var cache = Create(_ => { calls++; return Task.FromResult<string?>("x"); });
        cache.ReverseLookupEnabled = false;

        Assert.Null(cache.GetName(IPAddress.Parse("1.1.1.1")));
        Assert.Equal(0, calls);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }
}

public class AddressFormatterTests
{
    [Theory]
    [InlineData("github.com", "140.82.112.3", AddressDisplay.Names, "github.com")]
    [InlineData("github.com", "140.82.112.3", AddressDisplay.Addresses, "140.82.112.3")]
    [InlineData("github.com", "140.82.112.3", AddressDisplay.Both, "github.com (140.82.112.3)")]
    [InlineData(null, "140.82.112.3", AddressDisplay.Names, "140.82.112.3")]
    [InlineData("140.82.112.3", "140.82.112.3", AddressDisplay.Both, "140.82.112.3")]
    public void Format(string? name, string ip, AddressDisplay mode, string expected) =>
        Assert.Equal(expected, AddressFormatter.Format(name, ip, mode));
}
