# Photo Migration

[![CI](https://github.com/benmorgan11/google2amazon-photos/actions/workflows/ci.yml/badge.svg)](https://github.com/benmorgan11/google2amazon-photos/actions/workflows/ci.yml)

A .NET command-line project made to prepare Google Takeout photos and videos to be imported into Amazon Photos.

## Status

Read-only inventory and Takeout analysis commands are implemented. Analysis
classifies files, applies the currently supported sidecar filename rules, and
parses metadata from unambiguous matches without changing source files.

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

- `0`: the command completed successfully; for `analyze`, no invalid,
  unmatched, or ambiguous media needs attention.
- `1`: arguments were invalid or the operation could not be completed.
- `2`: analysis completed, but invalid sidecars, unmatched media, or ambiguous
  media needs attention.

Unused JSON candidates and `Other` files are counted but do not produce exit
code `2`, because they may be album metadata or unrelated export files.

## Privacy

Keep real exports, photos, sidecars, credentials, and migration reports outside the repository. Tests use synthetic data.

## Architecture

See docs/architecture.md.
