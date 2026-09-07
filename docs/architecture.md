# Architecture v0.1

## Decision

Build a local .NET CLI that prepares exported media for user-managed import into Amazon Photos.

The first input format is an already-extracted Google Takeout directory. Archive extraction and automated uploads are outside the initial scope.

## Planned pipeline

Discover files -> match sidecars -> analyze metadata -> produce a dry-run plan -> prepare output copies -> verify and report.

Each stage will be implemented separately.

## Boundaries

- CLI: arguments, user-facing output, and exit codes.
- Core: discovery, matching, metadata decisions, and verification.
- Tests: synthetic inputs and isolated temporary directories.

Add external-tool integrations only when a concrete feature requires them.

## Safety invariants

- Never modify, rename, or delete source files.
- Never overwrite an existing output silently.
- Do not re-encode photos or videos.
- Preserve the original source alongside any metadata-adjusted copy.
- Metadata changes can change whole-file hashes; unchanged hashes alone cannot verify quality for adjusted copies.
- Record metadata provenance and conflicts.
- Do not invent missing dates, timezones, or locations.
- Treat input filenames and sidecar contents as untrusted data.
- Keep personal media and reports out of Git.

## Validation still required

Determine Amazon Photos behavior for capture dates, timezones, GPS, video metadata, and supported formats using a small controlled sample.

Choose metadata precedence and writing tools only after testing representative cases.

The proposed external metadata engine, conservative precedence policy,
separate-output-copy workflow, media-data-hash verification, and staged format
rollout are documented in the
[embedded metadata strategy](embedded-metadata-strategy.md).

## First feature

Read-only recursive inventory of an extracted directory. Return relative paths and byte lengths in deterministic order.

No sidecar parsing, metadata edits, copying, hashing, or uploads.

## Supplemental sidecar parser

Core parses a single explicitly selected supplemental JSON file into optional,
typed metadata without modifying the source. Recognized timestamps are integral
Unix seconds represented as strings or numbers. Coordinates come from `geoData`,
must be finite numbers, and retain valid zero values. Unknown fields are ignored,
while malformed recognized values fail with a path-bearing parse exception.

The parser does not discover or match sidecars, choose metadata precedence, or
classify album metadata. Those remain separate milestones.

## Sidecar matching

Core accepts explicit media and JSON inventory entries and compares their full
logical paths using ordinal, case-sensitive rules. The matcher recognizes the
same-directory exact forms `<media filename>.json` and
`<media filename>.supplemental-metadata.json`. It also recognizes an observed
duplicate-number transformation from `photo(1).jpg` to
`photo.jpg.supplemental-metadata(1).json`. The duplicate suffix must be final,
positive, and free of leading zeroes; multiple digits and earlier parentheses
are preserved. Finally, it recognizes a supplemental sidecar that omits only the
media's final extension, such as `photo.jpg` paired with
`photo.supplemental-metadata.json`. Exact rules remain available for all explicit
media candidates, while extensionless names and dotfiles are excluded from the
duplicate-number and extension-omitted transformations.

Results are ordered by media path and identify matches, unmatched media, and
ambiguous candidates. A sidecar claimed by more than one media entry is never
assigned. Matching reads neither the sidecar nor the media file.

Truncation, edited-copy, fuzzy, and title-based rules remain outside this
milestone, as do `(0)`, duplicate suffixes with leading zeroes, and any heuristic
that combines duplicate-suffix removal with extension omission.

## Inventory classification

Core classifies supplied inventory entries in memory using only the final
filename extension and ordinal, case-insensitive extension comparison. The
initial recognized photo candidates are `.jpg`, `.jpeg`, `.png`, `.gif`, `.webp`,
`.heic`, `.heif`, `.tif`, `.tiff`, and `.dng`. The initial recognized video
candidates are `.mp4`, `.mov`, `.m4v`, `.avi`, `.mpg`, `.mpeg`, `.3gp`, `.3g2`,
`.mkv`, `.webm`, `.mts`, and `.m2ts`. Files ending in `.json` are JSON candidates.

These categories are project-level candidates, not guarantees of Google Photos
or Amazon Photos compatibility. Unsupported and extensionless files remain
reportable as `Other`; classification performs no filesystem or content
inspection.

## Read-only Takeout analysis

Core exposes a read-only analysis entry point that composes the implemented
stages in order: inventory, classification, sidecar matching, and sidecar
parsing. Photo and video candidates are passed to the matcher as media, while
JSON candidates are possible sidecars. Only uniquely matched sidecars are
parsed. A parse failure is retained as a typed invalid-sidecar result and does
not prevent other media from being analyzed; an inventory failure still fails
the complete operation.

The result separately retains successfully parsed media, media with invalid
matched sidecars, unmatched media, ambiguous media and its candidates, JSON
candidates unused by an accepted match, and `Other` files. Each category uses
ordinal relative-path ordering and retains its original inventory entries.
Unused JSON is deliberately not labeled orphan metadata because album and
export-level JSON classification is not implemented.

The analyzer adds no new matching or parsing heuristics. It does not classify
album metadata, resolve metadata precedence, hash content, alter metadata, or
prepare output copies.

The CLI exposes the analysis through a plain-text `analyze` command. Presentation
and exit-code decisions remain in the CLI layer: it prints summary counts and
details only for invalid, unmatched, and ambiguous media. Those attention cases
produce exit code `2`; unused JSON and `Other` files remain reportable counts and
do not affect success. Argument and operational failures produce exit code `1`.
The command creates no output files or saved reports.

## ExifTool discovery

Core can locate and validate an externally installed ExifTool without opening
media or sidecar files. An explicit executable path is authoritative; otherwise,
the detector checks nonempty `PATH` directories in order and then the standard
Intel macOS location. Candidate paths are absolute and deduplicated.

Validation starts the first existing candidate directly with only `-ver`, using
redirected output and no shell. Typed results distinguish success, absence,
execution failure, invalid version output, and timeout. A timed-out process is
terminated. The CLI exposes this check through `check-exiftool`, with an optional
authoritative `--path`; it prints only the detected version and executable path.
Installation and metadata reading or writing remain outside this boundary.

## JPEG and HEIC/HEIF embedded metadata reading

Core can read planning metadata from one explicitly supplied `.jpg`, `.jpeg`,
`.heic`, or `.heif` file by starting an explicitly supplied ExifTool executable
directly. Extension matching is case-insensitive, and every supported format uses
the same selected-tag request. The reader requests grouped JSON for EXIF and XMP
capture dates, the EXIF original-time offset, and EXIF/XMP GPS latitude,
longitude, and altitude. Individual GPS tags use ExifTool's `#` suffix for
machine-readable values, and EXIF latitude, longitude, and altitude references
are requested the same way. The reader does not use global `-n`, so capture-time
formatting is unchanged. It preserves each returned value's semantic field,
ExifTool group and tag, JSON kind, and raw text; it does not select a preferred
date or location.

Typed outcomes distinguish success, unsupported media, missing media, ExifTool
failure, timeout, and malformed JSON. Standard error and JSON warning or error
diagnostics are retained without automatic printing. Process execution and
cleanup are bounded, and the operation never writes media, metadata, backups,
copies, or timestamps. Video and other image formats, metadata precedence, and
writing remain future work.

## Embedded capture-time parsing

Core can parse the capture-time values already returned by the embedded metadata
reader without starting ExifTool or accessing files. It retains every date
candidate and its original semantic field, ExifTool group, tag, and raw text.
Supported values use ExifTool's `yyyy:MM:dd HH:mm:ss` form, optional fractional
seconds, and optional `Z`, `+HH:MM`, `-HH:MM`, `+HHMM`, or `-HHMM` offsets.

An `ExifIFD:OffsetTimeOriginal` value is combined only with an offset-free
`ExifIFD:DateTimeOriginal`. Offset provenance distinguishes an offset embedded
in the date, one supplied by that companion tag, and an unknown offset. Dates
without offsets remain `DateTimeKind.Unspecified`; offset-bearing values also
produce a `DateTimeOffset`. Invalid dates and offsets remain typed issues.
Results use deterministic ordinal ordering and do not choose metadata
precedence, compare sidecars, parse GPS, run tools, or read or write files.

## Embedded GPS parsing

Core can parse the GPS values already returned by the embedded metadata reader
without starting ExifTool or accessing files. Invariant-culture finite numbers
are accepted; latitude is limited to -90 through 90 and longitude to -180
through 180, with boundary and zero values retained.

EXIF values in the `GPS` family-1 group require their corresponding reference
tag. `N`, `S`, `E`, and `W` determine coordinate signs, while altitude reference
`0` means above sea level and `1` means below. Missing, invalid, or conflicting
references do not produce guessed values. Signed XMP values are retained without
using EXIF references. Every candidate keeps its source value and any reference
used as provenance; malformed numbers, non-finite values, range failures, and
reference problems remain typed issues in deterministic ordinal order.

The parser does not select a preferred location, combine coordinates, compare
sidecar values, apply metadata precedence, run tools, or read or write files.

## Embedded location candidates

Core can build complete location candidates from already parsed embedded GPS
values without running tools or accessing files. Latitude and longitude are
combined only within the same ExifTool family-1 group. Altitude is optional and
is attached only from that same group. Each completed location retains the
parsed latitude, longitude, optional altitude, and all source and reference
provenance held by those GPS candidates. Valid `0, 0` coordinates remain valid.

When a group contains multiple values, the builder returns every latitude,
longitude, and altitude combination without selecting or deduplicating one.
Typed issues retain groups and values with latitude but no longitude, longitude
but no latitude, or altitude without a complete coordinate pair. Results use
ordinal group and provenance ordering. This stage does not compare values,
apply tolerances or metadata precedence, compare sidecars, or read or write
files.

## Capture-time decisions

Core can make a read-only capture-time decision from an embedded capture-time
parse result and parsed Takeout sidecar metadata. Valid embedded candidates are
always preserved. Equivalent zoned candidates must represent the same instant;
equivalent unzoned candidates must have the same unspecified wall-clock value.
Mixed timezone knowledge or non-equivalent embedded candidates requires review,
and no tag is preferred by group or name.

Each embedded candidate is compared only with sidecar `PhotoTakenTime`. Zoned
values are compared as instants at whole-second precision, while unzoned values
remain incomparable rather than receiving a machine-local timezone. Match,
conflict, unknown-timezone, and no-sidecar-time outcomes remain explicit. A
sidecar conflict never replaces valid embedded metadata.

When no valid embedded candidate or parsing issue exists, sidecar
`PhotoTakenTime` may be proposed. Sidecar `CreationTime` is used only as a typed
low-confidence fallback. Both are recorded as instants whose original local
timezone is unknown. Embedded parsing issues without a remaining valid value
require review; otherwise the result reports capture time as missing. The layer
does not access files, run ExifTool, decide locations, compare other metadata,
or write output.

## Location decisions

Core can make a read-only location decision from an embedded GPS parse result
and parsed Takeout sidecar metadata. It normalizes and retains the GPS result,
builds same-group embedded locations with `EmbeddedLocationBuilder`, and retains
the build result, sidecar metadata, sidecar assessment, and every source and
reference value. Valid embedded locations are never replaced by sidecar values.

Multiple embedded locations are equivalent only when every latitude and
longitude pair differs by no more than `0.000001` degrees. Altitudes are compared
only when both locations contain them and may differ by no more than `0.1`
meters. Missing altitude does not create a conflict. Non-equivalent candidates
require review; equivalent candidates are all kept without group preference,
rounding, averaging, or deduplication.

A complete sidecar `geoData` pair is compared with every embedded location using
the same tolerances. Results distinguish matches, coordinate conflicts, altitude
conflicts, incomplete sidecar coordinates, unavailable sidecar coordinates, and
the Google `0, 0` placeholder. Conflicts remain reportable but do not replace
embedded metadata. With no complete embedded location, parsing or building
issues require review; otherwise a complete non-placeholder sidecar pair may be
proposed with optional altitude. An incomplete pair requires review, while a
sidecar `0, 0` pair is retained as a placeholder but never proposed for writing.
Embedded `0, 0` remains valid. This layer performs no I/O or metadata writing.

## Per-file metadata plans

Core can build a read-only plan for one embedded metadata read result and one
parsed Takeout sidecar. For a successful read, the plan composes the existing
capture-time parser, GPS parser, embedded location builder, capture-time decision
maker, and location decision maker. It retains the original read result, complete
sidecar metadata, every intermediate parse and build result, both decisions, and
ordered typed review reasons. Sidecar title and description remain source
metadata only; the plan does not propose writing them.

The overall status distinguishes no proposed change, safe proposed changes,
review required, embedded metadata unavailable, and an embedded format whose
reading is not implemented yet. A sidecar `PhotoTakenTime` or complete,
non-placeholder `geoData` proposal is a safe change only when no review reason
exists. Low-confidence `CreationTime`, decision-level review, embedded-versus-
sidecar conflicts, any parsing or location-building issue, and ExifTool warnings,
JSON error diagnostics, or nonempty standard error require review. An unknown
embedded timezone remains reportable but does not by itself require review when
the embedded value is preserved.

Unsupported-media, missing-media, ExifTool-failure, timeout, and malformed-JSON
reader results remain distinct typed plan outcomes with their original details.
The unsupported status describes only the current embedded reader boundary, not
Amazon Photos compatibility. All other read failures mean embedded metadata was
unavailable, and the planner does not attempt a sidecar fallback because the
existing metadata could not be checked safely. This layer performs no filesystem
access, tool execution, copying, hashing, metadata writing, or CLI presentation.

## Takeout metadata planning

Core can create a read-only library planning result from an extracted Takeout
root and an explicitly supplied ExifTool executable path. The service first runs
the complete `TakeoutAnalyzer`; an inventory or analyzer failure is propagated
before any partial planning result can be returned. It then calls
`ExifToolMetadataReader` once for each analyzed photo or video candidate and
passes that typed read outcome, together with the applicable sidecar metadata,
to `MediaMetadataPlanBuilder`. ExifTool discovery remains the caller's
responsibility.

Every media entry appears once in ordinal relative-path order. Typed sidecar
states retain the original matched, unmatched, invalid, or ambiguous analyzer
result. A valid match supplies its parsed sidecar metadata. The other three
states use an empty metadata value, so invalid JSON and competing sidecars cannot
influence a plan; ambiguous candidates are never guessed. Unsupported reader
formats and other typed reader failures remain per-item outcomes and do not stop
planning later media.

The result retains the complete `TakeoutAnalysisResult`, including unused JSON
candidates and `Other` files. Summary counts are derived from the final immutable
item list for sidecar states and metadata-plan statuses. Media paths are rebuilt
from inventory-relative paths, checked to remain within the analyzed root, and
made absolute before being passed to the reader.

This service does not detect ExifTool, add format support, create reports or
output directories, copy or rename files, or write metadata. Source media and
sidecars remain unchanged.

The CLI exposes this service through the `plan` command with a Takeout folder and
an optional authoritative `--exiftool` path. It validates command arguments and
detects ExifTool once. The CLI prints deterministic summary counts and details
only for attention cases: unmatched, invalid, or ambiguous sidecars; metadata
review reasons; embedded-read failures; and formats whose embedded readers are
not implemented. Ambiguous details retain candidate paths and match rules.
Parsed values and captured ExifTool output are never printed.

A completed plan returns exit code `0` when no item needs attention and `2`
otherwise. Invalid arguments, ExifTool detection failures, and operational
failures return `1`. Unused JSON candidates and `Other` files remain summary
counts but do not affect the exit code. The command finishes completed runs with
an explicit read-only confirmation and does not create saved reports.
