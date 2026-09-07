# Google Takeout Media and Sidecar Matching

Research snapshot: September 6, 2026

## Scope and source quality

This document describes the matching and read-only analysis behavior currently
implemented by the project, then separates it from unresolved Takeout patterns.
The implementation has automated tests built from synthetic files. It has not
been validated against a real personal Takeout export.

Google confirms that downloading media can assign new filesystem timestamps
while original timestamps remain embedded. Additional Google Photos information
may be exported in secondary JSON files. Google also notes that large exports
can be divided into multiple archives.
[Google Photos Help](https://support.google.com/photos/answer/3024190)

Google does not publish a stable specification for supplemental JSON filenames,
duplicate suffixes, truncation, edited-copy naming, or the complete JSON schema.
Filename patterns below are community observations, not Google guarantees.

## Useful observed patterns

Community reports show both legacy and supplemental names:

```text
IMG_1234.jpg
IMG_1234.jpg.json

IMG_1234.jpg
IMG_1234.jpg.supplemental-metadata.json
```

Duplicate numbers can move behind `supplemental-metadata`:

```text
Scan35(1).jpg
Scan35.jpg.supplemental-metadata(1).json
```

Other exports retain the number in the media portion. Both forms have been
observed. [immich-go discussion #650](https://github.com/simulot/immich-go/discussions/650)

```text
VIDEO0036(1).mp4
VIDEO0036(1).mp4.supplemental-metadata.json
```

Some supplemental names omit the media extension:

```text
Peanut Butter Balls.jpg
Peanut Butter Balls.supplemental-metadata.json
```

This is ambiguous when files such as `Peanut Butter Balls.jpg` and
`Peanut Butter Balls.png` share the same directory and stem.
[immich-go issue #674](https://github.com/simulot/immich-go/issues/674)

Community tools also report localized album metadata, filename truncation, and
edited copies that may share an original file's sidecar. These observations are
useful future evidence, not current matching rules.
[immich-go Takeout notes](https://github.com/simulot/immich-go/blob/main/docs/misc/google-takeout.md),
[immich-go issue #1422](https://github.com/simulot/immich-go/issues/1422),
[retakeout documentation](https://github.com/ek68794998/retakeout)

## Current implementation

### Inventory and classification

The inventory recursively lists regular files from one already-extracted root,
uses `/` in logical relative paths, sorts them ordinally, skips links and reparse
points, and fails the complete operation on traversal errors. It never modifies
the input folder.

The classifier uses only the final extension. It recognizes the project's
initial photo and video candidate extensions, recognizes `.json` without opening
the file, and retains unsupported files as `Other`. Extension comparison during
classification is ordinal and case-insensitive; this only identifies candidates
and does not claim that Amazon supports them.

### Filename matching

The matcher receives media and JSON candidates explicitly. It compares complete
logical paths using ordinal, case-sensitive matching and only matches files in
the same directory. It implements four rules:

1. Legacy exact: `<media filename>.json`.
2. Supplemental exact: `<media filename>.supplemental-metadata.json`.
3. Duplicate number: `photo(12).jpg` can match
   `photo.jpg.supplemental-metadata(12).json`.
4. Extension omitted: `photo.jpg` can match
   `photo.supplemental-metadata.json`.

The duplicate suffix must be a final positive number immediately before a normal
media extension, with no leading zero. Only that final suffix is removed, so
earlier parentheses remain unchanged. Extension omission removes only the final
normal extension and is not combined with the duplicate-number transformation.
Exact rules still work for extensionless filenames and dotfiles.

Results are deterministic. If multiple rules find candidates for one media file,
all candidates remain ambiguous. If media with different extensions claim one
extension-omitted sidecar, each result is ambiguous. A sidecar is not assigned to
multiple unrelated media files. Case-only filename differences do not match.

### JSON parsing and analysis

Only sidecars from unambiguous matches are parsed. The parser returns optional,
typed title, description, creation time, photo-taken time, latitude, longitude,
altitude, and URL values. Unix timestamps may be JSON strings or numbers. Zero
coordinates are preserved for later policy decisions. Missing fields are allowed
and unknown fields are ignored. Malformed JSON, invalid roots, and malformed
recognized timestamps or coordinates produce a path-bearing project exception
without exposing the complete JSON.

The analyzer runs inventory, classification, matching, and parsing in that order.
One invalid matched sidecar is recorded without stopping other media. Results
separate successfully parsed matches, invalid sidecars, unmatched media,
ambiguous media and candidates, unused JSON candidates, and `Other` files.
Unused JSON is not called orphan metadata because it may describe an album or
the export itself.

The `analyze` CLI prints counts plus deterministic details for invalid,
unmatched, and ambiguous media. It does not list every successful match. Exit
code `2` identifies those attention cases; unused JSON and `Other` files do not
cause that exit code. The command clearly reports that no files were changed.

## Safety boundary

Inventory, classification, matching, parsing, analysis, and CLI reporting are
read-only. They do not rename, move, delete, or write media or JSON. Matching
uses paths only; it does not open files. Source Takeout files must never be
modified by later metadata work either.

## Future and unresolved work

The following behavior is deliberately not implemented:

- Filename truncation. Community reports suggest shortened names and an
  approximate length limit, but that remains a hypothesis requiring sanitized
  examples. [Supplemental metadata matching patch](https://gist.github.com/AkuEgor/41f758cdf305d6c97608cd5f06a140fc)
- Edited-copy sharing, including localized edited suffixes.
- Localized album names and album or export-level JSON classification.
- Reconciliation across multiple extracted archive parts.
- Live Photo pairing and Google or Samsung Motion Photo handling.
- Title-based corroboration or matching.
- Unicode normalization of filenames.

These cases must remain unresolved rather than being assigned by fuzzy matching,
alphabetical proximity, or unrestricted JSON titles. Future work should use
sanitized filenames or synthetic fixtures first and clearly label any real-world
findings as observed behavior. Metadata precedence and output writing are
separate decisions documented in the
[embedded metadata strategy](embedded-metadata-strategy.md).
