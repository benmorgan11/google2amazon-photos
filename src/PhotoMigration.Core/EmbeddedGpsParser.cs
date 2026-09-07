using System.Globalization;

namespace PhotoMigration.Core;

public static class EmbeddedGpsParser
{
    private const string ExifGpsGroup = "GPS";
    private const string XmpGroupPrefix = "XMP-";

    public static EmbeddedGpsParseResult Parse(
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

        var candidates = new List<EmbeddedGpsCandidate>();
        var issues = new List<EmbeddedGpsParsingIssue>();

        foreach (var sourceValue in orderedValues.Where(IsGpsDataValue))
        {
            if (!double.TryParse(
                    sourceValue.RawValue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsedValue))
            {
                issues.Add(Issue(
                    EmbeddedGpsParsingIssueKind.MalformedNumber,
                    sourceValue,
                    null,
                    "The GPS value is not an invariant-culture number."));
                continue;
            }

            if (!double.IsFinite(parsedValue))
            {
                issues.Add(Issue(
                    EmbeddedGpsParsingIssueKind.NonFiniteNumber,
                    sourceValue,
                    null,
                    "The GPS value must be finite."));
                continue;
            }

            if (!IsWithinRange(sourceValue.Field, parsedValue))
            {
                issues.Add(Issue(
                    EmbeddedGpsParsingIssueKind.OutOfRange,
                    sourceValue,
                    null,
                    RangeMessage(sourceValue.Field)));
                continue;
            }

            if (IsXmpValue(sourceValue))
            {
                candidates.Add(new EmbeddedGpsCandidate(sourceValue, parsedValue, null));
                continue;
            }

            var referenceField = ReferenceFieldFor(sourceValue.Field);
            var referenceValues = orderedValues
                .Where(value => StringComparer.Ordinal.Equals(value.GroupName, ExifGpsGroup)
                                && value.Field == referenceField)
                .ToList();

            if (referenceValues.Count == 0)
            {
                issues.Add(Issue(
                    EmbeddedGpsParsingIssueKind.MissingReference,
                    sourceValue,
                    null,
                    "The EXIF GPS value has no matching reference tag."));
                continue;
            }

            foreach (var referenceValue in referenceValues)
            {
                if (!TryApplyReference(
                        sourceValue.Field,
                        parsedValue,
                        referenceValue.RawValue,
                        out var referencedValue,
                        out var issueKind))
                {
                    issues.Add(Issue(
                        issueKind,
                        sourceValue,
                        referenceValue,
                        issueKind == EmbeddedGpsParsingIssueKind.InvalidReference
                            ? "The EXIF GPS reference value is not supported."
                            : "The GPS number's sign conflicts with its EXIF reference."));
                    continue;
                }

                candidates.Add(new EmbeddedGpsCandidate(
                    sourceValue,
                    referencedValue,
                    referenceValue));
            }
        }

        return new EmbeddedGpsParseResult(
            candidates
                .OrderBy(candidate => candidate.SourceValue.GroupName, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.SourceValue.TagName, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.SourceValue.RawValue, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ReferenceValue?.GroupName, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ReferenceValue?.TagName, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ReferenceValue?.RawValue, StringComparer.Ordinal)
                .ToList()
                .AsReadOnly(),
            issues
                .OrderBy(issue => issue.SourceValue.GroupName, StringComparer.Ordinal)
                .ThenBy(issue => issue.SourceValue.TagName, StringComparer.Ordinal)
                .ThenBy(issue => issue.SourceValue.RawValue, StringComparer.Ordinal)
                .ThenBy(issue => issue.Kind)
                .ThenBy(issue => issue.ReferenceValue?.GroupName, StringComparer.Ordinal)
                .ThenBy(issue => issue.ReferenceValue?.TagName, StringComparer.Ordinal)
                .ThenBy(issue => issue.ReferenceValue?.RawValue, StringComparer.Ordinal)
                .ToList()
                .AsReadOnly());
    }

    private static bool IsGpsDataValue(EmbeddedMetadataValue value) =>
        value.Field is EmbeddedMetadataField.GpsLatitude
            or EmbeddedMetadataField.GpsLongitude
            or EmbeddedMetadataField.GpsAltitude
        && (StringComparer.Ordinal.Equals(value.GroupName, ExifGpsGroup)
            || IsXmpValue(value));

    private static bool IsXmpValue(EmbeddedMetadataValue value) =>
        value.GroupName.StartsWith(XmpGroupPrefix, StringComparison.Ordinal);

    private static bool IsWithinRange(EmbeddedMetadataField field, double value) =>
        field switch
        {
            EmbeddedMetadataField.GpsLatitude => value is >= -90 and <= 90,
            EmbeddedMetadataField.GpsLongitude => value is >= -180 and <= 180,
            EmbeddedMetadataField.GpsAltitude => true,
            _ => false
        };

    private static string RangeMessage(EmbeddedMetadataField field) =>
        field == EmbeddedMetadataField.GpsLatitude
            ? "GPS latitude must be between -90 and 90 degrees."
            : "GPS longitude must be between -180 and 180 degrees.";

    private static EmbeddedMetadataField ReferenceFieldFor(EmbeddedMetadataField field) =>
        field switch
        {
            EmbeddedMetadataField.GpsLatitude => EmbeddedMetadataField.GpsLatitudeReference,
            EmbeddedMetadataField.GpsLongitude => EmbeddedMetadataField.GpsLongitudeReference,
            EmbeddedMetadataField.GpsAltitude => EmbeddedMetadataField.GpsAltitudeReference,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

    private static bool TryApplyReference(
        EmbeddedMetadataField field,
        double value,
        string rawReference,
        out double referencedValue,
        out EmbeddedGpsParsingIssueKind issueKind)
    {
        referencedValue = default;
        issueKind = EmbeddedGpsParsingIssueKind.InvalidReference;

        string positiveReference;
        string negativeReference;

        switch (field)
        {
            case EmbeddedMetadataField.GpsLatitude:
                positiveReference = "N";
                negativeReference = "S";
                break;
            case EmbeddedMetadataField.GpsLongitude:
                positiveReference = "E";
                negativeReference = "W";
                break;
            case EmbeddedMetadataField.GpsAltitude:
                positiveReference = "0";
                negativeReference = "1";
                break;
            default:
                return false;
        }

        var isPositiveReference = StringComparer.Ordinal.Equals(
            rawReference,
            positiveReference);
        var isNegativeReference = StringComparer.Ordinal.Equals(
            rawReference,
            negativeReference);

        if (!isPositiveReference && !isNegativeReference)
        {
            return false;
        }

        if (value < 0 && isPositiveReference)
        {
            issueKind = EmbeddedGpsParsingIssueKind.ConflictingSignAndReference;
            return false;
        }

        referencedValue = value == 0
            ? 0
            : isNegativeReference
                ? -Math.Abs(value)
                : Math.Abs(value);
        return true;
    }

    private static EmbeddedGpsParsingIssue Issue(
        EmbeddedGpsParsingIssueKind kind,
        EmbeddedMetadataValue sourceValue,
        EmbeddedMetadataValue? referenceValue,
        string message) =>
        new(kind, sourceValue, referenceValue, message);
}
