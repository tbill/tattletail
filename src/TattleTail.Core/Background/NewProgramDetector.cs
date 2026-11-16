namespace TattleTail.Core;

/// <summary>What <see cref="NewProgramDetector.Observe"/> found.</summary>
/// <param name="Added">Programs seen for the first time, to be remembered.</param>
/// <param name="Announce">Whether the user should be told about them.</param>
public readonly record struct NewPrograms(IReadOnlyList<string> Added, bool Announce)
{
    public static readonly NewPrograms None = new([], false);
}

/// <summary>
/// Spots programs using the network for the first time.
/// </summary>
/// <remarks>
/// Like a doorman with a guest book: anyone already in the book walks past, a new face gets
/// written down and announced. On the very first day the book is empty and everyone is new, so
/// for a short "learning" period the doorman just writes names down without announcing them.
/// </remarks>
public sealed class NewProgramDetector
{
    public static readonly TimeSpan DefaultLearningPeriod = TimeSpan.FromMinutes(5);

    private readonly HashSet<string> _known;
    private readonly TimeSpan _learningPeriod;
    private readonly Func<DateTime> _clock;
    private DateTime _learningUntilUtc = DateTime.MinValue;

    /// <param name="known">Programs remembered from earlier (their history identity, e.g. exe path).</param>
    public NewProgramDetector(IEnumerable<string> known, TimeSpan? learningPeriod = null, Func<DateTime>? clock = null)
    {
        _known = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        _learningPeriod = learningPeriod ?? DefaultLearningPeriod;
        _clock = clock ?? (() => DateTime.UtcNow);
        if (_known.Count == 0)
        {
            StartLearning();
        }
    }

    /// <summary>True while new programs are being noted quietly.</summary>
    public bool IsLearning => _clock() < _learningUntilUtc;

    /// <summary>Call with the programs that just used the network.</summary>
    public NewPrograms Observe(IEnumerable<string> programs)
    {
        List<string>? added = null;
        foreach (var program in programs)
        {
            if (!string.IsNullOrEmpty(program) && _known.Add(program))
            {
                (added ??= []).Add(program);
            }
        }

        return added is null ? NewPrograms.None : new NewPrograms(added, !IsLearning);
    }

    /// <summary>Empties the guest book and starts a fresh learning period.</summary>
    public void Forget()
    {
        _known.Clear();
        StartLearning();
    }

    private void StartLearning() => _learningUntilUtc = _clock() + _learningPeriod;
}
