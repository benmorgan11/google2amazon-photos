# Photo Migration

[![CI](https://github.com/benmorgan11/google2amazon-photos/actions/workflows/ci.yml/badge.svg)](https://github.com/benmorgan11/google2amazon-photos/actions/workflows/ci.yml)

A .NET command-line project made to prepare Google Takeout photos and videos to be imported into Amazon Photos.

## Status

The first feature, a read-only inventory of an extracted Takeout folder, is implemented.

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

## Privacy

Keep real exports, photos, sidecars, credentials, and migration reports outside the repository. Tests use synthetic data.

## Architecture

See docs/architecture.md.
