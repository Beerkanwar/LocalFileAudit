# LocalFileAudit — Architecture Guide 🏗️

This document describes the architectural design, core subsystems, data pipelines, concurrency models, and security boundaries implemented in **LocalFileAudit**.

---

## 1. Architectural Philosophy

LocalFileAudit is structured around four non-negotiable principles:

```
[ Filesystem ]
      │ (Read-Only Traversal)
      ▼
[ Scanner Engine ] ──► [ SQLite Persistence (WAL Mode) ]
                              │
                              ▼
                 [ Deterministic Analysis Engine ]
                              │
                 ┌────────────┴────────────┐
                 ▼                         ▼
      [ Deterministic Reports ]    [ Candidate Minimizer ]
         (CSV / MD / HTML)                 │ (Loopback Only)
                                           ▼
                                  [ Local Ollama AI ]
                                           │
                                           ▼
                                    [ AI Review Report ]
```

1. **Facts First:** The scanner observes and records immutable filesystem facts (paths, byte sizes, timestamps, attributes).
2. **Deterministic Rules:** Aggregations, rankings, and classification categories are computed mathematically by deterministic rules, without AI involvement.
3. **AI Explains, Never Decides:** AI provides optional natural-language summaries and context. It is strictly advisory and cannot trigger filesystem actions.
4. **Permanent Read-Only Invariant:** No component in the solution contains logic to delete, mutate, rename, or move user source files.

---

## 2. Project Structure & Dependency Graph

```
LocalFileAudit.slnx
├── LocalFileAudit.Core              (Domain models, contracts, rule evaluator, validation)
├── LocalFileAudit.Infrastructure    (Scanning channel, SQLite persistence, reporting, Ollama)
├── LocalFileAudit.App               (WPF desktop shell, MVVM view models, views, styles)
├── LocalFileAudit.CrashWorker       (Child worker executable for process-kill crash tests)
└── LocalFileAudit.Tests             (Automated test suite, hardening & acceptance tests)
```

### Dependency Rules:
- **`LocalFileAudit.Core`** has **zero** dependencies on Infrastructure, UI, or external databases. It defines pure interfaces (`IScanEngine`, `IScanRepository`, `IDuplicateAnalysisEngine`, `IAiAnalysisService`) and pure data models.
- **`LocalFileAudit.Infrastructure`** implements the Core interfaces using `Microsoft.Data.Sqlite`, `System.Threading.Channels`, and `HttpClient`.
- **`LocalFileAudit.App`** depends on `Core` and `Infrastructure` and wires them via Microsoft Dependency Injection (`ServiceCollectionExtensions.cs`).
- **`LocalFileAudit.Tests`** tests `Core` and `Infrastructure` cross-platform on `net9.0`.

---

## 3. Core Subsystems

### 3.1 Filesystem Scanner Engine (`FilesystemScanner`)
- **Pipeline:** Implemented using `System.Threading.Channels.Channel<DirectoryRecord>` with bounded capacity (`ChannelOptions.BoundedCapacity = 500`).
- **Producer-Consumer:** Traversal enumerates directory trees asynchronously without building recursive call stacks, pushing items to bounded channels with backpressure.
- **Reparse Points:** Bypasses symbolic links and NTFS junction points when `FollowReparsePoints = false` to avoid infinite cyclic loops and external filesystem leaks.
- **Cooperative Control:** Supports non-blocking `Pause()`, `Resume()`, and `CancellationToken` cancellation.
- **Low-Impact Mode:** When enabled, introduces micro-delays between directory traversals to preserve system responsiveness during heavy desktop workloads.

### 3.2 Crash-Safe Persistence (`SqliteScanRepository`)
- **Engine:** SQLite 3 via `Microsoft.Data.Sqlite`.
- **Journal Mode:** Write-Ahead Logging (`PRAGMA journal_mode = 'wal'`). WAL mode guarantees that committed transactions survive power outages or process terminations, while readers do not block writers.
- **Batching:** Files and directory records are inserted in bounded transactions (`BatchSize = 1000`) protected by a `SemaphoreSlim` write lock.
- **Resumption Model:** Directory traversal records `Pending`, `InProgress`, and `Completed` states. If a scan is interrupted, `GetPendingOrInterruptedDirectoriesAsync` allows resumption without duplicate inserts.
- **Online Backup:** Implements `VACUUM INTO` for online, non-blocking snapshot backups of the database while in use.

### 3.3 Two-Stage Duplicate Detection (`DuplicateAnalysisEngine`)
To maximize speed on large drives with millions of files:
- **Stage 1 (Size Clustering):** Groups files by exact logical byte size in SQLite (`HAVING COUNT(1) >= 2`). Files with unique sizes are filtered out immediately without disk I/O.
- **Stage 2 (SHA-256 Verification):** Files within size clusters are asynchronously hashed using cryptographic SHA-256 streams (`FileAccess.Read`, `FileShare.Read`).
- **Concurrence Verification:** File modification timestamps are verified before and after hashing. If a file changed during computation, the hash is discarded to prevent false duplicate pairings.

### 3.4 Deterministic Storage Analysis (`StorageAnalysisEngine`)
- Computes aggregations via indexed SQLite queries:
  - Total logical size and file counts.
  - Extension distribution (count, size, percentage of total).
  - Storage hotspots by parent directory hierarchy.
  - Top 100 largest files.
  - Explainable review candidate classifications (`RULE_LARGE_FILE`, `RULE_OLD_MEDIA`, `RULE_SYSTEM_LOG`, `RULE_TEMP_CACHE`, `RULE_PROTECTED_SYSTEM`).

### 3.5 Atomic Report Generation (`ReportGenerator`)
- **Staging Folder Isolation:** Reports are generated into a temporary folder (`.staging_<guid>`).
- **Multi-Format Export:** Produces `Summary.md`, `Summary.html`, `ExtensionSummary.csv`, `DirectorySummary.csv`, `ReviewCandidates.csv`, `ScanErrors.csv`, `Inventory.csv`, and `Manifest.json`.
- **Atomic Promotion:** Files are moved to the target directory only after all artifacts and `Manifest.json` are verified. If interrupted, the staging directory is cleaned up and existing reports are never overwritten with partial content.
- **HTML Security:** All dynamic values in `Summary.html` are strictly escaped via `WebUtility.HtmlEncode`.

### 3.6 Local AI Service (`OllamaClient` & `AiAnalysisService`)
- **Network Isolation:** Validates `uri.IsLoopback` and rejects any remote hostname or non-loopback IP (`http://localhost` and `http://127.0.0.1` only).
- **Prompt Injection Defense:** File paths and names from user disks are framed as untrusted input data blocks. AI instructions state that metadata cannot execute commands.
- **Candidate Reconciliation:** AI recommendations are matched against real database candidate IDs. Any recommendation referencing a non-existent candidate ID is rejected as a hallucination.

---

## 4. Database Schema

The SQLite inventory database contains 5 core tables:

1. **`ScanRuns`**: Tracks scan execution metadata, elapsed times, drive filters, file count, error count, and scan status (`Pending`, `InProgress`, `Paused`, `Completed`, `Cancelled`, `Failed`).
2. **`Directories`**: Tracks directory paths, parent associations, traversal status, and error messages for crash-safe resumption.
3. **`Files`**: Records file paths, file names, extensions, byte sizes, creation/modification/access UTC timestamps, NTFS file attributes, and SHA-256 hashes. Indexed on `(ScanRunId, SizeBytes)`, `(ScanRunId, Extension)`, and `(ScanRunId, FullPath)`.
4. **`ScanErrors`**: Captures file and directory access errors (`UnauthorizedAccessException`, `IOException`) with path and timestamps.
5. **`AiReports`**: Stores historical narrative AI reviews and prompt summaries.

---

## 5. Security & Safety Invariants

1. **`FileAccess.Read` Only:** The application never opens user files with write or delete permissions.
2. **`asInvoker` Manifest:** `app.manifest` explicitly declares `<requestedExecutionLevel level="asInvoker" />`. Administrator privileges are never sought or required.
3. **Data Minimization:** AI prompts send only file name, extension, size in MB, and parent folder name. Full file contents are never read or sent.
4. **Deterministic Resilience:** Offline and deterministic operations remain 100% operational even if Ollama is unreachable, times out, or fails.
