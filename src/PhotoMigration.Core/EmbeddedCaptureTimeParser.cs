using System.Globalization;

namespace PhotoMigration.Core;

public static class EmbeddedCaptureTimeParser
{
    private const string ExifOriginalGroup = "ExifIFD";
    private const string OriginalDateTag = "DateTimeOriginal";
    private const string OriginalOffsetTag = "OffsetTimeOriginal";

    private static readonly string[] DateFormats =
    [
        "yyyy:MM:dd HH:mm:ss",
        "yyyy:MM:dd HH:mm:ss.FFFFFFF"
    ];

    public static EmbeddedCaptureTimeParseResult Parse(
        IEnumerable<EmbeddedMetadataValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var orderedValues = values
            .OrderBy(value => value.GroupName, StringComparer.Ordinal)
            .ThenBy(value => value.TagName, StringComparer.Ordinal)
            .ThenBy(value => value.RawValue, StringComparer.Ordinal)
            .ThenBy(value => value.Field)
            .ThenBy(value => value.ValueKind)
            .ToList();

        var issues = new List<EmbeddedCaptureTimeParsingIssue>();
        var validOriginalOffsets = new List<ParsedOffset>();

        foreach (var value in orderedValues.Where(
                     value => value.Field == EmbeddedMetadataField.CaptureTimezoneOffset))
        {
            if (!TryParseOffset(value.RawValue, out var offset))
            {
                issues.Add(new EmbeddedCaptureTimeParsingIssue(
                    EmbeddedCaptureTimeParsingIssueKind.InvalidOffsetValue,
                    value,
                    "The capture-time offset is not a supported UTC offset."));
                continue;
            }

            if (IsOriginalOffset(value))
            {
                validOriginalOffsets.Add(new ParsedOffset(value, offset));
            }
        }

        var candidates = new List<EmbeddedCaptureTimeCandidate>();

        foreach (var value in orderedValues.Where(
                     value => value.Field == EmbeddedMetadataField.CaptureDateTime))
        {
            if (!TryParseDate(value.RawValue, out var dateTime, out var includedOffset))
            {
                issues.Add(new EmbeddedCaptureTimeParsingIssue(
                    EmbeddedCaptureTimeParsingIssueKind.InvalidDateValue,
                    value,
                    "The capture-time value is not a supported ExifTool date."));
                continue;
            }

            if (includedOffset is not null)
            {
                if (!TryCreateDateTimeOffset(
                        dateTime,
                        includedOffset.Value,
                        out var dateTimeOffset))
                {
                    issues.Add(new EmbeddedCaptureTimeParsingIssue(
                        EmbeddedCaptureTimeParsingIssueKind.InvalidDateValue,
                        value,
                        "The capture-time value and offset are outside the supported date range."));
                    continue;
                }

                candidates.Add(new EmbeddedCaptureTimeCandidate(
                    value,
                    dateTime,
                    dateTimeOffset,
                    EmbeddedCaptureTimeOffsetSource.IncludedInDateValue,
                    null));
                continue;
            }

            if (IsOriginalDate(value) && validOriginalOffsets.Count > 0)
            {
                foreach (var parsedOffset in validOriginalOffsets)
                {
                    if (!TryCreateDateTimeOffset(
                            dateTime,
                            parsedOffset.Offset,
                            out var dateTimeOffset))
                    {
                        issues.Add(new EmbeddedCaptureTimeParsingIssue(
                            EmbeddedCaptureTimeParsingIssueKind.InvalidDateValue,
                            value,
                            "The capture-time value and offset are outside the supported date range."));
                        continue;
                    }

                    candidates.Add(new EmbeddedCaptureTimeCandidate(
                        value,
                        dateTime,
                        dateTimeOffset,
                        EmbeddedCaptureTimeOffsetSource.OffsetTimeOriginal,
                        parsedOffset.SourceValue));
                }

                continue;
            }

            candidates.Add(new EmbeddedCaptureTimeCandidate(
                value,
                dateTime,
                null,
                EmbeddedCaptureTimeOffsetSource.Unknown,
                null));
        }

        return new EmbeddedCaptureTimeParseResult(
            candidates.AsReadOnly(),
            issues
                .OrderBy(issue => issue.SourceValue.GroupName, StringComparer.Ordinal)
                .ThenBy(issue => issue.SourceValue.TagName, StringComparer.Ordinal)
                .ThenBy(issue => issue.SourceValue.RawValue, StringComparer.Ordinal)
                .ThenBy(issue => issue.Kind)
                .ToList()
                .AsReadOnly());
    }

    private static bool TryParseDate(
        string rawValue,
        out DateTime dateTime,
        out TimeSpan? includedOffset)
    {
        includedOffset = null;
        var dateText = rawValue;

        if (rawValue.EndsWith('Z'))
        {
            dateText = rawValue[..^1];
            includedOffset = TimeSpan.Zero;
        }
        else
        {
            var positiveOffsetStart = rawValue.LastIndexOf('+');
            var negativeOffsetStart = rawValue.LastIndexOf('-');
            var offsetStart = Math.Max(positiveOffsetStart, negativeOffsetStart);

            if (offsetStart >= "yyyy:MM:dd HH:mm:ss".Length)
            {
                dateText = rawValue[..offsetStart];
                if (!TryParseOffset(rawValue[offsetStart..], out var offset))
                {
                    dateTime = default;
                    return false;
                }

                includedOffset = offset;
            }
        }

        return DateTime.TryParseExact(
            dateText,
            DateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out dateTime);
    }

    private static bool TryParseOffset(string rawValue, out TimeSpan offset)
    {
        offset = default;

        if (rawValue.Length is not (5 or 6)
            || (rawValue[0] != '+' && rawValue[0] != '-'))
        {
            return false;
        }

        var hasColon = rawValue.Length == 6;
        if (hasColon && rawValue[3] != ':')
        {
            return false;
        }

        var hourText = rawValue.AsSpan(1, 2);
        var minuteText = hasColon
            ? rawValue.AsSpan(4, 2)
            : rawValue.AsSpan(3, 2);

        if (!int.TryParse(hourText, NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(
                minuteText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var minutes)
            || hours > 14
            || minutes > 59
            || (hours == 14 && minutes != 0))
        {
            return false;
        }

        offset = new TimeSpan(hours, minutes, seconds: 0);
        if (rawValue[0] == '-')
        {
            offset = -offset;
        }

        return true;
    }

    private static bool TryCreateDateTimeOffset(
        DateTime dateTime,
        TimeSpan offset,
        out DateTimeOffset dateTimeOffset)
    {
        try
        {
            dateTimeOffset = new DateTimeOffset(dateTime, offset);
            return true;
        }
        catch (ArgumentException)
        {
            dateTimeOffset = default;
            return false;
        }
    }

    private static bool IsOriginalDate(EmbeddedMetadataValue value) =>
        StringComparer.Ordinal.Equals(value.GroupName, ExifOriginalGroup)
        && StringComparer.Ordinal.Equals(value.TagName, OriginalDateTag);

    private static bool IsOriginalOffset(EmbeddedMetadataValue value) =>
        StringComparer.Ordinal.Equals(value.GroupName, ExifOriginalGroup)
        && StringComparer.Ordinal.Equals(value.TagName, OriginalOffsetTag);

    private sealed record ParsedOffset(
        EmbeddedMetadataValue SourceValue,
        TimeSpan Offset);
}
