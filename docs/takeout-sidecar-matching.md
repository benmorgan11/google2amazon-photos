# Google Takeout Media and Sidecar Matching

Research snapshot: September 6, 2026

## Purpose

This document proposes a conservative first version of the algorithm that
associates media files from Google Photos Takeout with their supplemental JSON
metadata.

The matching feature must:

- Never modify, rename, move, or delete source files.
- Avoid assigning metadata when more than one interpretation is possible.
- Record how every match was made.
- Report unmatched and ambiguous files for later review.
- Work from a complete inventory rather than processing files one at a time.

This phase only discovers associations. Writing metadata into photos and videos
belongs to a later milestone.

## What Google officially documents

Google confirms that downloading a photo or video can cause the operating system
to assign a new filesystem timestamp. Google says the original timestamp remains
in the media file's embedded metadata. Additional Google Photos information that
was not part of the original file, such as comments, is exported in a secondary
JSON file.

Google also documents that large exports can be divided into multiple archive
files and that very recent changes might not appear if they occur between
requesting and creating the export.
[Google Photos Help](https://support.google.com/photos/answer/3024190)

Google does not appear to publish a stable specification for:

- Supplemental JSON filenames.
- The relationship between duplicate suffixes and sidecar names.
- Filename truncation rules.
- Edited-copy naming.
- A guaranteed JSON schema.

The remaining behavior in this document is based on examples reported by
maintained migration tools and their users. It should be treated as observed
behavior rather than a Google guarantee.

## Observed Takeout layouts

Community tools report that a Takeout export commonly contains media in year
folders, album folders, or both. A photo may therefore appear multiple times in
the export.

Album folders can contain their own metadata JSON. The album metadata filename
may be localized, including names equivalent to `metadata.json` in other
languages. Filenames such as edited-copy suffixes can also vary by export
language.
[immich-go Takeout notes](https://github.com/simulot/immich-go/blob/main/docs/misc/google-takeout.md)

Commonly observed per-media pairs include:

```text
IMG_1234.jpg
IMG_1234.jpg.json
```

and:

```text
IMG_1234.jpg
IMG_1234.jpg.supplemental-metadata.json
```

Recent export reports show duplicate numbers moving to a different part of the
sidecar name:

```text
Scan35(1).jpg
Scan35.jpg.supplemental-metadata(1).json
```

Other exports keep the number on the media portion:

```text
VIDEO0036(1).mp4
VIDEO0036(1).mp4.supplemental-metadata.json
```

Both forms have been observed in real Takeout data.
[immich-go discussion #650](https://github.com/simulot/immich-go/discussions/650)

Long filenames introduce more variation. Reports show the
`supplemental-metadata` suffix being shortened and the original media filename
being truncated. A proposed matcher used by another migration project models a
total filename limit of approximately 51 characters, but this should remain a
testable hypothesis rather than a guaranteed Google rule.
[retakeout documentation](https://github.com/ek68794998/retakeout),
[supplemental metadata matching patch](https://gist.github.com/AkuEgor/41f758cdf305d6c97608cd5f06a140fc)

An observed extension-omission case looks like:

```text
Peanut Butter Balls.jpg
Peanut Butter Balls.supplemental-metadata.json
```

Because the sidecar omits `.jpg`, this becomes ambiguous if files such as
`Peanut Butter Balls.jpg` and `Peanut Butter Balls.png` are both present.
[immich-go issue #674](https://github.com/simulot/immich-go/issues/674)

Edited copies may share the original media's sidecar:

```text
IMG_1234.jpg
IMG_1234-edited.jpg
IMG_1234.jpg.supplemental-metadata.json
```

This means one sidecar may legitimately describe an original and its edited
sibling.
[immich-go issue #1422](https://github.com/simulot/immich-go/issues/1422)

## Observed JSON fields

A commonly reported supplemental JSON object contains fields resembling:

```json
{
  "title": "IMG_1234.jpg",
  "description": "",
  "imageViews": "12",
  "creationTime": {
    "timestamp": "1700000000",
    "formatted": "..."
  },
  "photoTakenTime": {
    "timestamp": "1600000000",
    "formatted": "..."
  },
  "geoData": {
    "latitude": 0.0,
    "longitude": 0.0,
    "altitude": 0.0,
    "latitudeSpan": 0.0,
    "longitudeSpan": 0.0
  },
  "url": "...",
  "googlePhotosOrigin": {}
}
```

These fields are demonstrated in community export examples rather than an
official schema.
[immich-go discussion #875](https://github.com/simulot/immich-go/discussions/875)

The matcher should make these conservative interpretations:

- `title` is matching evidence, not a unique identifier.
- `photoTakenTime.timestamp` is a candidate capture time.
- `creationTime.timestamp` is a separate creation or upload-related time and
  should not automatically replace capture time.
- Numeric timestamps should be used instead of localized `formatted` values.
- A latitude and longitude pair of `0, 0` should initially be treated as missing
  unless other evidence establishes it as intentional.
- Unknown fields should be preserved when reading the document rather than
  causing a parse failure.

Metadata precedence between embedded EXIF or video metadata and the JSON values
will be decided in a later milestone.

## Proposed matching algorithm

### 1. Build the complete catalog

Scan all supplied Takeout directories or archive parts before attempting any
match. Do not match independently inside each archive part.

For every regular file, retain:

- Its physical path.
- Its root-relative path.
- Its exact filename.
- Its directory.
- Its extension.
- Its byte length.
- Whether it appears to be JSON.
- Any parsing or access error.

Preserve the original text of paths. A normalized comparison form may be stored
separately.

If archive parts were extracted into separate folders, calculate a logical
Takeout-relative path for matching without physically merging or overwriting
those folders.

### 2. Classify JSON files

Attempt to parse JSON files and classify them by structure.

A likely media sidecar contains several media-related fields such as `title`,
`photoTakenTime`, `creationTime`, `geoData`, `url`, or `googlePhotosOrigin`.

Album and export-level JSON must be kept separate. Filename alone is insufficient
because album metadata filenames may be localized.

Malformed JSON should produce an invalid-sidecar result. It should not stop the
rest of the inventory.

### 3. Generate candidates in priority order

For each media file, generate sidecar candidates using these rules:

1. Same logical directory and exact legacy name:

   ```text
   <complete media filename>.json
   ```

2. Same logical directory and exact supplemental name:

   ```text
   <complete media filename>.supplemental-metadata.json
   ```

3. Duplicate-index transformation:

   ```text
   photo(12).jpg
   photo.jpg.supplemental-metadata(12).json
   ```

   The duplicate number must support any number of digits. A filename that
   already contains parentheses must not be altered beyond the final recognized
   export suffix.

4. Deterministic truncation candidates based on observed Takeout patterns.

   This rule should only be accepted when it produces one unique
   media-to-sidecar relationship. The assumed filename limit and generated
   candidate must be included in the match evidence.

5. Extension-omitted candidate:

   ```text
   photo.jpg
   photo.supplemental-metadata.json
   ```

   Accept this only when exactly one media file in the logical directory has
   that stem.

6. Edited sibling relationship.

   After the original file is matched, associate an exact `-edited` sibling with
   the original's sidecar only when the relationship is unique. Localized edited
   suffixes should remain unresolved until they are verified against sample
   exports or added through configuration.

The JSON `title` should confirm a candidate when possible. It should not be used
for unrestricted matching across the whole export because year and album folders
can contain media with identical titles.

### 4. Resolve the candidate set

A match is accepted only when the highest-priority applicable rule produces one
unambiguous result.

Each result should contain:

- Media path.
- Sidecar path.
- Match rule.
- Confidence category.
- Whether the JSON title agrees with the media filename.
- Whether truncation or duplicate-index transformation was required.
- Any associated edited sibling.

If two candidates have equal evidence, report the media and both candidates as
ambiguous. Do not select the first file returned by the filesystem.

One sidecar may be associated with an original and a confirmed edited sibling. A
normal media file should otherwise have at most one primary sidecar.

### 5. Produce a report

Suggested result categories are:

- Exact legacy match.
- Exact supplemental match.
- Duplicate-index match.
- Unique truncated match.
- Unique extension-omitted match.
- Edited sibling association.
- Unmatched media.
- Orphan sidecar.
- Ambiguous match.
- Album or export metadata.
- Invalid JSON.

Each category should include counts and individual file paths. This makes the
behavior inspectable and useful in a portfolio demonstration.

## Rules deliberately excluded from v1

Version one should not:

- Use fuzzy filename similarity.
- Choose the nearest file alphabetically.
- Match globally by `title` alone.
- Assume every trailing `(number)` was added by Google.
- Treat every same-basename photo and video as a Live Photo pair.
- Deduplicate album and year-folder copies.
- Write timestamps, GPS, or other metadata.
- Re-encode media.
- Delete sidecars after matching.

Content hashing can later help identify duplicate media across album and year
folders, but it should be a separate feature.

## Synthetic test cases

The first implementation should cover:

1. Exact legacy `.json` pairing.
2. Exact `.supplemental-metadata.json` pairing.
3. Duplicate number moved after `supplemental-metadata`.
4. Duplicate number retained in the media portion.
5. Multi-digit duplicate numbers.
6. A legitimate filename that already contains parentheses.
7. A long filename with a unique truncated sidecar.
8. Two long filenames that produce the same truncated candidate and remain
   ambiguous.
9. Extension-omitted sidecar with one possible media file.
10. Extension-omitted sidecar with `.jpg` and `.png` candidates.
11. Original and `-edited` files sharing one sidecar.
12. Localized album metadata classified by JSON structure.
13. Malformed JSON.
14. Media without a sidecar.
15. Sidecar without media.
16. Media and sidecar found in separate archive parts but the same logical
    directory.
17. Identically named copies in a year folder and an album folder.
18. Same-basename HEIC and MOV files left as unresolved companion candidates.
19. Zero-valued GPS fields treated as missing.
20. Invalid or out-of-range timestamp text.
21. Unicode-normalized filenames that compare equally but have different raw
    bytes.
22. Case-only filename differences.
23. Verification that no source file contents, timestamps, or paths change.

Tests should use synthetic directories and small fake files. Real personal photos
and Takeout metadata must not enter the repository.

## Open questions

Before implementing the more speculative rules, inspect a sanitized filename
listing from an actual current Takeout export:

- Which sidecar naming style does the export use?
- Is the observed 51-character limit still consistent?
- How does the export handle names that already end in `(number)`?
- Which language was used for album metadata and edited-copy names?
- Do `geoData` and any EXIF-derived location fields disagree?
- Are media and sidecars separated across archive parts?
- Are HEIC/MOV or JPEG/MP4 pairs explicitly connected anywhere?
- How frequently is `title` different from the exported filename?
- Does extracting multiple parts create path collisions?
- Which embedded photo and video timestamps does Amazon Photos actually
  prioritize?

These answers should refine later versions without weakening the rule that
uncertain matches remain unresolved.
