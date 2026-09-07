namespace PhotoMigration.Core;

public static class EmbeddedLocationBuilder
{
    public static EmbeddedLocationBuildResult Build(
        IEnumerable<EmbeddedGpsCandidate> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var orderedValues = values
            .OrderBy(value => value.SourceValue.GroupName, StringComparer.Ordinal)
            .ThenBy(value => value.SourceValue.TagName, StringComparer.Ordinal)
            .ThenBy(value => value.SourceValue.RawValue, StringComparer.Ordinal)
            .ThenBy(value => value.ReferenceValue?.GroupName, StringComparer.Ordinal)
            .ThenBy(value => value.ReferenceValue?.TagName, StringComparer.Ordinal)
            .ThenBy(value => value.ReferenceValue?.RawValue, StringComparer.Ordinal)
            .ToList();

        var candidates = new List<EmbeddedLocationCandidate>();
        var issues = new List<EmbeddedLocationIssue>();

        foreach (var group in orderedValues.GroupBy(
                     value => value.SourceValue.GroupName,
                     StringComparer.Ordinal))
        {
            var latitudes = ValuesFor(group, EmbeddedMetadataField.GpsLatitude);
            var longitudes = ValuesFor(group, EmbeddedMetadataField.GpsLongitude);
            var altitudes = ValuesFor(group, EmbeddedMetadataField.GpsAltitude);
            var hasCoordinatePair = latitudes.Count > 0 && longitudes.Count > 0;

            if (latitudes.Count > 0 && longitudes.Count == 0)
            {
                issues.Add(new EmbeddedLocationIssue(
                    EmbeddedLocationIssueKind.LatitudeWithoutLongitude,
                    group.Key,
                    latitudes,
                    "The metadata group has latitude values but no longitude values."));
            }

            if (longitudes.Count > 0 && latitudes.Count == 0)
            {
                issues.Add(new EmbeddedLocationIssue(
                    EmbeddedLocationIssueKind.LongitudeWithoutLatitude,
                    group.Key,
                    longitudes,
                    "The metadata group has longitude values but no latitude values."));
            }

            if (altitudes.Count > 0 && !hasCoordinatePair)
            {
                issues.Add(new EmbeddedLocationIssue(
                    EmbeddedLocationIssueKind.AltitudeWithoutCoordinatePair,
                    group.Key,
                    altitudes,
                    "The metadata group has altitude values but no complete coordinate pair."));
            }

            if (!hasCoordinatePair)
            {
                continue;
            }

            foreach (var latitude in latitudes)
            {
                foreach (var longitude in longitudes)
                {
                    if (altitudes.Count == 0)
                    {
                        candidates.Add(new EmbeddedLocationCandidate(
                            group.Key,
                            latitude,
                            longitude,
                            null));
                        continue;
                    }

                    foreach (var altitude in altitudes)
                    {
                        candidates.Add(new EmbeddedLocationCandidate(
                            group.Key,
                            latitude,
                            longitude,
                            altitude));
                    }
                }
            }
        }

        return new EmbeddedLocationBuildResult(
            candidates.AsReadOnly(),
            issues
                .OrderBy(issue => issue.GroupName, StringComparer.Ordinal)
                .ThenBy(issue => issue.Kind)
                .ToList()
                .AsReadOnly());
    }

    private static IReadOnlyList<EmbeddedGpsCandidate> ValuesFor(
        IEnumerable<EmbeddedGpsCandidate> values,
        EmbeddedMetadataField field) =>
        values
            .Where(value => value.SourceValue.Field == field)
            .ToList()
            .AsReadOnly();
}
