# Photo Migration

[![CI](https://github.com/benmorgan11/google2amazon-photos/actions/workflows/ci.yml/badge.svg)](https://github.com/benmorgan11/google2amazon-photos/actions/workflows/ci.yml)

A .NET command-line project made to prepare Google Takeout photos and videos to be imported into Amazon Photos.

## Status

Read-only inventory, Takeout analysis, ExifTool checking, metadata planning, and
output preparation commands are implemented. Planning compares selected embedded
JPEG, HEIC/HEIF, and MOV/MP4 metadata with unambiguous sidecar metadata. Preparation
publishes verified output copies while leaving the Takeout source unchanged.

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
  attention case was found, and for `prepare`, every media item was published.
- `1`: arguments were invalid or the operation could not be completed.
- `2`: analysis, planning, or preparation completed, but one or more media items
  need attention or failed.

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

Use `plan` as the read-only preview before preparing output. When you are ready,
create a separate output directory and run:

```console
dotnet run --project src/PhotoMigration.Cli -- prepare <takeout-folder> <output-folder> [--exiftool <executable-path>]
```

For example, on macOS:

```console
dotnet run --project src/PhotoMigration.Cli -- prepare "/Users/alex/Downloads/Takeout/Google Photos" "/Users/alex/Pictures/Amazon Photos Ready" --exiftool "/usr/local/bin/exiftool"
```

The completed summary separates each verified publication path:

```text
Preparation summary:
Total media: 4
Published unchanged: 1
Published with verified GPS: 1
Published with verified UTC capture time: 1
Published with verified GPS and UTC capture time: 1
Total published: 4
Attention required: 0
Failed: 0
```

Both directories must already exist and must not overlap. Always use an output
directory separate from the Takeout export: the command never changes source
media and never overwrites an existing output file. Files that need no metadata
change are published byte-for-byte. UTC capture-time changes are written and
verified for JPEG/JPG, HEIC/HEIF, PNG, MOV, and MP4. GPS changes are currently
restored and verified only for JPEG/JPG files. Files requiring uncertain changes
or changes to an unsupported format remain unpublished and are listed for
attention, while per-file operational failures are reported without stopping
later files.

The preparation summary counts total media, all four publication outcomes, total
published media, attention items, failures, unused JSON, and `Other` files. Only
attention and failed media are listed individually. Completed runs confirm that
the Takeout source was not changed and show the output directory.

## Privacy

Keep real exports, photos, sidecars, credentials, and migration reports outside the repository. Tests use synthetic data.

## Architecture

See docs/architecture.md.
