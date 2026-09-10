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

## Photo and video embedded metadata reading

Core can read planning metadata from one explicitly supplied `.jpg`, `.jpeg`,
`.heic`, or `.heif` file by starting an explicitly supplied ExifTool executable
directly. Extension matching is case-insensitive. These image formats retain the
existing selected-tag request for grouped EXIF and XMP capture dates, the EXIF
original-time offset, and EXIF/XMP GPS latitude, longitude, and altitude.
Individual GPS tags use ExifTool's `#` suffix for machine-readable values, and
EXIF latitude, longitude, and altitude references are requested the same way.

`.mov` and `.mp4` use a separate QuickTime request. The selected date tags are
`QuickTime:CreateDate`, `Keys:CreationDate`, `UserData:DateTimeOriginal`, and the
`ContentCreateDate` values in Keys, ItemList, and UserData. Filesystem dates,
modify dates, track dates, and media dates are deliberately excluded. The reader
does not enable ExifTool's `QuickTimeUTC` option: ExifTool documents that integer
QuickTime dates are intended to be UTC but are often written as local time by
cameras, so it leaves their timezone unspecified by default.
[ExifTool QuickTime tags](https://exiftool.org/TagNames/QuickTime.html)

QuickTime location selection reads raw `GPSCoordinates` from Keys, ItemList, and
UserData. The GPS parser accepts a conservative signed ISO 6709-style latitude
and longitude with optional signed altitude. It retains the original combined
value, group, and tag as provenance for every parsed component. Components from
one combined value remain atomic when complete locations are built; different
combined values are not cross-paired. Malformed, non-finite, and out-of-range
values remain typed GPS parsing issues, so a present invalid location prevents
automatic sidecar fallback. Valid embedded `0, 0` coordinates remain valid.

The reader does not use global `-n`, so capture-time formatting is unchanged. It
preserves each returned value's semantic field, ExifTool group and tag, JSON
kind, and raw text; it does not select a preferred date or location. Capture
dates without explicit offsets remain timezone-unknown, while offset-bearing
dates retain their instant and offset. Multiple values remain separate for the
decision layer to compare.

Typed outcomes distinguish success, unsupported media, missing media, ExifTool
failure, timeout, and malformed JSON. Standard error and JSON warning or error
diagnostics are retained without automatic printing. Process execution and
cleanup are bounded, and the operation never writes media, metadata, backups,
copies, or timestamps. Other video and image formats, timed GPS extraction,
metadata writing, and media pairing remain future work.

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

QuickTime `GPSCoordinates` values are parsed as atomic signed coordinate sets.
Their latitude, longitude, and optional altitude components keep the same
original combined value as provenance and are not mixed with components from a
different combined value. Invalid combined values remain parsing issues rather
than disappearing as missing metadata.

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

## Pure Amazon capture-time write planning

Controlled Amazon Photos tests found that JPEG, HEIC, and PNG use EXIF
`DateTimeOriginal`, MOV uses QuickTime `CreateDate`, and MP4 prefers Keys
`CreationDate`. Amazon displayed these timestamp fields literally and ignored
their timezone offsets. For a uniquely matched Takeout sidecar, Google
`photoTakenTime` is therefore treated as the authoritative instant, converted to
UTC at whole-second precision, and formatted for the applicable Amazon-facing
tag. Image plans also set EXIF `OffsetTimeOriginal` to `+00:00`; MP4 Keys values
include `+00:00` in the date string.

`AmazonCaptureTimeWritePlanBuilder` is a pure format-aware policy layer over one
`TakeoutMetadataPlanningItem`. A ready result retains the original item, UTC
instant, media format, ordered typed assignments, matched sidecar entry, and
match rule. This policy intentionally uses a matched `photoTakenTime` even when
existing embedded values differ, so matched files share one chronological
basis. Without that sidecar value, a trustworthy embedded capture-time decision
is retained without a write; sidecar `creationTime` never authorizes one.

Invalid or ambiguous sidecars and items without a usable capture time require
attention. JPEG/JPG, HEIC/HEIF, PNG, MOV, and MP4 are supported using
case-insensitive extensions. Other extensions receive an unsupported planning
result that does not claim Amazon incompatibility. The builder does not access
the filesystem, run ExifTool, change the existing reader decisions, write
metadata, or integrate with preparation or the CLI.

## Destination path planning

Core can create a pure destination-path plan from absolute Takeout and output
roots plus explicit media inventory entries. A successful result contains one
item per entry in ordinal relative-path order. Each item retains the original
`InventoryEntry` and provides absolute source and destination paths while
preserving the entry's relative directories and filename.

The planner rejects equal or overlapping roots, including an output nested in
the source or a source nested in the output. Logical paths must be nonempty and
relative, with no empty, `.` or `..` segments, and their normalized source and
destination paths must remain strictly below the corresponding root. Exact
duplicate logical paths produce typed issues rather than duplicate items.

Destination collision checks are deliberately conservative for the target Intel
Mac: absolute destination names are compared case-insensitively after standard
.NET Form C Unicode normalization. This detects case-only and canonically
equivalent Unicode names. A path that would need another planned file to be a
directory is also a typed file-versus-directory conflict. The planner never
renames entries or invents suffixes. Any issue produces a failure result instead
of a partial usable plan.

This milestone performs lexical validation only. It does not inspect source or
output files, create directories, copy or hash media, run ExifTool, change
timestamps, or write metadata. A future output writer must revalidate the real
filesystem immediately before use, keep source and output roots separate, reject
symbolic links and reparse points, and handle races and existing output paths
without overwriting them.

## Verified temporary media staging

Core can stage one `DestinationPathPlanningItem` as a verified temporary copy.
The service re-runs `DestinationPathPlanner` for the original inventory entry and
requires the supplied absolute source and destination paths to match that result.
Both roots must already exist as separate, non-overlapping directories. The
roots and every existing component below them are checked for symbolic links or
reparse points, the source must be a regular file, and its open-stream length
must match the inventoried byte count.

Destination directories are created one component at a time beneath the output
root and checked after creation. An existing final destination of any kind is a
failure. The service creates a generated temporary filename in the intended
destination directory with `FileMode.CreateNew`, retrying name collisions
without overwriting or reusing them. The temporary name retains the media's
extension for later format-specific work, but the verified file is not moved to
its final destination in this milestone.

The source is read through an explicit stream while its SHA-256 hash and copied
byte count are calculated. After the temporary stream is closed and flushed, the
temporary file is independently reopened and read to calculate its own byte
count and SHA-256 hash. Success retains both hashes, the count, original planning
item, temporary path, and intended final path. A mismatch is a typed verification
failure. Other typed failures distinguish invalid plans or roots, linked paths,
missing, changed, or non-regular sources, existing destinations, directory and
temporary-file creation problems, copy errors, and cleanup errors.

After creating a temporary file, every failure attempts to delete only that
file. A cleanup failure retains its path and the preceding failure category;
successful staging deliberately leaves the verified temporary file for a later
metadata stage. Source files and unrelated output files are never deleted or
modified, and sidecars are not copied.

Portable .NET path APIs cannot atomically hold every directory component open
with no-follow semantics. Another process could replace a checked component or
create the final destination between validation steps. On some filesystems,
another process may also alter a source while it is being read despite the file
sharing mode. The byte count and hashes prove that the temporary file matches the
bytes this operation read; the inventory contains no earlier hash that could
prove those bytes predate a concurrent same-size source change. The service
checks components, source length, and the final destination again before
success, but this reduces rather than eliminates these races. A future publisher
must repeat the checks, use create-new or non-overwriting move semantics, and
fail closed if any path has changed.

## Read-only media-data hashing

Core can ask an explicitly supplied ExifTool executable for the
`ImageDataHash` of one successfully staged temporary media copy. The reader
accepts a `VerifiedMediaFileStagingSuccessResult` and first checks that its byte
count and full-file hashes are internally consistent, that its paths are
absolute, and that the temporary file remains beside its intended final
destination with the same extension. The original Takeout source is not opened.

The reader supports JPEG/JPG, HEIC/HEIF, PNG, MOV, and MP4, using case-insensitive
extension matching. It checks the temporary copy before ExifTool runs to ensure
that the path exists and identifies a regular file rather than a symbolic link
or reparse point. ExifTool is started directly with these exact arguments:

```text
-json
-G1
-s
-api
ImageHashType=SHA256
-ImageDataHash
<absolute temporary-copy path>
```

The reader does not request all metadata or use any write option. ExifTool
documents `ImageDataHash` as a hash of the media-data portion and
`ImageHashType` as the API setting that selects the algorithm.
[ExifTool extra tags](https://exiftool.org/TagNames/Extra.html)

A valid response contains exactly one JSON object and one
`File:ImageDataHash` value. The value must be exactly 64 ASCII hexadecimal
characters and is normalized to uppercase; the original value remains
available for diagnostics. Warnings and standard error are captured separately
and are not interpreted as hashes. Typed results distinguish invalid staging
data or paths, missing, linked, or non-regular temporary files, unsupported
formats, ExifTool execution failures, timeouts, malformed JSON, missing hashes,
and invalid hashes.

After ExifTool exits successfully, the reader independently opens the temporary
copy and computes its current whole-file SHA-256 and byte count. It checks the
path again before and after this read and accepts the ExifTool value only when
the count and whole-file hash still match the staging result. Missing, linked,
or non-regular files keep their specific typed failures; a byte-count or hash
mismatch returns a distinct verification failure with the expected and actual
values. Verification never deletes or repairs the temporary copy.

Hashing a large video can take materially longer than reading selected metadata,
so the configurable default timeout is ten minutes. Tests use short explicit
timeouts. Timeout cleanup kills the process tree, waits at most one second for
exit, and bounds redirected-output cleanup; it never performs an indefinite
process wait.

This operation is read-only. The post-process whole-file SHA-256 is compared
only with the stager's whole-file SHA-256. It is never compared with ExifTool's
media-only `ImageDataHash`, because those hashes cover different byte sets.
Portable path checks cannot eliminate a race in which another process replaces
the temporary file between validation and process access. The reader validates
again after ExifTool exits, but a later publishing stage must repeat all
filesystem safety checks before trusting or moving the file.

## Pure JPEG metadata write planning

Core can build a write plan from one complete `TakeoutMetadataPlanningItem` and
the successful baseline `ImageDataHash` result for its verified temporary copy.
The Takeout item must contain a `SuccessfulMediaMetadataPlan`. The builder is
pure: it does not inspect the filesystem, run ExifTool, or modify the temporary
copy. It joins already-validated results by checking that the Takeout media
entry and embedded-metadata source match the staging result, that the baseline
and staging temporary paths agree, and that the retained final destinations
agree. The builder trusts the successful-result invariants established by the
destination planner, stager, media-data-hash reader, and metadata-plan builder;
it does not repeat their path normalization, hash validation, or decision
derivation. Every outcome retains the complete Takeout planning item.

Only JPEG and JPG paths are supported, using case-insensitive extension
matching. Typed outcomes distinguish a plan ready for a later writer, no changes
required, review required, invalid input or provenance, and a format whose write
planning is not supported. Every outcome retains the complete metadata plan,
staging result, and baseline hash result.

The only assignments currently produced are JPEG EXIF GPS latitude, latitude
reference, longitude, longitude reference, and optional altitude with its
above- or below-sea-level reference. They are produced only from a
`ProposeSidecarLocationDecision` backed by a `MatchedTakeoutSidecarState`. The
matched analysis result must retain the same media entry and the exact parsed
metadata object used by the metadata plan, plus one sidecar inventory entry and
its match rule. Each assignment retains that sidecar entry and rule alongside
the full `geoData` metadata. Unmatched, invalid, ambiguous, or forged sidecar
states cannot authorize an assignment. Values use invariant round-trip numeric
formatting and are emitted in a fixed typed-tag order. The fixed assignment
builder produces each supported tag at most once. Existing embedded locations
are never assigned or replaced.

Capture-time assignments are deliberately excluded. Existing embedded capture
times remain unchanged. A proposed sidecar `PhotoTakenTime` requires review
because the Unix instant does not establish the original local timezone, and a
low-confidence `CreationTime` fallback remains blocked. Conflicts, mixed
timezone knowledge, parsing issues, location issues, and every retained
`MediaMetadataPlanReviewReason` also prevent an executable plan. The write-plan
builder consumes those upstream reasons instead of reconstructing them. A plan
may retain otherwise safe GPS assignments for review, but no assignment is
approved for execution until all review reasons are resolved.

This milestone builds typed group, tag, string-value, and source records rather
than ExifTool arguments or shell text. It does not write title, description,
orientation, timestamps, GPS data, or any other metadata. A future writer must
repeat filesystem and baseline-hash validation immediately before performing
any write.

## JPEG GPS metadata writing

Core can execute one approved `JpegMetadataWriteReadyResult` against its unique
verified temporary output copy. Before starting ExifTool, the writer requires
the output root to remain an existing non-linked directory, keeps the temporary
and final paths strictly below that root, rejects linked components and
non-regular or missing temporary files, and fails if the final destination
exists. It independently recalculates the temporary copy's byte count and
whole-file SHA-256 and requires both to match the staging result. The original
Takeout path is never opened.

The writer accepts only a complete EXIF latitude/longitude assignment set and
requires altitude and altitude reference to occur together. It starts ExifTool
directly, without a shell, with `-overwrite_original`, the approved assignments
in deterministic tag order, and the absolute temporary path as the final
argument. The overwrite option applies only to the unique staging copy. No
filename, directory, all-metadata, capture-time, or final-publication option is
used.

Failures and timeouts retain the temporary file without cleanup or rollback.
Process termination and redirected-output capture are bounded. A zero exit code
returns a typed `PendingVerification` result: it does not establish that GPS was
written correctly or that media payload bytes were preserved. A later milestone
must read the metadata back and compare the post-write `ImageDataHash` with the
saved baseline before publication can be considered.

As in earlier filesystem stages, portable path checks reduce but cannot remove
replacement races between validation and ExifTool opening the file. Final
publication must repeat the relevant checks and use non-overwriting semantics.

## Amazon capture-time metadata writing

Core can execute one ready Amazon capture-time plan against its matching verified
temporary copy and baseline `ImageDataHash` result. The writer supports the
planned JPEG/JPG, HEIC/HEIF, PNG, MOV, and MP4 assignment shapes. It passes typed
assignments to ExifTool in their plan order, starts the absolute executable
directly without a shell, and uses `-overwrite_original` so ExifTool does not
leave backup files.

This writer and the JPEG GPS writer share one internal staging-write helper for
path safety, whole-file hash verification, and collision checks. A separate
small internal process runner provides direct ExifTool execution, bounded
timeouts, process termination, and output capture. Each public operation still
owns its typed plan or result validation, ordered arguments, and public outcomes.

Before writing, the service joins all three retained results to the same media,
requires the temporary copy to remain a regular non-linked file below the output
root, recalculates its byte count and whole-file SHA-256, and fails if the final
destination exists. The configurable default timeout is ten minutes for large
videos; process-tree termination and output capture are bounded.

A zero ExifTool exit returns `PendingVerification`. It neither proves that the
requested fields were stored nor that `ImageDataHash` stayed unchanged. This
stage never opens the Takeout source, publishes or renames the temporary copy,
or integrates with the CLI. Portable path checks reduce but do not eliminate
replacement races during process access.

## Amazon capture-time write verification

Core can verify a successful Amazon capture-time write for JPEG/JPG, HEIC/HEIF,
PNG, MOV, and MP4. One direct, read-only ExifTool call requests only the capture
field or fields selected by the format-aware plan plus SHA-256 `ImageDataHash`.
Returned capture-time strings must exactly equal every ordered approved
assignment, and the media-data hash must equal the retained pre-write baseline.

The verifier validates the retained write, staging, and baseline joins and checks
that the temporary file is regular and non-linked and the final destination is
absent both before and after ExifTool runs. Typed results distinguish invalid
input, filesystem changes, tool failures, bounded timeouts, malformed or missing
values, timestamp mismatches, and missing, invalid, or changed media hashes.
Success retains expected and actual assignments, both hashes, paths, and tool
diagnostics. Verification never opens the Takeout source, modifies or publishes
the staged copy, or integrates with the CLI.

## JPEG GPS write verification

Core can verify one successful JPEG GPS write before the temporary copy is
eligible for future publication. The verifier checks that the temporary path is
still a regular, non-linked file and that the final destination remains absent,
then makes one direct, read-only ExifTool call for the grouped EXIF GPS tags and
SHA-256 `ImageDataHash`. The original Takeout source is never opened.

The returned EXIF values are parsed through the existing GPS parser and location
builder. Latitude and longitude use the existing `0.000001` degree tolerance,
altitude uses the existing `0.1` meter tolerance, and direction and altitude
references must match exactly. An omitted altitude assignment requires altitude
to remain absent. The post-write media-only `ImageDataHash` must exactly match
the saved baseline after hexadecimal normalization; the whole-file staging hash
is deliberately not compared because a metadata write changes those bytes.

Typed results retain the write result, paths, expected and actual GPS values,
baseline and actual media hash, and ExifTool diagnostics. Failures distinguish
filesystem changes, malformed or missing values, GPS or media-hash mismatches,
tool errors, and bounded timeouts. Verification does not publish, rename, move,
delete, repair, or otherwise modify any file.

## Verified JPEG publication

Core can publish one successfully verified JPEG GPS result by moving its staged
temporary file to the retained final destination. Before moving, the publisher
requires an existing absolute output root that is a non-linked directory. The
temporary and final paths must match the retained staging, writing, and
verification results, remain strictly below the output root, and share one
directory. Every existing component below the root is checked for symbolic
links or reparse points, the temporary path must remain a regular non-linked
file, and the final destination must remain absent.

Publication uses `File.Move` with overwrite disabled. Because staging placed the
temporary file beside its final destination, this is a same-directory move and
does not copy or re-encode the verified bytes. Success retains the complete
verification result, former temporary path, final path, and byte count recorded
immediately before publication. Validation and move failures retain the same
paths and do not delete, repair, copy, or deliberately move the temporary file.
The original Takeout source is never accessed.

Portable filesystem APIs cannot make the validation checks and move one atomic
operation. A competing process may still create the destination or replace a
checked path; the non-overwriting move then fails rather than replacing an
existing file. Multi-file orchestration and recovery from external concurrent
changes remain outside this milestone.

## Verified unchanged-media publication

Core can publish any successfully staged media file when no metadata change is
required. The publisher is format-agnostic: it does not inspect metadata or run
ExifTool. It requires an existing absolute, non-linked output directory,
revalidates that the retained temporary and final paths remain together below
that root, rejects linked components and non-regular temporary files, and
requires the final destination to remain absent.

Immediately before publication, the temporary file is read independently to
recalculate its byte count and whole-file SHA-256. Both must match the staging
result, proving that the unchanged output still contains exactly the bytes that
were staged. Publication then uses a same-directory `File.Move` with overwrite
disabled. Success retains the staging result, former temporary path, final path,
verified byte count, and SHA-256.

Validation and move failures do not delete, repair, rename automatically, or
otherwise alter the temporary file. The Takeout source is never opened. As with
verified JPEG publication, portable filesystem races remain possible between
the final checks and move; a competing destination causes the non-overwriting
move to fail rather than replace that file.

## Multi-file Takeout preparation

Core can prepare one analyzed Takeout library sequentially. The preparation
service runs `TakeoutMetadataPlanner` once, creates one destination plan for all
media candidates, and processes its per-file items in ordinal relative-path
order. Every media candidate receives one immutable outcome. The complete result
retains the original metadata planning and analysis results, the destination
planning result, categorized outcome lists, and derived publication, attention,
and failure counts.

Items with no proposed metadata change are staged and passed directly to the
verified unchanged-media publisher. Unmatched sidecars do not block this path.
Safe metadata changes use the existing staged JPEG pipeline without duplicating
its decisions: baseline `ImageDataHash`, JPEG write planning, GPS writing,
post-write verification, and verified JPEG publication. The service adds no
format-specific writer beyond the existing JPEG implementation.

Review-required or unavailable metadata, unsupported readers or writers, and
invalid or ambiguous sidecars remain attention outcomes and are not published.
Operational failures retain the exact existing stage result and identify the
failed stage. Processing continues after per-file attention or failure; already
published files are not rolled back, and downstream diagnostic temporary files
are not deleted. Preparation is intentionally single-threaded and adds no CLI,
progress reporting, or batch recovery policy.

## Preparation command

The CLI exposes multi-file preparation through `prepare <takeout-folder>
<output-folder> [--exiftool <executable-path>]`. It requires two existing,
non-linked, non-overlapping directories, normalizes them to absolute paths, and
performs ExifTool detection exactly once. An explicit `--exiftool` value is the
authoritative executable path. After detection, the command prints a notice that
large libraries may take time and delegates the complete workflow to
`TakeoutPreparationService` without reproducing its per-file decisions.

Completed runs print publication, attention, failure, unused-JSON, and `Other`
counts. Only attention and failed media are listed, in ordinal path order, using
the concise outcome reason or failure stage and message. Retained metadata,
sidecar contents, hashes, captured ExifTool output, and stack traces are not part
of normal output. Exit code `0` means every media candidate was published, `2`
means preparation completed with attention or failed items, and `1` covers
invalid arguments, ExifTool detection failures, or library-level operational
failures. Unused JSON and `Other` counts do not affect the exit code.
