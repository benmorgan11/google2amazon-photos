# Photo Migration

[![CI](https://github.com/benmorgan11/google2amazon-photos/actions/workflows/ci.yml/badge.svg)](https://github.com/benmorgan11/google2amazon-photos/actions/workflows/ci.yml)

A .NET command-line project made to prepare Google Takeout photos and videos to be imported into Amazon Photos.

## Status

Read-only inventory, Takeout analysis, ExifTool checking, and metadata planning
commands are implemented. Planning compares selected embedded JPEG, HEIC/HEIF,
and MOV/MP4 metadata with unambiguous sidecar metadata and reports proposed
changes and review cases without changing source files.

## Goals

- Preserve untouched source files.
- Prepare separate output copies without re-encoding media.
- Preserve capture dates and locations where supported.
- Report ambiguous metadata and unsupported files.
- Validate Amazon Photos behavior with a small sample before full-library use.

## Development

Requires the .NET 10 SDK specified in global.json.

Build: `dotnet build`

Run tests: `dotnet test`

Run the CLI: `dotnet run --project src/PhotoMigration.Cli`

Inventory an already-extracted Takeout folder:

```console
dotnet run --project src/PhotoMigration.Cli -- inventory "/path/to/Takeout"
```

The command prints each regular file's relative path and byte length, followed by
the total file count. Paths use `/` separators and ordinal sorting. Symbolic links
and reparse-point entries are skipped; the input folder itself cannot be a link.

Analyze the inventoried files and their supplemental JSON candidates:

```console
dotnet run --project src/PhotoMigration.Cli -- analyze "/path/to/Takeout"
```

The analysis summary counts matched media, invalid sidecars, unmatched media,
ambiguous media, unused JSON candidates, and other files. Details are printed
for invalid, unmatched, and ambiguous media; successful matches are not listed
individually. Ambiguous details include each candidate sidecar path and matching
rule. The command does not write files or reports.

Command exit codes are:

- `0`: the command completed successfully; for `analyze` and `plan`, no defined
  attention case was found.
- `1`: arguments were invalid or the operation could not be completed.
- `2`: analysis or planning completed, but one or more media items need
  attention.

Unused JSON candidates and `Other` files are counted but do not produce exit
code `2`, because they may be album metadata or unrelated export files.

Check whether ExifTool is installed and runnable:

```console
dotnet run --project src/PhotoMigration.Cli -- check-exiftool
```

To validate one authoritative executable path instead of searching `PATH`:

```console
dotnet run --project src/PhotoMigration.Cli -- check-exiftool --path "/usr/local/bin/exiftool"
```

The command prints the detected version and absolute executable path on success.
It is read-only and returns exit code `1` when ExifTool is missing, cannot run,
times out, returns invalid version output, or receives invalid arguments.

Create a read-only metadata plan for an extracted Takeout folder:

```console
dotnet run --project src/PhotoMigration.Cli -- plan "/path/to/Takeout"
```

The command detects ExifTool once using the current search path. To require one
specific installation instead, supply an authoritative executable path:

```console
dotnet run --project src/PhotoMigration.Cli -- plan "/path/to/Takeout" --exiftool "/usr/local/bin/exiftool"
```

The planning summary counts sidecar states and metadata-plan statuses. Details
are limited to unmatched, invalid, ambiguous, review-required, unavailable, and
not-yet-supported items. Successful metadata values are not printed. Exit code
`2` indicates these attention cases; unused JSON and `Other` files are counted
but do not affect the exit code. The command creates no reports or output files.

## Privacy

Keep real exports, photos, sidecars, credentials, and migration reports outside the repository. Tests use synthetic data.

## Architecture

See docs/architecture.md.
