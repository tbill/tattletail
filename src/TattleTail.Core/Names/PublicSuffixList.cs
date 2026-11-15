using System.Globalization;
using System.Net;
using System.Reflection;

namespace TattleTail.Core;

/// <summary>
/// Works out a host's "registrable domain", the part someone actually owns:
/// <c>api.github.com</c> → <c>github.com</c>, <c>news.bbc.co.uk</c> → <c>bbc.co.uk</c>.
/// </summary>
/// <remarks>
/// Uses the Mozilla Public Suffix List (https://publicsuffix.org, MPL-2.0), embedded as
/// <c>Names/Data/public_suffix_list.dat</c>. By default only the ICANN section is used, so
/// hosting providers group together (every <c>*.cloudfront.net</c> host is under
/// <c>cloudfront.net</c>) instead of each customer subdomain becoming its own domain.
/// </remarks>
public sealed class PublicSuffixList
{
    private const string ResourceName = "TattleTail.Core.public_suffix_list.dat";
    private static readonly Lazy<PublicSuffixList> LazyDefault = new(() => LoadEmbedded(includePrivateDomains: false));
    private static readonly IdnMapping Idn = new();

    private readonly HashSet<string> _rules = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wildcardRules = new(StringComparer.Ordinal); // "*.ck" stored as "ck"
    private readonly HashSet<string> _exceptionRules = new(StringComparer.Ordinal); // "!www.ck" stored as "www.ck"

    /// <summary>The embedded list, ICANN section only.</summary>
    public static PublicSuffixList Default => LazyDefault.Value;

    public PublicSuffixList(TextReader list, bool includePrivateDomains)
    {
        string? line;
        while ((line = list.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.StartsWith("// ===BEGIN PRIVATE DOMAINS===", StringComparison.Ordinal) && !includePrivateDomains)
            {
                break;
            }

            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            // Rules end at the first whitespace.
            var rule = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
            if (rule.StartsWith('!'))
            {
                _exceptionRules.Add(ToAscii(rule[1..]));
            }
            else if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                _wildcardRules.Add(ToAscii(rule[2..]));
            }
            else
            {
                _rules.Add(ToAscii(rule));
            }
        }
    }

    public static PublicSuffixList LoadEmbedded(bool includePrivateDomains)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException("The public suffix list resource is missing.");
        using var reader = new StreamReader(stream);
        return new PublicSuffixList(reader, includePrivateDomains);
    }

    /// <summary>
    /// The registrable domain of <paramref name="host"/>. Returns the input unchanged for IP
    /// addresses, single-label names, labels like "(multicast)", and hosts that are themselves
    /// a public suffix.
    /// </summary>
    public string GetDomain(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return host;
        }

        var cleaned = host.Trim().TrimEnd('.');
        if (!cleaned.Contains('.') || cleaned.StartsWith('(') || IPAddress.TryParse(cleaned, out _))
        {
            return cleaned;
        }

        string ascii;
        try
        {
            ascii = ToAscii(cleaned);
        }
        catch (ArgumentException)
        {
            return cleaned.ToLowerInvariant();
        }

        var labels = ascii.Split('.');
        if (labels.Any(l => l.Length == 0))
        {
            return ascii;
        }

        var suffixStart = FindPublicSuffixStart(labels);
        return suffixStart > 0 ? string.Join('.', labels[(suffixStart - 1)..]) : ascii;
    }

    /// <summary>Index of the first label of the public suffix within <paramref name="labels"/>.</summary>
    private int FindPublicSuffixStart(string[] labels)
    {
        // Exception rules win outright: "!www.ck" means www.ck is registrable, so the suffix is "ck".
        for (var i = 0; i < labels.Length; i++)
        {
            if (_exceptionRules.Contains(Join(labels, i)))
            {
                return i + 1;
            }
        }

        // Otherwise the longest matching rule wins; i = 0 is the longest candidate.
        for (var i = 0; i < labels.Length; i++)
        {
            if (_rules.Contains(Join(labels, i)))
            {
                return i;
            }

            if (i + 1 < labels.Length && _wildcardRules.Contains(Join(labels, i + 1)))
            {
                return i;
            }
        }

        // The implicit "*" rule: any unlisted top-level label is a public suffix.
        return labels.Length - 1;
    }

    private static string Join(string[] labels, int start) => string.Join('.', labels, start, labels.Length - start);

    private static string ToAscii(string name) => Idn.GetAscii(name).ToLowerInvariant();
}
