# Synology HyperBackup Cloud Image Format

Reverse-engineered from a HyperBackup 4.1.2-4045 backup of a DS918+ NAS targeting
Microsoft Azure Blob Storage. Compression and encryption were both **off**.

---

## Table of Contents

1. [Overview](#overview)
2. [Test Data](#test-data)
3. [Repository Directory Layout](#repository-directory-layout)
4. [Top-Level Metadata](#top-level-metadata)
5. [Config Layer](#config-layer)
6. [Pool Layer (Data Storage)](#pool-layer-data-storage)
7. [Binary Index Format (`7053 a86e`)](#binary-index-format-7053-a86e)
8. [File Pool Format (`e235 abc8`)](#file-pool-format-e235-abc8)
9. [Guard Layer (Cloud Sync Tracking)](#guard-layer-cloud-sync-tracking)
10. [Control Layer](#control-layer)
11. [Data Recovery Walkthrough](#data-recovery-walkthrough)
12. [Naming Conventions](#naming-conventions)
13. [SQLite Database Schemas](#sqlite-database-schemas)

---

## Overview

HyperBackup's "cloud image" format (`formatType=cloud_image`) is a
deduplication-based, versioned backup system. Data is split into chunks identified
by MD5 hashes, stored in numbered "buckets" under a Pool directory. Metadata is
tracked across multiple SQLite databases and custom binary index files.

### Key Design Principles

- **Content-addressed storage**: Every chunk is identified by its MD5 hash (16 bytes).
- **Deduplication**: Identical chunks across files and versions are stored once.
  Controlled by `support_cross_file_dedup=true` in task config.
- **Versioned snapshots**: Each backup run creates a version with per-share file
  listings, enabling point-in-time restore.
- **Two-tier storage**: Small files (<=1024 bytes by default) are stored as chunks
  in buckets. Large files go to a separate file pool as XZ-compressed blobs.
- **Cloud-aware**: Guard databases track upload state for resumable transfers.

### Format Identifiers

| Field | Value |
|-------|-------|
| `bkpVersion` | `2` |
| `formatType` | `cloud_image` |
| `bkpType` | `cloud` |
| Index format | `{"major":0,"minor":9,"sub_minor":1}` |
| Target format | `{"major":0,"minor":3,"sub_minor":0}` |

---

## Test Data

The backup was created from this source directory on a Synology DS918+:

```
/Video/hyperbackup-test/
  test.txt       5 bytes  "hello"
  dir/
    test2.txt    5 bytes  "world"
```

The backup also includes Synology system files (`@AppConfig` share) and extended
attribute streams (`@SynoEAStream` / `@eaDir` entries) for each file/directory.

---

## Repository Directory Layout

The top-level backup directory on Azure is named `synology_1.hbk`. The full
tree structure:

```
synology_1.hbk/
  _Syno_TaskConfig                      # INI: backup task configuration
  SynologyHyperBackup.bkpi              # Empty marker file (identifies repo)
  synobkpinfo.db                        # SQLite: backup identity & flags
  storage_statistics.db.2               # SQLite: per-version statistics
  test.txt                              # (see note below)
  dir/
    test2.txt                           # (see note below)

  Config/
    index_ver.json.1                    # Index format version
    target_ver.json.1                   # Target format version
    target_info.db.1                    # SQLite: target state
    target_recover.info.1               # 8-byte recovery marker
    version_info.db.2                   # SQLite: version registry (core)
    @Share/
      @AppConfig/
        1.db.2                          # SQLite: file listing, version 1
        complete_list.db.2              # SQLite: completion tracking
      Video/
        1.db.2                          # SQLite: file listing, version 1
        complete_list.db.2              # SQLite: completion tracking
    file_chunk1.index/0.idx.2           # Binary index: chunk-to-file tier 1
    file_chunk2.index/0.idx.2           # Binary index: chunk-to-file tier 2
    file_chunk3.index/0.idx.2           # Binary index: chunk-to-file tier 3
    file_chunk4.index/0.idx.2           # Binary index: chunk-to-file tier 4
    virtual_file.index/0.idx.2          # Binary index: B-tree virtual file map

  Pool/
    bucketID.counter.2                  # 8-byte BE int: next bucket ID (=3)
    0/0/
      0.bucket.2                        # Raw chunk data (bucket 0)
      0.index.2                         # Chunk index for bucket 0
      1.bucket.2                        # Raw chunk data (bucket 1)
      1.index.2                         # Chunk index for bucket 1
      2.bucket.2                        # Raw chunk data (bucket 2)
      2.index.2                         # Chunk index for bucket 2
    chunk_index/
      0.idx.2                           # Global chunk dedup hash table
    file_pool/
      1.file.2                          # XZ-compressed large file blob
      file_pool_map.db.2                # SQLite: file pool checksum map
      file_id.counter.2                 # 8-byte BE int: next file ID (=2)

  Control/
    @writer/
      v1.cformat                        # Empty: format version marker
      v1.2.-1.0.0.none.none.cinfo       # Empty: writer state marker

  Guard/
    cloud/
      0_file.db.2                       # SQLite: cloud file upload tracker
      0_bucket.db.2                     # SQLite: cloud bucket upload tracker
```

> **Note on `test.txt` and `dir/test2.txt` at root level**: These appear as
> actual files in the Azure container at the repository root. This seems to be
> a quirk of how HyperBackup uploads to Azure -- the raw user files exist
> alongside the backup metadata, but the authoritative data lives in the Pool
> buckets.

---

## Top-Level Metadata

### `_Syno_TaskConfig` (INI format)

The task configuration file describes the backup job:

```ini
[task_config]
backup_folders=["/Video/hyperbackup-test"]
backup_data_type="data"
data_compress_type=0                    # 0 = no compression
enable_data_encrypt=false
enable_xattr=true
support_cross_file_dedup=true
source_model="DS918+"
host_name="synology"
name="Microsoft Azure 1"
target_dir="synology_1.hbk"
repo_id=9
create_time=1774897149
unikey="001132A9310A_9_1774897149"      # MAC_repoID_createTime
linkkey="synology_001132A9310A_9"
```

Key fields:
- `data_compress_type`: `0`=none, other values enable compression
- `enable_data_encrypt`: controls AES encryption of data
- `enable_xattr`: backs up extended attributes as `@SynoEAStream` files
- `unikey`: globally unique identifier using NAS MAC address

### `synobkpinfo.db` (SQLite)

Identity database with two tables:

**`task_id_tb`**: Single row with the task identifier.

| task_id |
|---------|
| `synology_001132A9310A_9` |

**`backup_info_tb`**: Key-value pairs for backup properties.

| info_name | info_value | Description |
|-----------|-----------|-------------|
| `dataUnique` | `001132A9310A_9_1774897149` | Unique backup identifier |
| `bkpAuthUser` | `HyperBackup` | Auth user for the backup |
| `bkpVersion` | `2` | Backup format version |
| `bkpType` | `cloud` | Target type (cloud/local/rsync) |
| `formatType` | `cloud_image` | Storage format |
| `dataEnc` | `F` | Encryption: F=off, T=on |
| `dataComp` | `F` | Compression: F=off, T=on |
| `enableXattr` | `T` | Extended attributes: T=on |

### `SynologyHyperBackup.bkpi`

Empty marker file (0 bytes). Its presence identifies the directory as a
HyperBackup repository.

### `storage_statistics.db.2` (SQLite)

Tracks backup run statistics.

**`source_table`**: One row per backup version.

| Column | Example | Description |
|--------|---------|-------------|
| `start_time` | `1774897175` | Backup start (unix epoch) |
| `end_time` | `1774897191` | Backup end (unix epoch) |
| `source_size` | `80` | Total source data size (bytes) |
| `total_count` | `5` | Total files scanned |
| `new_count` | `5` | New files |
| `modify_count` | `0` | Modified files |
| `remove_count` | `0` | Deleted files |
| `compress_size` | `57172` | Size after compression (=uncompress when off) |
| `version_id` | `1` | References version_info |

**`target_table`**: Upload statistics.

| action_type | Meaning |
|-------------|---------|
| `3` | Initialization |
| `1` | Version backup upload |

---

## Config Layer

### `Config/version_info.db.2` (SQLite) -- Central Version Registry

This is the most important metadata database. Each backup version gets a row.

```sql
CREATE TABLE version_info (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  timestamp INTEGER,           -- Unix epoch of backup
  name TEXT,                   -- User-assigned version name
  source TEXT,                 -- JSON: what was backed up
  share TEXT,                  -- Comma-separated share names
  status TEXT,                 -- "Complete", "InProgress", etc.
  statistics TEXT,             -- JSON: detailed stats
  diff_size INTEGER,
  locked INTEGER DEFAULT 0,
  share_info BLOB,             -- Binary share metadata
  tag_db_magic BLOB,           -- 8-byte magic for tag DB
  tag_db_file_size_thr INTEGER,-- Threshold for inline tags (default 1024)
  has_suspend_dup INTEGER DEFAULT 0,
  suspend_history TEXT,        -- JSON: lifecycle events
  enc_cksum BLOB DEFAULT NULL,
  depose_time INTEGER DEFAULT -1
);
```

Example row for our test backup:

| Field | Value |
|-------|-------|
| `id` | `1` |
| `name` | `my_ver` |
| `status` | `Complete` |
| `share` | `@AppConfig,Video,` |
| `tag_db_file_size_thr` | `1024` |

The `source` JSON reveals the backup scope:
```json
{
  "share_path": ["/Video/hyperbackup-test"],
  "path_filter": {
    "/Video/hyperbackup-test": {"whitelist": ["**"]}
  }
}
```

The `statistics` JSON breaks down what was stored:
```json
{
  "compress_size": 57172,
  "new_chunk_size": 1880,
  "summary": {
    "share": {"new_cnt": 2, "new_size": 10},
    "ea":    {"new_cnt": 3, "new_size": 979},
    "app":   {"new_cnt": 2, "new_size": 56183}
  }
}
```

The `suspend_history` records lifecycle events:
```json
[
  {"client_time": 1774897180, "event": 1, "event_desc": "create"},
  {"client_time": 1774897190, "event": 6, "event_desc": "complete"}
]
```

### `Config/@Share/{ShareName}/{version_id}.db.2` -- Per-Share File Listings

Each backed-up share gets its own directory under `Config/@Share/`. Each version
produces a `{version_id}.db.2` file containing the complete file tree for that
share at that point in time.

```sql
CREATE TABLE version_list (
  name_id_v2 BLOB PRIMARY KEY,   -- 20-byte SHA-1 hash (file identity)
  pname_id_v2 BLOB,              -- 20-byte SHA-1 hash (parent directory)
  off_virtual_file INTEGER,      -- Offset into virtual file index
  file_name TEXT,                 -- File/directory name
  mtime_sec INTEGER,             -- Modify time (seconds)
  mtime_nsec INTEGER,            -- Modify time (nanoseconds)
  size INTEGER,                  -- File size in bytes
  ctime_sec INTEGER,             -- Change time (seconds)
  ctime_nsec INTEGER,            -- Change time (nanoseconds)
  mode INTEGER,                  -- Unix file mode
  dedup_id INTEGER,              -- Dedup reference
  version_id INTEGER,            -- Which backup version
  status TEXT,                   -- Entry status
  inode INTEGER,                 -- Source filesystem inode
  tag BLOB,                      -- 20 bytes: MD5(16) + size_BE(4)
  cr_time INTEGER,               -- Creation time
  nlink INTEGER,                 -- Hard link count
  fs_id INTEGER,                 -- References file_system_list
  ...
);

CREATE TABLE file_system_list (
  fs_id INTEGER PRIMARY KEY AUTOINCREMENT,
  version_id INTEGER,
  device INTEGER,
  fs_uuid TEXT                   -- Filesystem UUID
);

CREATE TABLE setting (key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE xattr (name_id_v2 BLOB PRIMARY KEY, name BLOB, value BLOB);
```

#### Name ID System

Files and directories are identified by 20-byte `name_id_v2` blobs (SHA-1
hashes). The directory hierarchy is formed through `pname_id_v2` parent
references. The root sentinel is:

```
5058F1AF 5058F1AF 8388633F 609CADB7 5A75DC9D
```

#### Tag System (Inline Small File Storage)

Files at or below `tag_db_file_size_thr` (1024 bytes) get a 20-byte `tag` blob:

```
Bytes 0-15:  MD5 checksum of file content
Bytes 16-19: File size (big-endian uint32)
```

Examples from the Video share:

| File | MD5 (hex) | Size |
|------|-----------|------|
| `test.txt` | `62109206880D38A4010A98E11243924A` | 5 |
| `test2.txt` | `6B1A0A0FFC1AA4313A9532C3D848B7A8` | 5 |
| `test.txt@SynoEAStream` | `C29A43B6150CE2E0BC180E48915E3D34` | 408 |
| `dir@SynoEAStream` | `78047B2E8DB0ABC675EB248CC2BAF67E` | 163 |

Files without tags (directories) have `tag = NULL`.

Files above the threshold (like `config.dss` at 55,292 bytes) have
`off_virtual_file = -1` and are stored in the file pool instead.

#### `off_virtual_file` Field

This integer is an offset into the virtual file index
(`Config/virtual_file.index/0.idx.2`), which maps logical file positions to
physical chunk locations in the Pool. Directories also have offsets (pointing to
directory metadata entries).

#### Entry Types in `version_list`

| Pattern | Mode | Type |
|---------|------|------|
| Regular file | `0100644` (33188) or `0100700` (33216) | User data file |
| Directory | `040700` (16832) or `040777` (16895) | Directory entry |
| `*@SynoEAStream` | `0100777` (33279) | Extended attribute stream |
| `@eaDir` | `040777` (16895) | EA metadata directory |

Synology stores POSIX/macOS extended attributes by creating companion
`@SynoEAStream` files in `@eaDir` subdirectories. These contain serialized
xattr data (Apple Double format on macOS: `com.apple.provenance`,
`com.apple.lastuseddate#PS`, `com.apple.metadata:kMDLabel_*`, etc.).

### `Config/@Share/{ShareName}/complete_list.db.2`

Simple completion tracking:

```sql
CREATE TABLE complete_list (version_id INTEGER PRIMARY KEY);
```

A row with `version_id=1` means version 1 of that share is fully backed up.
An empty table means the share backup is incomplete or in progress.

### `Config/target_info.db.1`

Target state tracking:

```sql
CREATE TABLE target_info (
  name TEXT, option TEXT, status TEXT, pid INTEGER, pcmd TEXT,
  save_pid INTEGER, save_pcmd TEXT, file_chunk_index_size TEXT, privilege BLOB
);
```

The `status` field is `"ready"` when the target is idle.

### `Config/target_recover.info.1`

8-byte binary file. Structure:

```
Bytes 0-3: Magic "R-I_" (0x522D495F)
Bytes 4-7: Flags (0x00000FFF)
```

Purpose: Recovery state marker.

### `Config/index_ver.json.1` / `Config/target_ver.json.1`

Simple JSON version numbers:

```json
{"major":0,"minor":9,"sub_minor":1}   // index_ver
{"major":0,"minor":3,"sub_minor":0}   // target_ver
```

---

## Pool Layer (Data Storage)

The Pool is where actual backup data lives. It has three components:

### 1. Buckets (`Pool/0/0/*.bucket.2` + `*.index.2`)

Buckets are the primary chunk storage mechanism. Each bucket is a pair of files:

- **`{N}.bucket.2`**: Raw concatenated chunk data (no headers, no framing)
- **`{N}.index.2`**: Binary index mapping chunks within the bucket

Buckets are organized in a two-level directory hierarchy: `Pool/{group}/{subgroup}/`.
In this backup, all buckets are in `Pool/0/0/`.

#### Bucket Contents in Our Test Backup

**Bucket 0** (1,707 bytes): System/config chunks
- Chunk 0: `_Syno_TaskConfig` content (891 bytes)
- Chunk 1: `test.txt@SynoEAStream` xattr data (408 bytes)
- Chunk 2: `test2.txt@SynoEAStream` xattr data (408 bytes)

**Bucket 1** (10 bytes): User file data
- Chunk 0: `test.txt` content = `"hello"` (5 bytes)
- Chunk 1: `test2.txt` content = `"world"` (5 bytes)

**Bucket 2** (163 bytes): Directory xattr data
- Chunk 0: `dir@SynoEAStream` xattr data (163 bytes)

#### Bucket Index Format

See [Binary Index Format](#binary-index-format-7053-a86e) Type 2 below.

#### `Pool/bucketID.counter.2`

8-byte big-endian integer: the next available bucket ID. Value `3` means
buckets 0, 1, 2 exist.

### 2. Chunk Index (`Pool/chunk_index/0.idx.2`)

Global deduplication hash table. Maps chunk MD5 hashes to their bucket
locations. This enables dedup: before storing a new chunk, its hash is looked
up here. If found, the existing bucket reference is reused.

Uses the binary index format (Type 1, sparse hash-table variant). 131,173
bytes with data at 64KB page boundaries.

### 3. File Pool (`Pool/file_pool/`)

Large files that exceed the chunking threshold are stored separately as
compressed blobs.

#### `Pool/file_pool/{id}.file.2`

XZ-compressed file blobs with a custom header. See
[File Pool Format](#file-pool-format-e235-abc8) below.

In our backup, `1.file.2` (55,696 bytes) contains `config.dss` -- a
tar archive holding `ConfigBkp/_Syno_ConfBkp.db` (SQLite database with
Synology system configuration).

#### `Pool/file_pool/file_pool_map.db.2` (SQLite)

```sql
CREATE TABLE file_pool_map (
  id INTEGER UNIQUE,
  checksum BLOB PRIMARY KEY NOT NULL,  -- 16-byte MD5
  count INTEGER                        -- Reference count
);
```

Example:

| id | checksum (hex) | count |
|----|----------------|-------|
| 1 | `EE08C925FF64D9BBC77E2449B29455D1` | 1 |

#### `Pool/file_pool/file_id.counter.2`

8-byte big-endian integer: next available file pool ID. Value `2` means
ID 1 is in use.

---

## Binary Index Format (`7053 a86e`)

All `.index.2` and `.idx.2` files share a common binary format identified by
magic bytes `7053 a86e`. All multi-byte integers are **big-endian**.

### Common Header (60 bytes)

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| `0x00` | 4 | `magic` | Always `7053 a86e` |
| `0x04` | 4 | `format_type` | 0=B-tree, 1=flat/hash, 2=bucket chunk index |
| `0x08` | 4 | `param_a` | Tree depth (type 0), 0 (type 1), entry_version (type 2) |
| `0x0C` | 4 | reserved | Always 0 |
| `0x10` | 4 | `data_offset` | Offset to first data entry |
| `0x14` | 4 | reserved | Always 0 |
| `0x18` | 4 | `declared_size` | Logical file size |
| `0x1C` | 4 | `flags` | `0x08000000` or `0x09000000` |
| `0x20` | 4 | `page_size` | `0x00008000` (32768) for paged, 0 for type 2 |
| `0x24` | 24 | reserved | Zeros |
| `0x3C` | 4 | `header_crc` | CRC32 of bytes `[0x00:0x3C]` |

### Type 2: Bucket Chunk Index (`Pool/0/0/*.index.2`)

Maps chunks within a single `.bucket.2` file. Used to locate and verify
individual chunks.

Data starts at offset `0x20`. Entry 0 is a meta-entry (8 bytes of metadata +
CRC). Entries 1..N describe actual chunks.

**Chunk Entry (32 bytes each):**

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 4 | `chunk_size` | Size of this chunk in bytes |
| 4 | 4 | `bucket_offset` | Cumulative byte offset in `.bucket.2` |
| 8 | 4 | `dedup_size` | Deduplicated size (= chunk_size when uncompressed) |
| 12 | 16 | `md5_hash` | MD5 hash of the chunk data |
| 28 | 4 | `fingerprint` | Unknown 4-byte value (not CRC32) |

**Verified**: Chunk offsets are cumulative (chunk N starts at sum of sizes 0..N-1),
and the total equals the `.bucket.2` file size exactly. MD5 hashes verified
against known data (`md5("hello")` = `5d41402abc4b2a76b9719d911017c592`).

Example for bucket 1 (contains `test.txt` and `test2.txt`):

```
Entry 1: size=5  offset=0  md5=5d41402abc4b2a76b9719d911017c592  ("hello")
Entry 2: size=5  offset=5  md5=7d793037a0760186574b0282f2f435e7  ("world")
```

### Type 1: Flat/Hash Index

Used for:
- `Config/file_chunk{1,2,3,4}.index/0.idx.2` -- chunk-to-file mapping tiers
- `Pool/chunk_index/0.idx.2` -- global dedup hash table

These use 32KB declared page sizes with data at 64KB physical boundaries.
The chunk index acts as a hash table mapping MD5 chunk hashes to bucket
locations (bucket ID + offset).

### Type 0: B-Tree Paged Index (`Config/virtual_file.index/0.idx.2`)

A B-tree index of the virtual filesystem. Maps file/directory metadata with
timestamps, permissions, sizes, and pointers to chunk data.

**Header specifics:**
- `format_type=0`, `tree_depth=2`
- `declared_size=262368` (logical), physical file is 196,904 bytes
- Pages at 64KB boundaries (0x00000, 0x10000, 0x20000, 0x30000)

**Page structure:**
```
[4-byte CRC32] [B-tree node entries...]
```

**B-tree internal node**: Alternating child pointers and key entries.

**Child pointer (8 bytes):**
```
Bytes 0-1: page_id (uint16)
Bytes 2-3: reserved (0)
Bytes 4-7: offset_in_page (uint32)
```

**Directory entry (~40 bytes):**

| Offset | Size | Field |
|--------|------|-------|
| 0 | 4 | Node ID |
| 4 | 4 | Parent ID |
| 8 | 4 | UID |
| 12 | 4 | GID |
| 16 | 4 | Create time (seconds) |
| 20 | 4 | Create time (nanoseconds) |
| 24 | 4 | Reserved |
| 28 | 4 | Modify time (seconds) |
| 32 | 4 | Modify time (nanoseconds) |
| 36 | 4 | Checksum |

**File entry (~48 bytes):** Similar but includes mode and size fields.

---

## File Pool Format (`e235 abc8`)

Large files stored in `Pool/file_pool/{id}.file.2` use this format:

| Offset | Size | Description |
|--------|------|-------------|
| `0x00` | 4 | Magic: `e235 abc8` |
| `0x04` | 252 | Header (mostly zeros, metadata fields TBD) |
| `0x100` | ~76 | Metadata block |
| `0x14C` | var | XZ-compressed payload |

The payload in our test backup is an XZ-compressed **tar archive** (ustar
format) containing `ConfigBkp/_Syno_ConfBkp.db` -- the Synology configuration
backup database.

To extract:
```bash
# Skip the 0x14C (332) byte header, decompress with xz
dd if=1.file.2 bs=1 skip=332 | xz -d > output.tar
tar xf output.tar
```

---

## Guard Layer (Cloud Sync Tracking)

The Guard layer tracks which objects have been successfully uploaded to the
cloud storage backend, enabling resumable uploads.

### `Guard/cloud/0_file.db.2` (SQLite)

```sql
CREATE TABLE file_info (
  type INTEGER NOT NULL,    -- Object type code
  name TEXT,                -- Object name/path
  idx INTEGER,              -- Object index
  mtime INTEGER,            -- Modification time
  size INTEGER,             -- Object size in bytes
  crc BLOB,                 -- CRC (NULL in practice)
  status INTEGER,           -- 1=synced
  check_status INTEGER,     -- Integrity check status
  mtime_nsec INTEGER
);
```

**File type codes:**

| Type | Meaning | Example |
|------|---------|---------|
| 1 | Pool auxiliary file | `Pool/bucketID.counter`, `file_pool_map.db` |
| 2 | Share version database | `@AppConfig` idx=1, `Video` idx=1 |
| 3 | Virtual file index | 196,904 bytes |
| 4 | File chunk index | Tiers 1-4 |
| 5 | Chunk dedup index | 131,173 bytes |
| 8 | File pool blob | `1.file.2` (55,696 bytes) |

### `Guard/cloud/0_bucket.db.2` (SQLite)

Same schema as `0_file.db.2`. Tracks bucket uploads.

**Bucket type codes:**

| Type | Meaning |
|------|---------|
| 6 | Bucket index (`.index.2` files) |
| 7 | Bucket data (`.bucket.2` files) |

Each bucket has two entries: one for data (type 7) and one for its index (type 6).

---

## Control Layer

### `Control/@writer/`

Contains empty marker files whose **filenames** encode state:

- **`v1.cformat`**: Format version 1 marker
- **`v1.2.-1.0.0.none.none.cinfo`**: Writer state encoded in filename

The `.cinfo` filename pattern appears to be:
```
v{format_ver}.{revision}.{field3}.{field4}.{field5}.{compression}.{encryption}.cinfo
```

Where `none.none` confirms compression and encryption are off.

---

## Data Recovery Walkthrough

To restore files from a HyperBackup cloud image without Synology software:

### Step 1: Identify Versions

Query `Config/version_info.db.2`:
```sql
SELECT id, name, timestamp, status, share FROM version_info;
```

### Step 2: List Files for a Version

For each share, query `Config/@Share/{ShareName}/{version_id}.db.2`:
```sql
SELECT file_name, size, mode, off_virtual_file, tag,
       mtime_sec, name_id_v2, pname_id_v2
FROM version_list
WHERE version_id = 1;
```

Rebuild the directory tree using `name_id_v2` / `pname_id_v2` parent
references. Root sentinel: `5058F1AF5058F1AF8388633F609CADB75A75DC9D`.

### Step 3: Recover Small Files (tag != NULL, size <= threshold)

The `tag` blob contains the MD5 hash (bytes 0-15) and size (bytes 16-19).
Use the MD5 to find the chunk in bucket indexes:

```
For each Pool/0/0/{N}.index.2:
  Parse chunk entries (32 bytes each starting at offset 0x40)
  Match MD5 hash from tag against entry bytes 12-27
  Read chunk_size bytes at bucket_offset from {N}.bucket.2
```

### Step 4: Recover Large Files (off_virtual_file == -1, tag == NULL)

Look up the file in `Pool/file_pool/file_pool_map.db.2` by its checksum.
The `id` maps to `Pool/file_pool/{id}.file.2`. Skip the header (offset
0x14C) and decompress with XZ/LZMA.

### Step 5: Skip Metadata Entries

Filter out entries where `file_name` contains `@SynoEAStream` or `@eaDir`
unless you need extended attributes. These are Synology's EA serialization
and can generally be ignored for plain file recovery.

---

## Naming Conventions

| Pattern | Meaning |
|---------|---------|
| `.1` / `.2` suffix | File format generation (e.g., `target_info.db.1` vs `version_info.db.2`) |
| `@` prefix on dirs | System/metadata directory (`@Share`, `@AppConfig`, `@writer`, `@eaDir`) |
| `@SynoEAStream` suffix | Synology extended attribute stream file |
| `*_v2` column names | Version 2 of the naming scheme (SHA-1 based) |
| `{N}.bucket.2` + `{N}.index.2` | Paired bucket data + index files |
| `{id}.file.2` | File pool blob by numeric ID |
| `*.counter.2` | 8-byte big-endian auto-increment counter |

---

## SQLite Database Schemas

### Complete Schema Reference

Below is every SQLite database and its full schema for reference.

#### `synobkpinfo.db`
```sql
CREATE TABLE task_id_tb (task_id TEXT);
CREATE TABLE backup_info_tb (info_name TEXT, info_value TEXT);
```

#### `storage_statistics.db.2`
```sql
CREATE TABLE source_table (
  start_time INTEGER, end_time INTEGER, source_size INTEGER,
  total_count INTEGER, modify_count INTEGER, new_count INTEGER,
  unchange_count INTEGER, remove_count INTEGER,
  file_size_distribution TEXT, version_id INTEGER,
  compress_size INTEGER, uncompress_size INTEGER,
  PRIMARY KEY (end_time)
);
CREATE TABLE target_table (
  start_time INTEGER, end_time INTEGER, action_type INTEGER,
  target_size INTEGER, version_id INTEGER,
  PRIMARY KEY (end_time)
);
CREATE TABLE versions (version_number INTEGER PRIMARY KEY);
```

#### `Config/version_info.db.2`
```sql
CREATE TABLE version_info (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  timestamp INTEGER, name TEXT, source TEXT, share TEXT,
  status TEXT, statistics TEXT, diff_size INTEGER,
  locked INTEGER NOT NULL DEFAULT 0,
  share_info BLOB, missing_src TEXT,
  tag_db_magic BLOB, tag_db_file_size_thr INTEGER,
  has_suspend_dup INTEGER DEFAULT 0,
  bad_share TEXT DEFAULT '', internal_share TEXT DEFAULT '',
  suspend_history TEXT DEFAULT '',
  enc_cksum BLOB DEFAULT NULL,
  depose_time INTEGER DEFAULT -1
);
```

#### `Config/target_info.db.1`
```sql
CREATE TABLE target_info (
  name TEXT, option TEXT, status TEXT, pid INTEGER, pcmd TEXT,
  save_pid INTEGER, save_pcmd TEXT,
  file_chunk_index_size TEXT, privilege BLOB
);
```

#### `Config/@Share/{ShareName}/{version}.db.2`
```sql
CREATE TABLE version_list (
  name_id_v2 BLOB PRIMARY KEY, pname_id_v2 BLOB,
  off_virtual_file INTEGER, file_name TEXT,
  mtime_sec INTEGER, mtime_nsec INTEGER, size INTEGER,
  ctime_sec INTEGER, ctime_nsec INTEGER, mode INTEGER,
  dedup_id INTEGER, version_id INTEGER, cand_id INTEGER,
  status TEXT, inode INTEGER, tag BLOB, cr_time INTEGER,
  disk_entry_tag BLOB, nlink INTEGER, fs_id INTEGER,
  middle_file_path TEXT DEFAULT NULL
);
CREATE TABLE file_system_list (
  fs_id INTEGER PRIMARY KEY AUTOINCREMENT,
  version_id INTEGER, device INTEGER, fs_uuid TEXT
);
CREATE TABLE setting (key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE xattr (name_id_v2 BLOB PRIMARY KEY, name BLOB, value BLOB);
```

#### `Config/@Share/{ShareName}/complete_list.db.2`
```sql
CREATE TABLE complete_list (version_id INTEGER PRIMARY KEY);
```

#### `Guard/cloud/0_file.db.2` and `Guard/cloud/0_bucket.db.2`
```sql
CREATE TABLE file_info (
  type INTEGER NOT NULL, name TEXT, idx INTEGER,
  mtime INTEGER, size INTEGER, crc BLOB,
  status INTEGER, check_status INTEGER, mtime_nsec INTEGER
);
```

#### `Pool/file_pool/file_pool_map.db.2`
```sql
CREATE TABLE file_pool_map (
  id INTEGER UNIQUE,
  checksum BLOB PRIMARY KEY NOT NULL,
  count INTEGER
);
```
