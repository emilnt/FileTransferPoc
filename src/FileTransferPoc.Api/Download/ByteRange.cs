using System.Globalization;

namespace FileTransferPoc.Api.Download;

public enum RangeOutcome
{
    /// <summary>Send the whole file with 200 OK.</summary>
    Full,

    /// <summary>Send <see cref="RangeDecision.Start"/>..+<see cref="RangeDecision.Length"/> with 206 Partial Content.</summary>
    Partial,

    /// <summary>Answer 416 Range Not Satisfiable.</summary>
    NotSatisfiable,
}

public readonly record struct RangeDecision(RangeOutcome Outcome, long Start, long Length)
{
    public long End => Start + Length - 1;

    public static RangeDecision Full(long fileLength) => new(RangeOutcome.Full, 0, fileLength);

    public static RangeDecision Partial(long start, long length) => new(RangeOutcome.Partial, start, length);

    public static RangeDecision NotSatisfiable { get; } = new(RangeOutcome.NotSatisfiable, 0, 0);
}

/// <summary>
/// Single-part Range handling per RFC 9110 §14, with the ESAS rule that a range reaching past
/// the end of the file from its first byte is answered with 200 OK and the whole file.
/// </summary>
public static class ByteRange
{
    public static RangeDecision Resolve(string? rangeHeader, long fileLength)
    {
        // RFC 9110 lets a server ignore a Range it does not understand or support (including
        // multi-part ranges, which ESAS does not require), and answer with the full representation.
        if (string.IsNullOrWhiteSpace(rangeHeader) || fileLength == 0 || !TryParse(rangeHeader, out var first, out var last))
        {
            return RangeDecision.Full(fileLength);
        }

        if (first is null)
        {
            // Suffix range: the last N bytes.
            var suffix = last!.Value;
            if (suffix == 0)
            {
                return RangeDecision.NotSatisfiable;
            }

            return suffix > fileLength
                ? RangeDecision.Full(fileLength)
                : RangeDecision.Partial(fileLength - suffix, suffix);
        }

        var start = first.Value;
        if (start >= fileLength)
        {
            return RangeDecision.NotSatisfiable;
        }

        if (last is null)
        {
            return RangeDecision.Partial(start, fileLength - start);
        }

        if (last.Value >= fileLength)
        {
            // Range larger than the file: whole file from the first byte gets 200 (ESAS),
            // otherwise clamp to the end as RFC 9110 prescribes so resumed downloads still work.
            return start == 0
                ? RangeDecision.Full(fileLength)
                : RangeDecision.Partial(start, fileLength - start);
        }

        return RangeDecision.Partial(start, last.Value - start + 1);
    }

    /// <summary>Parses <c>bytes=first-last</c>, <c>bytes=first-</c> or <c>bytes=-suffix</c>. Anything else fails.</summary>
    private static bool TryParse(string header, out long? first, out long? last)
    {
        first = null;
        last = null;

        var value = header.AsSpan().Trim();
        const string unit = "bytes=";
        if (!value.StartsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = value[unit.Length..].Trim();
        if (spec.Contains(','))
        {
            return false;
        }

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var firstPart = spec[..dash].Trim();
        var lastPart = spec[(dash + 1)..].Trim();
        if (firstPart.IsEmpty && lastPart.IsEmpty)
        {
            return false;
        }

        if (!firstPart.IsEmpty)
        {
            if (!TryParseNumber(firstPart, out var f))
            {
                return false;
            }

            first = f;
        }

        if (!lastPart.IsEmpty)
        {
            if (!TryParseNumber(lastPart, out var l))
            {
                return false;
            }

            last = l;
        }

        return first is null || last is null || last >= first;
    }

    private static bool TryParseNumber(ReadOnlySpan<char> value, out long number) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
}
