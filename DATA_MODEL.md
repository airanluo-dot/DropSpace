# DropSpace Data Model

## Modeling decision

Use one aggregate plus composed payload records. `ClipboardItem`, `FileItem`, `ImageItem`, `TextItem`, and `UrlItem` are conceptual projections, not persistence subclasses.

## Core types

### DropItem

| Field | Type | Notes |
|---|---|---|
| Id | GUID/16-byte key | Stable application identity |
| Source | enum | `Space`, `Clipboard` |
| Kind | enum | `File`, `Folder`, `Text`, `Image`, `Url`, `Color`, `Code`, `Unknown` |
| Title | string | User-facing, bounded length |
| CreatedAtUtc | timestamp | Capture/add time |
| LastUsedAtUtc | timestamp nullable | Updated on meaningful action |
| IsPinned | bool | Retention exemption/filter state |
| Status | enum | `Available`, `Missing`, `Unavailable`, `Processing`, `Error` |
| Fingerprint | bytes/string nullable | Duplicate/self-loop support |
| SearchText | string | Normalized bounded search projection |
| PayloadId | GUID nullable | References app-owned payload |
| MetadataJson | JSON nullable | Versioned non-query-critical metadata only |
| Revision | integer | Optimistic stale-result protection |

### FileReference

- `ItemId`
- `OriginalPath`
- `NormalizedPath`
- `EntryKind` file/folder/shortcut/other
- `Extension`
- `KnownSize` nullable
- `KnownModifiedAtUtc` nullable
- `VolumeHint` nullable; never treated as stable identity
- `LastCheckedAtUtc` nullable
- `AvailabilityReason` nullable

The path is a reference, not a guaranteed identifier. MVP does not promise rename/move tracking.

### TextPayload

- `ItemId`
- `InlineText` nullable
- `PayloadId` nullable for large text
- `CharacterCount`
- `DetectedSubtype` plain/url/color/code/json/path/email/phone/unknown
- `DetectionConfidence` low/medium/high for presentation only
- `LanguageHint` nullable

Subtype detection never grants security trust or changes the original string.

### ImagePayload

- `ItemId`
- `PayloadId` required
- `PixelWidth`, `PixelHeight`
- `EncodedBytes`
- `MimeType`
- `HasAlpha` nullable
- `ThumbnailRevision`

### UrlMetadata

- `ItemId`
- `NormalizedUrl`
- `DisplayUrl`
- `Host`
- `Scheme`

No remote title/favicon fetch in MVP.

File/folder records can use either source. `Source=Space` means deliberate Temporary Space staging; `Source=Clipboard` means an automatic Explorer copy-history reference. Both use `file_references`, but duplicate checks are source-scoped so the same external file may legitimately appear once in each product surface. Clipboard batch metadata contains only a fingerprint, count and item index; it never owns or copies the referenced source.

### PayloadRecord

- `Id`
- `Kind` image/large-text
- `RelativePath`
- `ByteLength`
- `ContentHash`
- `CreatedAtUtc`
- `StorageVersion`

Only relative app-owned paths are stored. Resolve them against one controlled payload root and reject traversal.

## Relationships

```text
DropItem 1 --- 0..1 FileReference
DropItem 1 --- 0..1 TextPayload
DropItem 1 --- 0..1 ImagePayload
DropItem 1 --- 0..1 UrlMetadata
DropItem 0..1 --- 1 PayloadRecord
DropItem 1 --- 0..* ItemTag (future, not MVP UI)
```

Pinned is a field on `DropItem`; there is no Pinned table or copied item.

## SQLite schema v1

```sql
CREATE TABLE schema_info (
  version INTEGER NOT NULL,
  applied_at_utc TEXT NOT NULL
);

CREATE TABLE items (
  id BLOB PRIMARY KEY,
  source INTEGER NOT NULL,
  kind INTEGER NOT NULL,
  title TEXT NOT NULL,
  created_at_utc TEXT NOT NULL,
  last_used_at_utc TEXT NULL,
  is_pinned INTEGER NOT NULL DEFAULT 0,
  status INTEGER NOT NULL,
  fingerprint BLOB NULL,
  search_text TEXT NOT NULL,
  payload_id BLOB NULL,
  metadata_json TEXT NULL,
  revision INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE file_references (
  item_id BLOB PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
  original_path TEXT NOT NULL,
  normalized_path TEXT NOT NULL,
  entry_kind INTEGER NOT NULL,
  extension TEXT NULL,
  known_size INTEGER NULL,
  known_modified_at_utc TEXT NULL,
  volume_hint TEXT NULL,
  last_checked_at_utc TEXT NULL,
  availability_reason TEXT NULL
);

CREATE TABLE text_payloads (
  item_id BLOB PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
  inline_text TEXT NULL,
  character_count INTEGER NOT NULL,
  detected_subtype INTEGER NOT NULL,
  detection_confidence INTEGER NOT NULL,
  language_hint TEXT NULL
);

CREATE TABLE image_payloads (
  item_id BLOB PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
  pixel_width INTEGER NOT NULL,
  pixel_height INTEGER NOT NULL,
  encoded_bytes INTEGER NOT NULL,
  mime_type TEXT NOT NULL,
  has_alpha INTEGER NULL,
  thumbnail_revision INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE url_metadata (
  item_id BLOB PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
  normalized_url TEXT NOT NULL,
  display_url TEXT NOT NULL,
  host TEXT NOT NULL,
  scheme TEXT NOT NULL
);

CREATE TABLE payloads (
  id BLOB PRIMARY KEY,
  kind INTEGER NOT NULL,
  relative_path TEXT NOT NULL UNIQUE,
  byte_length INTEGER NOT NULL,
  content_hash BLOB NOT NULL,
  created_at_utc TEXT NOT NULL,
  storage_version INTEGER NOT NULL
);
```

Foreign-key reference from `items.payload_id` is enforced in the final migration SQL after ordering/cycle behavior is verified; deletion uses an application transaction plus deferred physical cleanup.

## Indexes

- `items(source, created_at_utc DESC)` for collection paging.
- `items(is_pinned, created_at_utc DESC)` partial/compound pinned query.
- `items(kind, created_at_utc DESC)` for type filtering.
- `items(fingerprint, source, created_at_utc DESC)` for fingerprint chronology/diagnostics; it is not used to globally suppress Clipboard history.
- `file_references(normalized_path)` for duplicate/reference refresh.
- `url_metadata(host)` for URL filtering.
- Search: begin with indexed projections and measured queries; add FTS5 only after confirming deployment availability and migration cost.

## Limits and normalization

- Titles capped at 512 characters.
- Inline text default threshold 64 KiB; total accepted text default 2 MiB.
- `search_text` is normalized/case-folded and capped; full text remains in payload.
- Metadata JSON has a schema/version field and a size cap; query-critical data gets columns.
- Timestamps stored in UTC ISO 8601 with invariant parsing.
- Paths preserve original display form and a comparison form; do not lowercase blindly for all providers.

## Duplicate policy

- Space: exact normalized duplicate prompts/merges by default rather than silently adding.
- Clipboard: a process-local serialized coordinator collapses only consecutive identical observed snapshots after a successful persistence. `A → B → A` creates three rows; no historical fingerprint query, uniqueness constraint, recency rewrite, or duplicate counter is used.
- A fingerprint is not a security boundary and may use SHA-256 over canonicalized content.

## Migrations

1. Acquire the single-instance/database writer boundary.
2. Verify integrity pragmas appropriate to startup budget.
3. Create a pre-migration backup for destructive or complex migrations.
4. Execute one numbered migration in a transaction.
5. Update schema version only in the same transaction.
6. Validate required tables/indexes and reopen repositories.
7. On failure, preserve the original and show recovery; never create a blank replacement silently.

Migrations are forward-only in production. Downgrade means restoring a compatible backup, not reverse-running SQL.

## Cache and payload consistency

- Database commit happens after durable payload write.
- Deletion commits logical removal first and queues physical cleanup.
- Startup orphan scan is bounded and moves unknown payloads to quarantine before deletion.
- Thumbnail files contain no authority; missing/corrupt files regenerate.
- Cache keys include item revision, requested logical size, rasterization scale, and decoder version.

## Retention queries

- Only `Source=Clipboard AND IsPinned=0` is eligible for automatic retention.
- Apply age cutoff, then count cap from newest to oldest.
- Clear-range operations use `CreatedAtUtc`, display affected count, and default to preserving pinned items.
- Space items have no automatic expiry in MVP.

## v2 transfer schema

Schema version 2 adds `paired_devices` and `transfer_sessions`. Peer rows contain only a device ID, display name, Windows platform, pinned certificate fingerprint, DPAPI secret-key ID, capability flags, timestamps, and blocked state. Transfer rows contain direction/mode/state, peer ID, item/byte totals, progress, completion time, and a coarse error category. Pairing secrets and receive staging bytes are stored outside SQLite under application data and are never included in search or diagnostics.

## v3 pending removal state

Schema 3 adds the following nullable columns to `items`:

- `pending_delete_token TEXT NULL`
- `pending_delete_expires_at_utc TEXT NULL`

`ix_items_pending_delete` indexes the token and expiry. A pending row remains fully intact (same ID, metadata, payload reference, timestamps, pin state, and batch metadata) but is excluded from normal queries/counts and mutation projections. `UndoPendingRemovalAsync(token)` clears both columns atomically. `FinalizePendingRemovalAsync(token)` deletes the rows and unreferenced payload metadata in one transaction, returning relative paths only for payload records that were actually removed; the App-owned payload store then performs confined cleanup. Startup finalizes expired tokens. The user-facing Clear Clipboard operation uses the same pending path and excludes pinned rows by default.
