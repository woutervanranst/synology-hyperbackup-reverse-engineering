# Synology HyperBackup Cloud Image Format

> [!NOTE]
> This research is intended to identify which HyperBackup chunks are required
> for each restore point, so chunks that are not currently needed can be moved
> to the Azure Archive tier and later rehydrated before restore when needed.
> The analysis was performed on my own backups, without leaked Synology source
> code or confidential materials. This document describes observed behavior and
> file formats, not copied Synology implementation code.

Reverse-engineered from HyperBackup 4.1.2-4045 backups of a DS918+ NAS targeting
Microsoft Azure Blob Storage. Two backup sets were analyzed:

1. **Unencrypted/uncompressed** — `data_compress_type=0`, `enable_data_encrypt=false`
2. **Encrypted + compressed** — `data_compress_type=1`, `enable_data_encrypt=true`

Both backed up the same source folder. The encryption/compression variant is
documented alongside the base format throughout this document.

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
11. [Encryption and Compression](#encryption-and-compression)
12. [Data Recovery Walkthrough](#data-recovery-walkthrough)
13. [Naming Conventions](#naming-conventions)
14. [SQLite Database Schemas](#sqlite-database-schemas)
15. [License](#license)

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
- **Optional encryption**: AES-256-CBC for data, with per-version keys wrapped by
  NaCl `crypto_box_seal` (X25519 sealed boxes — *not* RSA; see the correction in
  [Encryption and Compression](#encryption-and-compression)). Encrypts chunk data,
  filenames, and config values while leaving SQLite schemas/structure in the clear.
- **Optional compression**: LZ4 block compression applied before encryption.

### Format Identifiers

| Field | Value |
|-------|-------|
| `bkpVersion` | `2` |
| `formatType` | `cloud_image` |
| `bkpType` | `cloud` |
| Index format | `{"major":0,"minor":9,"sub_minor":1}` |
| Target format | `{"major":0,"minor":3,"sub_minor":0}` |

---

## Reference Implementation (PoC)

A working .NET 10 proof-of-concept that reads this format lives in [`src/`](src/):

- **`HyperBackup.Core`** — a storage-agnostic library: SQLite + binary-index
  parsing, chunk/file-pool restore, AES / NaCl sealed-box / LZ4 / XZ crypto, and
  an Azure-tier-aware archive planner. It runs against either a local folder
  (`LocalDirectoryStorage`) or Azure Blob Storage (`AzureBlobStorage`).
- **`HyperBackup.Cli`** (`hbk`) — a CLI exposing four core capabilities:
  1. `list` — enumerate every file in a backup version (decrypting filenames)
  2. `restore` — restore a file's bytes (verified end-to-end on the unencrypted
     sample *and* on a real encrypted Azure backup, for small single-chunk files)
  3. `archive-plan` — decide which blobs can move to the Azure Archive tier
  4. `rehydrate` — determine and rehydrate the exact blob(s) needed for one file

  plus `info`, `demo`, and the raw-blob `ls` / `get` discovery helpers. See
  [Using the Reference Implementation](#using-the-reference-implementation).

```bash
dotnet run --project src/HyperBackup.Cli -- <command> [storage] [options]
```

---

## Using the Reference Implementation

### Requirements & build

Only the **.NET 10 SDK** is required. The tool shells out to nothing — every
format dependency is a NuGet package: XZ via **SharpCompress**, LZ4 via
**K4os.Compression.LZ4**, NaCl sealed boxes via **Sodium.Core**, SQLite via
**Microsoft.Data.Sqlite**, and Azure via **Azure.Storage.Blobs**.

```bash
dotnet build HyperBackup.slnx
```

The sandbox can't produce a native apphost, so invoke the built DLL directly
rather than a `hbk` executable:

```bash
dotnet src/HyperBackup.Cli/bin/Debug/net10.0/hbk.dll <command> [storage] [options]
```

### Storage selection

Pick exactly one backend:

```bash
# Local folder on disk
--local <dir>

# Azure Blob Storage (account + key)
--azure-account <account> --azure-container <name> [--azure-prefix <prefix>] --azure-key <key>

# Alternative Azure credentials
--azure-connection-string <cs>   # or env HBK_AZURE_CONNECTION_STRING
--azure-sas <containerSasUrl>    # or env HBK_AZURE_SAS
```

Secrets may be supplied via environment variables instead of flags:
`HBK_AZURE_KEY`, `HBK_AZURE_CONNECTION_STRING`, `HBK_AZURE_SAS`. The
`--azure-prefix` is the repository folder inside the container (e.g.
`synology_1.hbk`).

### Encryption

```bash
--rsa-key <file>        # path to the exported 32-byte X25519 private key
                        #   (the "..._private.pem" file — raw bytes; for cloud backups)
--passphrase <p>        # backup passphrase (decrypts local-format encKeys.1); or env HBK_PASSPHRASE
```

Despite the flag name `--rsa-key`, the file is the raw 32-byte X25519 private
key (see [Encryption and Compression](#encryption-and-compression)).

### Commands

| Command | What it does |
|---------|--------------|
| `info` | Show format, encryption/compression flags, key availability, and versions |
| `list [--version N] [--all]` | List files in a version (decrypts filenames when keys are available); `--all` includes EA-metadata entries |
| `restore --version N --file <name-or-path> [--out <file>]` | Restore one file's bytes and verify MD5 + size |
| `archive-plan [--hot-version N ...] [--execute]` | Decide which blobs can move to Azure Archive (keep metadata Hot, archive bulk data); `--execute` performs the tier change |
| `rehydrate --version N --file <name> [--execute] [--priority high]` | Determine and rehydrate the exact blob(s) one file needs |
| `demo` | End-to-end showcase on the local sample |
| `ls [--prefix p] [--limit N]` | Raw blob listing with sizes/tiers (discovery, no parsing) |
| `get --blob <repo-path> [--offset O --length L] --out <file>` | Download one blob, optionally a byte range |

Examples:

```bash
dotnet src/HyperBackup.Cli/bin/Debug/net10.0/hbk.dll info    --local target-azure-azure-nopassword
dotnet src/HyperBackup.Cli/bin/Debug/net10.0/hbk.dll list    --local target-azure-azure-nopassword
dotnet src/HyperBackup.Cli/bin/Debug/net10.0/hbk.dll restore --local target-azure-azure-nopassword --version 1 --file test.txt
dotnet src/HyperBackup.Cli/bin/Debug/net10.0/hbk.dll ls      --local target-azure-azure-nopassword --limit 50
```

### Worked example 1 — local unencrypted sample

```bash
# Full end-to-end showcase (overview → list → archive-plan → rehydrate → restore)
dotnet src/HyperBackup.Cli/bin/Debug/net10.0/hbk.dll demo --local target-azure-azure-nopassword

# Restore a single file and verify it
dotnet src/HyperBackup.Cli/bin/Debug/net10.0/hbk.dll restore \
  --local target-azure-azure-nopassword --version 1 --file test.txt --out restored/test.txt
```

### Worked example 2 — encrypted Azure backup

Secrets live in a gitignored `secrets/` directory; pass them by path or env so
they never land in the shell history or this README. Use your own values for
the `<account>` / `<container>` placeholders.

```bash
# Inspect the backup (format, encryption, versions, whether keys resolved)
dotnet src/HyperBackup.Cli/bin/Debug/net10.0/hbk.dll info \
  --azure-account <account> --azure-container <container> --azure-prefix synology_1.hbk \
  --azure-key "$(cat secrets/azure.key)" \
  --rsa-key secrets/private.pem

# Restore one file from the encrypted backup
dotnet src/HyperBackup.Cli/bin/Debug/net10.0/hbk.dll restore \
  --azure-account <account> --azure-container <container> --azure-prefix synology_1.hbk \
  --azure-key "$(cat secrets/azure.key)" \
  --rsa-key secrets/private.pem \
  --version 1 --file test.txt --out restored/test.txt
```

### Status / limitations

Verified working against **real encrypted cloud backups**, and byte-for-byte
against an unencrypted backup's original source files:

- `list` (including correctly decrypted filenames),
- `restore` of **single-chunk and large multi-chunk files** — e.g. a 7 MB,
  875-chunk file restored MD5-identical to source, and encrypted multi-chunk
  files restored to valid originals (MD5 + size verified on every restore),
- `archive-plan`, and
- per-file `rehydrate` (resolves every bucket a file's chunks span).

How multi-chunk files are resolved (see [Chunk Manifest](#chunk-manifest)):
a file's chunks are the contiguous run, in global write order, whose `dedup_size`s
sum to the file size; the `tag` (= `MD5` of the concatenated chunk content-MD5s)
disambiguates. This needs **no chunk-manifest parsing** — only the bucket indexes
and the tag, both plaintext even in encrypted backups.

Remaining gaps:

- **Deduplicated files** whose chunks are *not* contiguous (a chunk shared with
  an earlier file) can't be resolved by the implicit run model; they need the
  `file_chunk*.index` manifest, whose 4-tier node format is only partly decoded.
- **`@AppConfig` system files without an inline tag** (e.g. `_Syno_TaskConfig`)
  are located via `virtual_file.index` and are not restored (not user data).
- The **file-pool internal segmenting** is only partially understood.

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

When encryption is enabled, two additional files appear:

```
  Config/
    encKeys.1                           # 16-byte header (no key material in cloud format)
    public.pem.1                        # 32-byte X25519 public key (raw, not PEM text)
  Pool/
    vkey.db.2                           # SQLite: sealed-box-wrapped per-version keys
```

All other files exist in the same paths with the same structure.

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
- `data_compress_type`: `0`=none, `1`=LZ4 compression
- `enable_data_encrypt`: controls AES-256-CBC encryption of chunk data and filenames
- `enable_xattr`: backs up extended attributes as `@SynoEAStream` files
- `unikey`: globally unique identifier using NAS MAC address (`MAC_repoID_createTime`)

When encryption is enabled, `backup_folders` and `backup_filter` are wrapped
in `/*eNC00*<base64>*/` envelopes (see
[Config Value Encryption](#config-value-encryption)):

```ini
enc_backup_folders="/*eNC00*Urc2c9+qLVLe45nP4P6pqPjgVZAwp8QpLmQMmg==*/"
enc_backup_filter="/*eNC00*2reF14Pn1CzbM0+e/70GUCP8noWTJvWQI4+JIuPzcaobSdY=*/"
```

The plaintext `backup_folders` and `backup_filter` fields are replaced with
empty values (`[]` and `{}`), and the encrypted versions are stored in
`enc_backup_folders` and `enc_backup_filter` instead.

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

In an encrypted+compressed backup, `dataEnc` = `T` and `dataComp` = `T`.

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
  tag BLOB,                      -- 20 bytes: MD5(MD5(content))(16) + size_BE(4)
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

Files at or below `tag_db_file_size_thr` (1024 bytes) in user shares get a
20-byte `tag` blob that acts as the chunk locator:

```
Bytes 0-15:  MD5(MD5(content))   -- the double-MD5 dedup key (NOT the raw content MD5)
Bytes 16-19: File size (big-endian uint32)
```

> **Correction (verified by the reference implementation in `src/`):** the first
> 16 bytes are **not** `MD5(content)`. They are `MD5` applied to the content's MD5,
> i.e. `MD5(MD5(content))`. The bucket index
> ([Type 2](#type-2-bucket-chunk-index-pool00index2)) stores the *content* MD5; to
> locate a file's chunk, compute `MD5(content_md5)` for each bucket-index entry and
> match it against the file's `tag[0:16]`. Because the bucket-index MD5 is always
> stored in the clear, this lookup also works for encrypted backups.

Examples from the Video share (`tag` field shown = `MD5(MD5(content))`):

| File | `tag[0:16]` = MD5(MD5(content)) | content MD5 (in bucket index) | Size |
|------|--------------------------------|-------------------------------|------|
| `test.txt` | `62109206880D38A4010A98E11243924A` | `5D41402ABC4B2A76B9719D911017C592` (`md5("hello")`) | 5 |
| `test2.txt` | `6B1A0A0FFC1AA4313A9532C3D848B7A8` | `7D793037A0760186574B0282F2F435E7` (`md5("world")`) | 5 |
| `dir@SynoEAStream` | `78047B2E8DB0ABC675EB248CC2BAF67E` | `157B2B8B4D1D043832653A98F1ED6BF4` | 163 |

Files without tags (directories) have `tag = NULL`. Files in the `@AppConfig`
system share also have `tag = NULL` even when small (e.g. `_Syno_TaskConfig`,
~900 bytes); their content is located through `virtual_file.index` /
`file_chunk*.index` rather than an inline tag, and is not restored by the PoC
(Synology system data, not user files).

Files above the threshold (like `config.dss`) have `off_virtual_file = -1` and are
stored in the file pool instead.

> **Multi-chunk files (cracked & verified):** large files are split into many
> chunks that can span **multiple buckets** (each `.bucket` is filled to a fixed
> ~50 MiB capacity, e.g. 52,432,240 bytes). A file's chunks are the **contiguous
> run, in global write order (bucket id, then offset within a bucket), whose
> `dedup_size`s sum to the file size**. The `tag` generalizes to **`MD5` of the
> concatenation of that run's chunk content-MD5s** — for a single chunk this is
> exactly `MD5(MD5(content))` — and disambiguates when several runs sum to the same
> size. Restore = read those chunks in order, decrypt/decompress each, concatenate.
> This is resolvable from metadata alone (bucket indexes + tag), so it works for
> encrypted backups too (verified: a 7 MB / 875-chunk file restored MD5-identical
> to its source). *Caveats:* very large files use a higher-tier `tag` (the run is
> then identified by its unique size), and **deduplicated** files whose chunks are
> not contiguous need the explicit [chunk manifest](#chunk-manifest).

#### `off_virtual_file` Field

This integer is the byte offset of the file's record inside the virtual file index
(`Config/virtual_file.index/0.idx.<gen>`). That record is per-entry **metadata
only** — node id, parent id, uid/gid, create/modify timestamps (sec + nsec),
permissions, and a trailing checksum (a fixed ~56 bytes); it does **not** contain
chunk locations. File content is located via the `tag` + bucket indexes (above),
not via this index. A value of `-1` is the sentinel meaning "stored in the file
pool".

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

The 60-byte common header is followed by a 32-byte meta region (`0x20`–`0x3F`).
**Actual chunk entries begin at offset `0x40`** and are 32 bytes each; the entry
count is `(file_size - 0x40) / 32`. (Verified by the reference implementation.)

**Chunk Entry (32 bytes each, starting at `0x40`):**

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
- `Config/file_chunk{1,2,3,4}.index/0.idx.2` -- the chunk manifest tiers (see below)
- `Pool/chunk_index/0.idx.2` -- global dedup hash table

These use 32KB declared page sizes with data at 64KB physical boundaries.
The chunk index acts as a hash table mapping MD5 chunk hashes to bucket
locations (bucket ID + offset).

### Chunk Manifest

A file's content is the **contiguous run of whole chunks**, in global write order
(bucket id ascending, then offset within the bucket), whose `dedup_size`s sum to
the file size. The file's run is identified without parsing any custom index:

```
ordered_chunks = all bucket-index entries, sorted by (bucket_id, bucket_offset)
for each contiguous run [i..j) whose sum(dedup_size) == file.size:
    if MD5( concat( ordered_chunks[i..j].content_md5 ) ) == file.tag[0:16]:
        -> this run is the file's chunk list
(if no run matches the tag — very large files use a higher-tier tag — but exactly
 one run sums to the size, that run is the file)
```

So the `tag` is the file→chunks verifier, and the run order is implicit. This is
why restore needs only the (always-plaintext) bucket indexes and tags, even for
encrypted backups.

The `file_chunk{1,2,3,4}.index` files are a 4-tier index that stores this
file→chunk mapping explicitly (a Merkle-style tier tree; the top tier feeds the
large-file `tag`). The PoC does **not** parse them because the implicit run model
above resolves all non-deduplicated files. They are only required for
**deduplicated** files, whose chunks are *not* contiguous (a chunk is shared with
an earlier file), and for which the run model therefore fails. Their exact node
format is only partially decoded.

### Type 0: B-Tree Paged Index (`Config/virtual_file.index/0.idx.2`)

A B-tree index of the virtual filesystem. Each leaf entry holds file/directory
**metadata only** — node id, parent id, uid/gid, create/modify timestamps
(sec + nsec), permissions, and a trailing checksum — in fixed ~56-byte records
(confirmed: a 37 MB file's record is the same size as a 6 KB file's). It does
**not** store chunk locations or pointers; a file's chunks come from the
[chunk manifest](#chunk-manifest). The original "pointers to chunk data"
description is incorrect.

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

The payload in our test backup is an XZ-compressed **GNU tar archive**
containing `ConfigBkp/_Syno_ConfBkp.db` -- the Synology configuration backup
database. Observed header fields just before the payload: a CRC32-like value at
`0x13C` and two little-endian 32-bit sizes at `0x144`/`0x148`.

To extract (note: in the bundled sample this stream is truncated/corrupt, so
`xz` exits non-zero after emitting partial output):
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

## Encryption and Compression

When `enable_data_encrypt=true` and/or `data_compress_type=1` are set in
`_Syno_TaskConfig`, HyperBackup applies LZ4 compression and AES-256-CBC
encryption to chunk data, filenames, and certain config values. The repository
structure remains identical -- all SQLite databases, binary index files, JSON
configs, and counters exist in the same paths with the same schemas. Only the
**content** of certain fields changes.

> [!IMPORTANT]
> **Major correction (verified by the reference implementation against a real
> encrypted cloud backup + the exported key):** the per-version keys are **not**
> RSA-wrapped. They use **NaCl `crypto_box_seal`** — libsodium *sealed boxes*
> (Curve25519/X25519 + XSalsa20-Poly1305). The "encryption key" Synology exports
> (e.g. `Microsoft Azure 1_private.pem`) is a **32-byte X25519 private key** (raw
> bytes, despite the `.pem` name), and `public.pem.1` is the matching 32-byte
> X25519 public key (`X25519(private) == public.pem.1`, confirmed). The `vkey`
> blob sizes are the giveaway: `sealed_box(32-byte key)` = 32 (ephemeral pk) + 32
> (ciphertext) + 16 (MAC) = **80 bytes**; `sealed_box(16-byte IV)` = 32 + 16 + 16
> = **64 bytes**. Decryption:
> ```
> file_key = crypto_box_seal_open(vkey.rsa_vkey)      -> 32-byte AES-256 key
> file_iv  = crypto_box_seal_open(vkey.rsa_vkey_iv)   -> 16-byte AES IV
> ```
> The exported X25519 key is **required** for cloud backups and is **not**
> derivable from the password (the password only ever decrypts the *local*
> `encKeys.1`, which is empty in the cloud). The full content pipeline, verified
> end-to-end on real data:
> `crypto_box_seal_open` → **AES-256-CBC** (PKCS7) → **LZ4 block** decompress to
> `dedup_size` (small/incompressible chunks are stored *uncompressed* — detect by
> `unpadded_len == dedup_size`) → MD5 matches the bucket index. Filenames decrypt
> with `key = SHA256(x25519_private || unikey)`, `iv = MD5(unikey || unikeySalt2)`,
> AES-256-CBC, base64(`+_`) — also confirmed. The "RSA-640/RSA-512/PKCS1" wording
> below is the original (incorrect) guess, kept for context.

### What Is Encrypted vs. Plaintext

| Category | Encrypted? | Details |
|----------|-----------|---------|
| SQLite database schemas | No | All tables, indexes, columns identical |
| SQLite structural data (IDs, timestamps, sizes, modes, inodes) | No | Fully readable |
| Chunk data in `.bucket.2` files | **Yes** | AES-256-CBC encrypted |
| File pool blobs (`.file.2`) | **Yes** | Encrypted after XZ compression |
| Filenames in `version_list.file_name` | **Yes** | AES-CBC, base64 encoded |
| `source` field in `version_info` | **Yes** | `/*eNC00*...*/ ` envelope |
| `backup_folders` / `backup_filter` in `_Syno_TaskConfig` | **Yes** | `/*eNC00*...*/ ` envelope |
| Tags (MD5 + size in `version_list.tag`) | No | Hash of original plaintext content |
| `name_id_v2` / `pname_id_v2` blobs | No | Same SHA-1 computation |
| Version statistics JSON | No | Fully readable |
| Guard databases | No | All upload tracking in the clear |
| Counters, JSON version files, marker files | No | Identical to unencrypted |

### Additional Files in Encrypted Repositories

Three files appear only when encryption is enabled:

#### `Config/encKeys.1` (16 bytes)

Header-only file. In local HyperBackup, this file additionally stores the
32-byte X25519 private key, AES-256-CBC-encrypted with the password key. In
cloud format, only the 16-byte header is present -- the X25519 private key is
**not** stored in the cloud backup and must be exported from the NAS separately.

```
Offset  Hex                               ASCII
0x00    65 6b 68 74 61 72 00 02           ekhtar..    magic + version=2
0x08    01 00 00 00 00 00 00 00           ........    flags + padding
```

| Offset | Size | Field | Value |
|--------|------|-------|-------|
| 0x00 | 6 | Magic | `ekhtar` (ASCII) |
| 0x06 | 2 | Version | `0x0002` |
| 0x08 | 2 | Flags | `0x0100` |
| 0x0A | 6 | Reserved | zeros |

#### `Config/public.pem.1` (32 bytes)

Binary blob -- NOT a PEM-format file despite the name. It is the **32-byte
X25519 public key** used by the NaCl sealed boxes that wrap each version key.
Verified: `X25519(exported_private_key) == public.pem.1`. The matching 32-byte
X25519 private key is what the NAS exports as `Microsoft Azure 1_private.pem`
(also raw bytes, not PEM text).

#### `Pool/vkey.db.2` (SQLite -- Version Key Database)

Stores per-version encryption keys, each wrapped in a NaCl `crypto_box_seal`
(libsodium *sealed box*) against the backup's 32-byte X25519 public key. The
column names contain `rsa_` for historical reasons, but the contents are sealed
boxes, **not** RSA.

```sql
CREATE TABLE vkey (
    version_id INTEGER PRIMARY KEY,
    rsa_vkey BLOB NOT NULL,        -- sealed_box(file_key)  (80 bytes)
    rsa_vkey_iv BLOB,              -- sealed_box(file_iv)   (64 bytes)
    checksum BLOB,                 -- MD5 integrity check (16 bytes)
    ref_count INTEGER NOT NULL DEFAULT 0
);
```

Example row:

| Field | Size | Description |
|-------|------|-------------|
| `version_id` | — | `1` |
| `rsa_vkey` | 80 bytes | `crypto_box_seal(file_key)`; the 32-byte AES-256 key sealed = 32 (ephemeral pubkey) + 32 (ciphertext) + 16 (Poly1305 MAC) |
| `rsa_vkey_iv` | 64 bytes | `crypto_box_seal(file_iv)`; the 16-byte AES IV sealed = 32 (ephemeral pubkey) + 16 (ciphertext) + 16 (Poly1305 MAC) |
| `checksum` | 16 bytes | `MD5(rsa_vkey + fileKeySalt + rsa_vkey_iv)` |
| `ref_count` | — | `3` (number of buckets using this version key) |

To recover the version key, open each sealed box with the X25519 key pair:

```
file_key = crypto_box_seal_open(rsa_vkey,    x25519_pub, x25519_priv)  -> 32 bytes
file_iv  = crypto_box_seal_open(rsa_vkey_iv, x25519_pub, x25519_priv)  -> 16 bytes
```

A wrong key fails the Poly1305 MAC, so an incorrect key is detected immediately.

### Encryption Key Hierarchy

The key derivation follows a multi-layer hierarchy. Hardcoded salt strings
are embedded in the HyperBackup binary:

```
passwordSalt  = "5mNgudh053SUoMrZxoKG8GUWyj6kEtGO"
unikeySalt1   = "CIpfMargmxetgFtkBmG3KqEiQ6qfqZgF"
unikeySalt2   = "kkE7sRZRvnbVlJFofhD7WCXumXBGyzki"
fileKeySalt   = "8Llx6OSaDPzbwCkjG8eYc64GZGMIlMXm"
```

#### Layer 1: Password Key

```
password_key = SHA256(passwordSalt + password)     → 32 bytes (AES-256 key)
```

Used **only in local backups** to decrypt the stored X25519 private key. It is
useless on its own for cloud backups (see Layer 2).

#### Layer 2: X25519 Key Pair

An X25519 (Curve25519) key pair is generated when the encrypted backup task is
created. The **public key** (`Config/public.pem.1`, 32 raw bytes) seals each
per-version key with NaCl `crypto_box_seal` during backup. The matching 32-byte
**private key** is needed for decryption.

In local HyperBackup format, the 32-byte X25519 private key is stored in
`encKeys.1`, AES-256-CBC-encrypted with the password key:

```
x25519_private_key = AES-256-CBC-decrypt(
    key  = password_key,
    iv   = MD5(unikey + unikeySalt1),
    data = encKeys.1 payload
)
```

**In cloud format, `encKeys.1` contains only the 16-byte header.** The X25519
private key must be exported from the NAS separately (the
`Microsoft Azure 1_private.pem` file — 32 raw bytes despite the name). Without
it, cloud-only recovery is impossible -- the password alone is not sufficient.

#### Layer 3: Version Keys (Per-Version AES Key + IV)

Each backup version has its own AES key and IV, stored as NaCl sealed boxes in
`Pool/vkey.db.2`. Open them with the X25519 key pair:

```
file_key = crypto_box_seal_open(rsa_vkey,    x25519_pub, x25519_priv)  → 32 bytes
file_iv  = crypto_box_seal_open(rsa_vkey_iv, x25519_pub, x25519_priv)  → 16 bytes
```

The checksum is verified:
```
expected = MD5(rsa_vkey + fileKeySalt + rsa_vkey_iv)
assert expected == checksum column
```

These `file_key` / `file_iv` values are the actual AES-256-CBC parameters
used to encrypt/decrypt chunk data in buckets. (A wrong X25519 key already
fails the sealed box's Poly1305 MAC before the checksum is even reached.)

#### Layer 4: Filename Encryption Key

Filenames use a separate key derivation:

```
filename_key = SHA256(x25519_private_key_bytes + unikey)    → 32 bytes
filename_iv  = MD5(unikey + unikeySalt2)                     → 16 bytes
```

Where `x25519_private_key_bytes` is the same 32-byte X25519 private key from
Layer 2, and `unikey` is from `_Syno_TaskConfig`
(e.g., `001132A9310A_10_1774900146`).

### Filename Encryption

Filenames in `version_list.file_name` are encrypted with AES-CBC using
PKCS7 padding, then base64-encoded with modified alphabet (`+` and `_`
as altchars instead of `+` and `/`).

Properties:
- **Deterministic**: The same plaintext filename always produces the same
  ciphertext (fixed key + IV).
- **Base64 output** uses `+_` altchars (URL-safe variant with `_` instead of `/`).
- To decrypt: base64-decode with `+_` altchars → AES-256-CBC decrypt with
  `filename_key` / `filename_iv` → strip PKCS7 padding.

Example encrypted filenames from `Video/1.db.2`:
```
pdN4e+fafyDyFu9ocjY3Iw==                          → test.txt (5 bytes)
reDhJCAE7C8VrEh9w_VVWQ==                          → test2.txt (5 bytes)
xQyOoSS8MPuKNHDR4UQHm9X1rnG1Xka7nJOc5bOOE3c=      → hyperbackup-test (dir)
gLJY6XDel8_shwgeDmOdpg==                          → dir
eb2wRVZjvLKpaK9k7BDB6w==                          → @eaDir
```

### Config Value Encryption

Certain config values are wrapped in an `/*eNC00*...*/ ` envelope:

```
/*eNC00*<base64-ciphertext>*/
```

This appears in:
- `backup_folders` and `backup_filter` in `_Syno_TaskConfig`
- `source` field in `version_info.db.2`

The ciphertext length is approximately `plaintext_length + 1`, suggesting a
stream cipher mode or a different encryption scheme than the CBC used for
filenames and chunk data. The exact algorithm for this envelope has not been
fully determined.

Example:
```ini
enc_backup_folders="/*eNC00*Urc2c9+qLVLe45nP4P6pqPjgVZAwp8QpLmQMmg==*/"
```

### Compression (LZ4)

When `data_compress_type=1`, chunks are compressed with **LZ4 block format**
(no frame header) before encryption.

The processing pipeline for each chunk:
```
original data
  → LZ4 block compress
  → PKCS7 pad to AES block size (16 bytes)
  → AES-256-CBC encrypt with file_key / file_iv
  → store in bucket + append 4-byte integrity tag
```

To decompress after decryption, the LZ4 block data requires a prepended
little-endian uint32 size header (the original uncompressed size) before
calling the standard LZ4 block decompressor.

In the bucket index, sizes are tracked as:
- `chunk_size` = encrypted size in the bucket (excludes the 4-byte trailing tag)
- `dedup_size` = original uncompressed size (before compression and encryption)

The MD5 hash in the index is computed on the **original plaintext data**
(before compression, before encryption), preserving deduplication semantics.

### Encrypted Bucket Index Differences

The Type 2 binary index format changes slightly when encryption is enabled:

| Offset | Unencrypted | Encrypted | Meaning |
|--------|-------------|-----------|---------|
| `0x1C` (flags) | `0x0800` | `0x0A00` | Bit 1 set = compression+encryption active |
| `0x2A` | `0x00` | `0x01` | Encryption flag byte |

Each encrypted chunk in a `.bucket.2` file has a **4-byte trailing integrity
tag** appended after the AES ciphertext. This tag is NOT included in the
`chunk_size` field of the index entry, so the actual bytes consumed in the
bucket per chunk = `chunk_size + 4`.

Example from encrypted bucket 1 (contains "hello" and "world"):
```
Index entry 0: chunk_size=16  bucket_offset=0   dedup_size=5
               → 16 bytes AES ciphertext + 4 bytes tag = 20 bytes
Index entry 1: chunk_size=16  bucket_offset=20  dedup_size=5
               → 16 bytes AES ciphertext + 4 bytes tag = 20 bytes
Total bucket size: 40 bytes (vs. 10 bytes unencrypted)
```

The 5-byte strings "hello" and "world" are LZ4-compressed (negligible
reduction), padded to 16 bytes (AES block size), then encrypted, yielding
16 bytes of ciphertext plus 4 bytes of integrity tag each.

### `version_info` Encryption Checksum

When encryption is enabled, `version_info.enc_cksum` contains a 32-byte
SHA-256 checksum (vs. NULL when unencrypted). This is used to verify the
decryption key is correct before attempting full data recovery.

### Cloud Format Limitation

**Critical**: Full decryption of a cloud-format encrypted backup requires the
**32-byte X25519 private key** exported from the NAS (the
`Microsoft Azure 1_private.pem` file — raw bytes, not PEM text). This single key
is sufficient: it opens the per-version sealed boxes and derives the filename
key.

The **password alone is NOT sufficient** for cloud-only recovery. In a *local*
backup the password decrypts `encKeys.1` to recover the X25519 private key, but
the cloud `encKeys.1` file contains only a 16-byte header with no key material.
Users must separately export and preserve the encryption key from the NAS
management interface.

---

## Data Recovery Walkthrough

To restore files from a HyperBackup cloud image without Synology software:

### Step 1: Detect Encryption

Check `synobkpinfo.db`:
```sql
SELECT info_value FROM backup_info_tb WHERE info_name = 'dataEnc';
-- 'T' = encrypted, 'F' = unencrypted
SELECT info_value FROM backup_info_tb WHERE info_name = 'dataComp';
-- 'T' = compressed, 'F' = uncompressed
```

If encrypted, you need the **32-byte X25519 private key** exported from the NAS
(`Microsoft Azure 1_private.pem`). For a *local* backup you can instead supply
the **password**, which decrypts that same key out of `encKeys.1`; for a *cloud*
backup the password is not enough and the exported key is required.

### Step 2: Derive Decryption Keys (Encrypted Only)

```python
import hashlib
from Crypto.Cipher import AES               # PyCryptodome (AES only)
from nacl.public import PrivateKey, SealedBox

password_salt = b"5mNgudh053SUoMrZxoKG8GUWyj6kEtGO"
unikey_salt1  = b"CIpfMargmxetgFtkBmG3KqEiQ6qfqZgF"
unikey_salt2  = b"kkE7sRZRvnbVlJFofhD7WCXumXBGyzki"
file_key_salt = b"8Llx6OSaDPzbwCkjG8eYc64GZGMIlMXm"

unikey = b"..."  # from _Syno_TaskConfig

# Obtain the 32-byte X25519 PRIVATE key:
#   cloud backup  -> read it straight from the exported "..._private.pem"
#                    (it is 32 raw bytes, NOT PEM text)
# x25519_private_bytes = open("Microsoft Azure 1_private.pem", "rb").read()
#
#   local backup  -> decrypt it out of encKeys.1 with the password key
# password_key = hashlib.sha256(password_salt + password.encode()).digest()
# kdf_iv = hashlib.md5(unikey + unikey_salt1).digest()
# x25519_private_bytes = AES.new(password_key, AES.MODE_CBC, kdf_iv) \
#                           .decrypt(enckeys_payload)   # then strip PKCS7
x25519_private_bytes = b"..."  # 32 bytes, from one of the two paths above

# Open the per-version sealed boxes (NaCl crypto_box_seal). A wrong key raises.
box      = SealedBox(PrivateKey(x25519_private_bytes))
file_key = box.decrypt(rsa_vkey)      # 32 bytes — AES-256 key
file_iv  = box.decrypt(rsa_vkey_iv)   # 16 bytes — AES IV

# Derive filename decryption key (note: the X25519 private key bytes, NOT RSA)
filename_key = hashlib.sha256(x25519_private_bytes + unikey).digest()
filename_iv  = hashlib.md5(unikey + unikey_salt2).digest()
```

### Step 3: Identify Versions

Query `Config/version_info.db.2`:
```sql
SELECT id, name, timestamp, status, share FROM version_info;
```

### Step 4: List Files for a Version

For each share, query `Config/@Share/{ShareName}/{version_id}.db.2`:
```sql
SELECT file_name, size, mode, off_virtual_file, tag,
       mtime_sec, name_id_v2, pname_id_v2
FROM version_list
WHERE version_id = 1;
```

Rebuild the directory tree using `name_id_v2` / `pname_id_v2` parent
references. Root sentinel: `5058F1AF5058F1AF8388633F609CADB75A75DC9D`.

If encrypted, decrypt each `file_name`:
```python
import base64
ciphertext = base64.b64decode(file_name, altchars=b'+_')
cipher = AES.new(filename_key, AES.MODE_CBC, filename_iv)
plaintext = cipher.decrypt(ciphertext)
# Strip PKCS7 padding
pad_len = plaintext[-1]
plaintext = plaintext[:-pad_len].decode('utf-8')
```

### Step 5: Recover a File's Content (tag != NULL)

A file's content is a contiguous run of chunks (see [Chunk Manifest](#chunk-manifest));
the `tag` (bytes 0-15) verifies it and the size (bytes 16-19) bounds it:

```
ordered = all bucket-index entries, sorted by (bucket_id, bucket_offset)
          # each entry: content_md5, dedup_size, chunk_size, bucket_offset
find the contiguous run [i..j) where:
    sum(ordered[i..j].dedup_size) == file.size
    AND MD5( concat(ordered[i..j].content_md5) ) == file.tag[0:16]
    # single-chunk reduces to MD5(MD5(content)); very large files use a higher-tier
    # tag, so fall back to the unique run whose dedup_sizes sum to file.size
content = concatenate, for each entry in the run:
    plaintext:  read chunk_size bytes at bucket_offset from {bucket}.bucket
    encrypted:  read chunk_size bytes (a 4-byte integrity tag follows, excluded);
                AES-256-CBC decrypt with file_key/file_iv; strip PKCS7; if the
                result length != dedup_size, LZ4-block-decompress it to dedup_size
                (small/incompressible chunks are stored uncompressed)
```

Verify each chunk's `MD5(plaintext) == content_md5` and the total length ==
`file.size`. The `file_key`/`file_iv` are the version key of the **bucket** holding
each chunk (bucket generation `.N` ⇒ version `N-1`); a deduplicated chunk may use a
different version's key, confirmed by the per-chunk MD5.

### Step 6: Recover Large Files (off_virtual_file == -1, tag == NULL)

Large files live in the file pool: `Pool/file_pool/{id}.file.2`. Skip the
0x14C-byte header (the XZ magic `FD 37 7A 58 5A 00` begins there) and decompress
with XZ/LZMA; the result is a GNU `tar` archive holding the file.

> **Caveats (observed in the sample):** the file→`id` mapping is not stored in any
> `version_list` field for large files (`tag` and `disk_entry_tag` are both NULL),
> and `file_pool_map.checksum` does **not** equal `MD5(content)` or
> `MD5(MD5(content))` of the decompressed output. In the included sample,
> `config.dss`'s XZ payload is truncated/corrupt (`xz` reports *"Compressed data is
> corrupt"*), so it does not fully restore. The file-pool internal segment format
> (header fields at `0x100`–`0x14C`) is only partially reverse-engineered.

### Step 7: Skip Metadata Entries

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

#### `Pool/vkey.db.2` (Encrypted Repositories Only)
```sql
CREATE TABLE vkey (
  version_id INTEGER PRIMARY KEY,
  rsa_vkey BLOB NOT NULL,          -- sealed_box(file_key), 80 bytes (NaCl, not RSA)
  rsa_vkey_iv BLOB,                -- sealed_box(file_iv),  64 bytes (NaCl, not RSA)
  checksum BLOB,                   -- MD5 integrity check (16 bytes)
  ref_count INTEGER NOT NULL DEFAULT 0
);
```

---

## License

This repository is licensed under the Apache License 2.0. See [LICENSE](LICENSE).

The license applies only to the original documentation, analysis, and code in
this repository. It does not grant any rights to Synology software, trademarks,
backup data, encryption keys, or other third-party materials.
