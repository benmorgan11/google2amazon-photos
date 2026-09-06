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
