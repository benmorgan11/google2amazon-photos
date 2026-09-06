# Photo Migration

A .NET command-line project made to prepare Google Takeout photos and videos to be imported into Amazon Photos.

## Status

Initial scaffold. Migration functionality is not implemented yet.

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

## Privacy

Keep real exports, photos, sidecars, credentials, and migration reports outside the repository. Tests use synthetic data.

## Architecture

See docs/architecture.md.