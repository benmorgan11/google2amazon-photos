# Embedded Metadata Strategy

Research snapshot: September 6, 2026

## Decision

Use ExifTool as an externally installed metadata engine, called through a small,
controlled .NET process wrapper. The first release should find ExifTool through
an explicit setting or the system `PATH`, verify its version, and give a clear
error when it is unavailable. Do not add a separate C# metadata library unless a
later requirement justifies having two metadata engines.

ExifTool documents broad read and write support for formats relevant to this
project, including JPEG, HEIC, HEIF, MOV, MP4, PNG, TIFF, WebP, and DNG. Actual
write capabilities vary by format and must be checked before use.
[ExifTool application documentation](https://exiftool.org/exiftool_pod2.html)

MetadataExtractor is a useful .NET reader, but it does not solve the project's
writing requirement. Using ExifTool for both reading and writing also avoids
having two libraries interpret the same tags differently.
[MetadataExtractor for .NET](https://github.com/drewnoakes/metadata-extractor-dotnet)

## Installation and process safety

ExifTool's official macOS installer supports Intel Macs and normally installs
the executable under `/usr/local/bin`. The application should check an explicit
path, the system `PATH`, and common macOS locations, then run `exiftool -ver`
without changing any files. The .NET CLI can be published for `osx-x64` while
ExifTool remains a documented prerequisite. Bundling ExifTool should wait for a
separate review of packaging, updates, and licensing.
[Installing ExifTool](https://exiftool.org/install.html)

The wrapper must use `ProcessStartInfo.ArgumentList` or an equivalent argument
API, never a shell command assembled from filenames or metadata. It should use
absolute paths, capture standard output and error separately, check the exit
code, enforce a timeout, support cancellation, and preserve warnings in typed
results. Filenames and sidecar values are untrusted input. Complete metadata and
GPS values should not be logged unless the user asks for them.

Start with one ExifTool process per operation because it is easier to test. A
persistent process can be considered later if performance requires it.

## Conservative metadata policy

The reader should request only fields needed for planning and retain each value's
source tag, group, original text, parsed value, offset status, and warnings. It
must not immediately collapse several embedded tags into one answer.

QuickTime dates require special care. ExifTool documents that integer QuickTime
timestamps should use UTC, although some cameras store local values. ExifTool
therefore does not assume UTC unless `QuickTimeUTC` is enabled, while string dates
may contain explicit offsets. [ExifTool QuickTime tags](https://exiftool.org/TagNames/QuickTime.html),
[ExifTool FAQ](https://exiftool.org/faq.html)

Google documents that downloads may receive new filesystem timestamps while the
original timestamp remains embedded in the media. Google Photos can also export
additional information in secondary JSON files. Current filesystem timestamps
are therefore diagnostic data, not reliable capture-time evidence.
[Google Photos Help](https://support.google.com/photos/answer/3024190)

### Capture time

1. Keep a valid embedded capture time when one exists.
2. If it is absent, propose the sidecar's `photoTakenTime`.
3. If embedded and sidecar values disagree, report a conflict; do not overwrite
   the embedded value automatically.
4. Use Google `creationTime` only as a labeled, low-confidence fallback when no
   capture time exists.
5. Never infer capture time from extraction time or current filesystem dates.

### Timezone

A Unix timestamp identifies an instant, not the original timezone or local
wall-clock time. Preserve an embedded offset when present. If only a Google epoch
timestamp exists, record that the local capture timezone is unknown. Do not infer
it from the current computer, GPS coordinates, folder names, or daylight-saving
rules. Writing an absolute instant into an offset-free EXIF field must wait for
controlled Amazon testing.

### Location, titles, and descriptions

Keep valid embedded GPS data. If it is missing, propose valid sidecar coordinates.
Report conflicts instead of silently choosing one source. The sidecar parser
correctly preserves zero values; a later planning layer should normally treat a
`0, 0` pair as missing unless other evidence shows it is intentional. Preserve
altitude independently.

Retain Google titles and descriptions for analysis, but defer writing them. They
are not needed for the first goal of preserving dates and locations.

## Safe output and verification

**Never run ExifTool against a Takeout source. Never modify, rename, move, or
delete source media or sidecars.** Metadata changes must be made only to a
temporary file beneath a separate output root.

The proposed write workflow is:

1. Choose an unused destination and create a unique temporary output path.
2. Copy the source bytes to that temporary path.
3. Compare source and copy using full-file SHA-256 hashes.
4. Save the copy's original metadata and ExifTool `ImageDataHash`.
5. Run ExifTool only against the temporary copy and check its exit code and
   warnings.
6. Read the metadata back and verify the intended fields.
7. Compare the pre-write and post-write `ImageDataHash` values.
8. Reject the result if the media-data hash changed.
9. Move the verified temporary file to its final unused destination without
   overwriting anything.
10. Record provenance, decisions, hashes, and warnings outside the repository.

SHA-256 verifies that the initial copy is byte-for-byte identical. ExifTool's
`ImageDataHash` then checks media payload data while excluding most metadata. It
supports formats including JPEG, TIFF, PNG, HEIC, MOV, and MP4; MOV and MP4 hashes
include video and audio data. These checks answer different questions and both
are required. [ExifTool extra tags](https://exiftool.org/TagNames/Extra.html)

ExifTool normally creates an original backup when writing. A future writer may
use `-overwrite_original` only on its temporary output copy because the untouched
Takeout source remains the backup. Filesystem timestamps on the output should be
set deliberately after verification. A failed operation may remove only its own
unique temporary file. [ExifTool writing documentation](https://exiftool.org/writing.html)

## Staged rollout

Start with JPEG. Its EXIF behavior is established, Amazon lists it as supported,
and `ImageDataHash` can verify its image payload. Add HEIC and HEIF only after
controlled tests confirm dates, offsets, GPS, orientation, previews, and media
hashes. Treat MOV and MP4 as a separate phase because QuickTime dates, offsets,
and location fields need their own policy. Other formats remain analysis-only
until they receive dedicated write and verification tests.
[Amazon Photos file requirements](https://digprjsurvey.amazon.co.uk/csad/help/node/GGU2SU8Y22DZYRMQ)

## Amazon behavior still requiring controlled testing

Amazon documents accepted file types and advertises full-resolution photo
storage, but those claims do not establish byte identity or which EXIF, XMP,
QuickTime, or filesystem date Amazon uses for sorting.
[Amazon Photos overview](https://www.aboutamazon.com/news/amazon-prime/how-to-use-amazon-photos-storage)

Before enabling writes for a format, upload non-personal samples with deliberately
different embedded, sidecar, and filesystem dates and known GPS values. Include
JPEG EXIF and XMP cases, HEIC, and MOV/MP4 dates with and without offsets. Record
the displayed date and location, download the files, and compare returned
metadata and media-data hashes. Label the results as observed behavior with a
test date because Amazon's import pipeline may change.

The project must not promise correct Amazon sorting or metadata preservation
until this controlled matrix has been completed. Google and Samsung Motion
Photos are listed as unsupported by Amazon; Live Photo pairing and other compound
media also need separate investigation.
[Amazon Photos file requirements](https://digprjsurvey.amazon.co.uk/csad/help/node/GGU2SU8Y22DZYRMQ)
