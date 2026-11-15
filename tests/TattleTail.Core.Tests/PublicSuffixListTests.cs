using TattleTail.Core;
using Xunit;

namespace TattleTail.Core.Tests;

public class PublicSuffixListTests
{
    [Theory]
    [InlineData("api.github.com", "github.com")]
    [InlineData("lb-140-82-113-23-iad.github.com", "github.com")]
    [InlineData("github.com", "github.com")]
    [InlineData("WWW.Example.COM.", "example.com")]
    [InlineData("news.bbc.co.uk", "bbc.co.uk")]
    [InlineData("a.b.c.example.com.au", "example.com.au")]
    [InlineData("rr3---sn-abc.googlevideo.com", "googlevideo.com")]
    [InlineData("d1234abcd.cloudfront.net", "cloudfront.net")] // private section ignored by default
    [InlineData("user.github.io", "github.io")]
    [InlineData("runner.abc.cx.internal.cloudapp.net", "cloudapp.net")]
    [InlineData("foo.bar.unlistedtld", "bar.unlistedtld")] // implicit "*" rule
    public void GroupsHostsByRegistrableDomain(string host, string expected) =>
        Assert.Equal(expected, PublicSuffixList.Default.GetDomain(host));

    [Theory]
    [InlineData("www.ck", "www.ck")]       // exception rule !www.ck
    [InlineData("a.b.ck", "a.b.ck")]       // wildcard rule *.ck
    [InlineData("a.b.kawasaki.jp", "a.b.kawasaki.jp")] // wildcard *.kawasaki.jp
    [InlineData("x.city.kawasaki.jp", "city.kawasaki.jp")] // exception !city.kawasaki.jp
    public void HandlesWildcardAndExceptionRules(string host, string expected) =>
        Assert.Equal(expected, PublicSuffixList.Default.GetDomain(host));

    [Theory]
    [InlineData("140.82.112.3")]
    [InlineData("2606:50c0:8000::154")]
    [InlineData("localhost")]
    [InlineData("(multicast)")]
    [InlineData("co.uk")]
    public void LeavesNonDomainsAlone(string host) =>
        Assert.Equal(host, PublicSuffixList.Default.GetDomain(host));

    [Fact]
    public void InternationalNamesUsePunycode() =>
        Assert.Equal("xn--bcher-kva.example", PublicSuffixList.Default.GetDomain("www.bücher.example"));

    [Fact]
    public void PrivateSectionCanBeIncluded()
    {
        var withPrivate = PublicSuffixList.LoadEmbedded(includePrivateDomains: true);
        Assert.Equal("user.github.io", withPrivate.GetDomain("user.github.io"));
        Assert.Equal("d1234abcd.cloudfront.net", withPrivate.GetDomain("d1234abcd.cloudfront.net"));
    }
}
