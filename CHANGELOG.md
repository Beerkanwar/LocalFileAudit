# Changelog

All notable changes to the LocalFileAudit project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.3.13] - 2026-10-24

### Comprehensive Security, Architecture & Reliability Remediation
- **Scan Resumption Target Integrity (H1):**
  - Rebuilt `ScanTarget` in `FilesystemScanner.ResumeScanAsync` from the saved `ConfigurationSnapshot`, restoring `ExcludedPaths`, `RecordCloudPlaceholders`, `FollowReparsePoints`, and `LowImpactMode` to preserve scan parameters across crashes.
- **Archive Verification Hashing & Timestamp Verification (H2):**
  - Defaulted SHA-256 calculation to ON in `ArchiveVerificationViewModel`.
  - Accurately labeled unhashed matches as `"Size match (unhashed - SHA-256 check was not performed)"`.
  - Added file timestamp comparison and distinguished SHA-256 verified matches from unhashed size matches in completion toasts.
- **Shared Path Canonicalization & Overlap Defense (H3):**
  - Introduced `PathOverlapValidator` using `Path.GetFullPath` to detect overlapping, nested, identical, and aliased directory boundaries across both `ArchivePlannerService` and `ArchiveVerificationService`.
- **Archive Verification Audit Guard (H4):**
  - Added confirmation dialog requiring independent SHA-256 verification before setting `IsVerified` in `ArchivePlanViewModel`.
- **Archive Copy Script Hardening (H5):**
  - Added `/XO` (exclude older files) and `/DCOPY:DAT` (directory timestamps) flags to robocopy invocations in generated `copy-archive.ps1`.
  - Added pre-checks verifying destination folder availability before copy execution.
- **Accurate Persistence Fault Classification (H6):**
  - Captured persistence consumer exceptions in `FilesystemScanner.ExecuteScanLoopAsync`, properly marking the scan as `ScanState.Failed` with detailed error logs instead of masking disk/database errors as user cancellations.
- **Durable SQLite Counter Reconciliation (H7):**
  - Reconciled file and completed directory counts directly from SQLite upon completion, cancellation, and checkpoints to maintain absolute consistency with the `Files` table.
- **Scan Root Normalization & Nested Root Pruning (H8):**
  - Canonicalized scan roots with `Path.GetFullPath`, pruned child roots nested inside parent roots (e.g. dropping `C:\Users` when `C:\` is scanned), and verified target directory existence prior to scan launch.
- **Ollama Payload Bounding & Fallback Resilience (H9):**
  - Bounded inference candidate payloads to prevent exceeding model context windows (`num_ctx`).
  - Added automatic compact candidate retries on truncation or JSON parse errors and pinned system safety prompts.
- **AI Markdown Injection Sanitization (M1):**
  - Sanitized executive summaries, hotspot assessments, review plans, and safety warnings in `AiAnalysisService.SaveAiReportAsync` to neutralize markdown link and image exfiltration risks.
  - Hardened `ReportGenerator.EscapeMarkdown` against backticks, brackets, backslashes, and newlines.
- **Comprehensive Path & Directory Redaction (M2):**
  - Added `PathRedactor.RedactDirectory` to redact personal intermediate directory structures in AI prompts, chat context, and largest directory summaries.
- **Service-Level AI Enforcement (M3):**
  - Enforced `config.AI.Enabled` directly in `OllamaClient`, `AiAnalysisService`, and `AiReportsViewModel`.
- **Synchronized Privacy Redaction Toggles (M4):**
  - Synchronized `RedactFilePaths` across both `Logging.RedactFilePaths` and `AI.RedactFilePaths` in `SettingsViewModel`.
- **Database Migrations & WAL Truncation (M5):**
  - Implemented versioned SQLite migration (`user_version = 2`) and added `CheckpointTruncateAsync()` for periodic `wal_checkpoint(TRUNCATE)` maintenance.
- **Query & Sorting Indexes (M6):**
  - Created composite SQLite indexes on `FileName`, `LastWriteTimeUtc`, and `FullPath` for responsive inventory sorting.
- **Keyset Pagination for Full Exports (M7):**
  - Rewrote `ExportFullInventoryCsvAsync` with `AfterId` keyset pagination and honored `SearchText`, `MinModifiedUtc`, and `MaxModifiedUtc` filters.
- **Candidate Rule Alignment & Pre-Filtering (M8):**
  - Aligned analysis classification rules with `CandidateRuleEvaluator.AllCandidateExtensions`, pre-filtered protected items, and added truncation transparency flags.
- **Awaitable Cache Invalidation & Bounded Memory (M9):**
  - Implemented awaitable `InvalidateCacheAsync` and bounded in-memory cache to 10 entries.
- **Duplicate Hashing Throttling & Scaling (M10, M12):**
  - Capped concurrent duplicate hashers to prevent spinning disk thrash, tightened duplicate timestamp tolerance to 1 second, and used `FileShare.ReadWrite`.
- **Reparse Cycle Prevention (M11):**
  - Added visited inode/path tracking to prevent infinite recursive loops when `FollowReparsePoints` is true.
- **Comparison Root Mismatch Diagnostics (M13):**
  - Added baseline and comparison root mismatch warning banners and case-insensitive comparison joins.
- **Non-Dropping Inventory Search & Keyset Virtualization (M14):**
  - Cancelled in-flight queries rather than dropping keystrokes, and evaluated age filter cutoffs dynamically at query execution time.
- **Eliminated Double Initialization & Debounced Search (M15):**
  - Eliminated redundant viewmodel loads and debounced inventory/compare filters with 200ms cancellation tokens.
- **Scanning Guards for Scan Deletion (M16):**
  - Added `IsScanning` guard and confirmation prompts before deleting scan runs.
- **Configuration Persistence & Per-Field Repair (M17, M18, M19, M20):**
  - Preserved `RecordCloudPlaceholders` on save, repaired invalid configuration fields per-field rather than reverting all settings, used `AppEnvironment.BackupsDirectory`, and pruned backups keeping 5 recent.
- **Model Selection Retention (M21):**
  - Preserved configured `ModelName` during connection tests in `AiReportsViewModel`.
- **Dependency & Supply Chain Cleanup (M22, L2):**
  - Completely removed heavy `System.Management.Automation` and vulnerable `System.Security.Cryptography.Pkcs` test packages.
  - Hardened vulnerability audit step in CI to fail immediately if feeds are unreachable or errors occur.
  - Bounded log files to 10 MB with rotation, stripped log injection newlines, and removed dead console logging.
- **Dispatcher Exception Sanitization (L1):**
  - Sanitized error messages in WPF unhandled exception dialogs using `PathRedactor.Redact`.
- **Startup Integrity & Documentation Alignment (L12, L13):**
  - Added `PRAGMA quick_check` verification on startup, added maintainer security contact email to `SECURITY.md`, and synchronized documentation drift across `README.md`, `ARCHITECTURE.md`, `USER_GUIDE.md`, and `PRIVACY.md`.

---

## [1.3.12] - 2026-10-24

### Security & Architecture Audit Remediation
- **AI Payload File Name Redaction (SEC-01 / High):**
  - Updated `AiAnalysisService` candidate payload and chat context to call `PathRedactor.RedactFileName(fileName, candidateId)`.
  - When `RedactFilePaths` is enabled, sensitive filenames (e.g., `Taxes_2025_JohnDoe.pdf`) are sanitized into `candidate_{id}.ext`, preventing privacy leaks to Ollama and database records.
- **Comparison CSV Formula Injection Defense (SEC-02 / Medium):**
  - Replaced manual quote formatting in `SqliteScanRepository.ExportComparisonCsvAsync` with `CsvSanitizer.EscapeCsv` across `DeltaType`, `FullPath`, and `Extension`.
  - Prevents spreadsheet formula execution (`=`, `+`, `-`, `@`, `\t`, `\r`) when opening comparison exports in Excel or Calc.
- **Root Resolution & Count Parsing (BUG-01 / Medium):**
  - Replaced legacy comma/semicolon splitting with `SelectedRootsHelper.ParseRoots` in `StorageAnalysisEngine.GetLargestDirectoriesAsync` and `DashboardViewModel`.
  - Scanned paths containing commas are no longer corrupted during directory aggregation, and dashboard accurately displays root count.
- **Archive Verification Navigation Refresh (BUG-02 / Medium):**
  - Added `case "ArchiveVerification":` to `MainViewModel.Navigate` to proactively refresh verification view state upon tab switching.
- **Non-Blocking File Allocation Queries (PERF-01 / Medium):**
  - Offloaded `FileAllocationHelper.GetAllocatedSizeBytes` in `InventoryViewModel.OnSelectedFileChanged` to a background task with race protection.
  - Eliminates UI dispatcher freezing and stutter when browsing inventory files on network shares or sleeping drives.
- **Scanner Directory Exclusions (ARCH-01 / Low):**
  - Added `ExcludedPaths` to `ScanningConfiguration` and exposed semicolon-separated folder exclusions in `SettingsView`.
  - Passed exclusions to `ScanTarget` in `ScansViewModel` so massive trees (e.g. `node_modules`, `.git`) can be skipped.
- **Application Lifecycle & Dispatcher Hardening (ARCH-02 & ARCH-03 / Low):**
  - Made `App.OnExit` synchronous with bounded wait timeout, guaranteeing host stop and mutex release finish before process exit.
  - Hardened `DispatcherUnhandledException` to let fatal CLR corruption exceptions (`OutOfMemoryException`, `StackOverflowException`, `AccessViolationException`) fail fast rather than leaving the app in a zombie state.
- **Result Truncation Transparency (UX-01 / Low):**
  - Added `ResultsCountText` and `IsTruncated` alerts in `CompareRunsViewModel` and `ArchiveVerificationViewModel`.
  - Users are clearly notified whenever matched records exceed the 1,000-item grid limit with guidance to export to CSV for full datasets.
- **Logging Level Persistence & Config Cleanliness (CONF-01 & CONF-02 / Low):**
  - Added configurable `LogLevel` selector in `SettingsViewModel` and `SettingsView`, preserving user diagnostics preferences across saves.
  - Marked obsolete dead `SendDocumentContents` property in `AppConfiguration`.
- **CI/CD Supply Chain Hardening & Multi-Architecture Release (CI-01 / Low):**
  - Pinned all GitHub Actions (`checkout`, `setup-dotnet`, `upload-artifact`, `action-gh-release`) to immutable commit SHAs.
  - Removed unused `System.Security.Cryptography.Pkcs` test package reference.
  - Expanded release pipeline to build and publish self-contained single-file binaries for both Windows x64 (`win-x64`) and Windows ARM64 (`win-arm64`).

---

## [1.3.11] - 2026-10-23

### Security & Hardening Remediation
- **PowerShell Script Injection Hardening (High 1):**
  - Updated `ArchivePlannerService.EscapePowerShellSingleQuotedLiteral` to escape ASCII single quotes `'` and all PowerShell Unicode single-quote delimiters: left single quotation mark `‘` (U+2018), right single quotation mark `’` (U+2019), single low-9 quotation mark `‚` (U+201A), and single high-reversed-9 quotation mark `‛` (U+201B).
  - Hardened `SanitizeIdentifier` to strip shell metacharacters (`;`, `$`, `` ` ``, quotes).
  - Resolved robocopy trailing backslash issue: paths ending in `\` (such as `D:\`) are mapped to `$safeSrc`/`$safeDst` (`D:\.`) to prevent escaping robocopy parameter quotes.
  - Added `ValidatePathsDoNotOverlap` enforcing that destination cannot be nested inside source or identical to source.
  - Backs up corrupted `archive-plans.json` to `.corrupted.<timestamp>.bak` before resetting state.
- **Archive Verification Safety (High 2 & Medium 8):**
  - Updated `ArchiveVerificationService`: hash calculation failures return `null` instead of error strings.
  - Added `VerificationStatus.HashError`: if either source or destination hashing fails, status is `HashError` and is strictly never counted as a match.
  - Added `IsPartial` flag and `EnumerationErrors` collection to `VerificationSummary` so unreadable directories are prominently surfaced in UI and tagged with `# WARNING` in CSV exports.
  - Aligned file streaming in verifier to `FileAccess.Read, FileShare.Read` matching SECURITY.md.
- **Directory Checkpoints & Durability (High 3, Medium 4 & Medium 5):**
  - Fixed directory ID persistence in `SqliteScanRepository.InsertDirectoryBatchAsync` using `ON CONFLICT(ScanRunId, Path) DO UPDATE ... RETURNING Id`, ensuring generated IDs are written back to memory records.
  - Added unique index `idx_dirs_scan_path_unique` on `Directories(ScanRunId, Path)` to guarantee checkpoint idempotency on scan resumption.
  - In `FilesystemScanner`: directory checkpoints are sequenced in FIFO order with file persistence items, flushing buffered files before marking a directory `Completed` or `Error`.
  - Prevented scanner deadlocks: bounded channel cancellation triggers immediately if consumer faults, ensuring background worker threads terminate and final scan status is cleanly saved as `Failed` or `Cancelled`.
- **Cloud Hydration Prevention & Duplicate Optimization (Medium 6 & Medium 7):**
  - Filtered Stage 1 duplicate candidate queries to `MetadataStatus = 'Ok'` and non-reparse points `(Attributes & 1024) = 0`.
  - Filtered Stage 2 hashing to skip reparse points and OneDrive placeholders without hydrating offline files.
  - Added database index on `Files(FullPath, SizeBytes)` for fast lookups.
  - Parallelized Stage 2 SHA-256 hashing using bounded concurrency (`Math.Clamp(ProcessorCount, 2, 8)`).
- **AI Data Minimization & Output Validation (Medium 9 & Medium 10):**
  - Redacted `SelectedDrives` roots and masked personal user profile folders in `PathRedactor` across drive letters, forward/backward slashes, and UNC paths.
  - Redacted candidate context filenames and scanned roots in chat when `RedactFilePaths` is enabled.
  - Added category validation in `AiAnalysisService.ValidateAndParseResponse` against `CandidateCategory` enum, defaulting unknown/hallucinated categories to `Investigate`.
  - Escaped all table cells with `EscapeMarkdownTableCell` in Markdown reports and exported tables to prevent layout breakage and link/image injection.
- **Configuration Safety & Recovery (Medium 11):**
  - `JsonConfigurationService`: Automatically backs up corrupted or unparseable `config.json` files to `.corrupted.<timestamp>.bak` before falling back to defaults.
  - `ConfigurationValidator`: Enforces absolute URI and `http`/`https` scheme even when `RequireLocalEndpoint = false`.
- **Scan Roots Delimiter & Anchored Path Rules (Medium 13 & Medium 14):**
  - Created `SelectedRootsHelper` using pipe `|` delimiter for scan roots, supporting paths with semicolons and commas without corrupted roll-ups.
  - Anchored `IsProtectedPath` to root drive (`\Windows\`, `\Program Files\`, etc.) and downloads check to user profile `\Users\<user>\Downloads\`, preventing false positives on dev trees like `D:\Projects\Windows\...`.
- **Formula Injection Defense & Ollama Bounding (Low Items):**
  - Consolidated CSV escaping across the application into `CsvSanitizer.EscapeCsv`, prefixing formula characters (`=`, `+`, `-`, `@`, `\t`, `\r`) with single quote `'`.
  - Set `AllowAutoRedirect = false` on Ollama HTTP client.
  - Used `ResponseHeadersRead` with bounded streaming (64KB error, 5MB response) and wrapped full requests in timeout exception handling.
  - Wired `FileLoggerProvider.ApplyConfiguration` with `RedactFilePaths`, `RetainDays`, and `LogLevel`.

## [1.3.10] - 2026-10-21

### Fixed
- **Thinking Model Support (`think: false` & Zero-Wasted Thinking Tokens):**
  - Resolved `AI returned an empty response`: When using Ollama reasoning/thinking models (such as `qwen3:4b`), Ollama by default generates internal `<think>...</think>` reasoning tokens that are placed into a separate `thinking` property. If the model is truncated during thinking, Ollama returns `response: ""`, causing the JSON validator to fail.
  - Added `think = false` in `OllamaClient.GenerateJsonAsync` and `SendChatAsync` requests. This instructs Ollama to disable thinking mode, allowing the model to produce the final JSON response directly in ~30 seconds.
  - Added fallback JSON extraction: if `response` is ever empty, `OllamaClient` inspects the `thinking` property for embedded JSON objects before throwing.
  - Added automatic tag stripping: cleanly strips any `<think>...</think>` reasoning blocks from assistant chat messages so only the direct advisory answers are displayed.

---

## [1.3.9] - 2026-10-21

### Fixed
- **Resilience Against Duplicate Candidate IDs (`Key already added` exception):**
  - Resolved `ArgumentException: An item with the same key has already been added. Key: 591166` in `AiAnalysisService.ValidateAndParseResponse` by replacing `.ToDictionary(c => c.FileRecordId)` with collision-safe `candidateMap.TryAdd(c.FileRecordId, c)` and `TryGetValue`.
  - Added unique candidate grouping (`.GroupBy(c => c.FileRecordId).Select(g => g.First())`) in `BuildInferencePayload` ensuring prompt candidates are always unique by file ID.
  - Implemented automatic cache purging (`DELETE FROM AnalysisResults WHERE ScanRunId = @ScanRunId;`) and candidate deduplication in `SqliteScanRepository.SaveAnalysisResultsAsync`, preventing duplicate rows from accumulating across repeated analysis runs.
  - Added duplicate `FileId` filtering in `GetCachedAnalysisResultsAsync` using a `seenFileIds` set to safely handle existing SQLite databases with historical duplicate analysis rows.
- **Runaway Generation Prevention (`num_predict` Token Cap):**
  - Added `num_predict = 1536` in `OllamaClient.GenerateJsonAsync` and `num_predict = 1024` in `SendChatAsync`. In "Thinking" models (such as `qwen3:4b`), omitting `num_predict` allowed llama-server's `--context-shift` feature to shift contexts and loop token generation indefinitely across multiple context shifts (generating >5,700 tokens over 7 minutes).
  - Explicitly instructed the model in the system prompt to suppress internal chain-of-thought monologue and `<think>...</think>` tags during JSON generation mode, outputting the structured JSON object immediately.

---

## [1.3.8] - 2026-10-21

### Fixed
- **HttpClient Default Timeout Premature Abort (99s / 1m39s):**
  - .NET's `HttpClient.Timeout` defaults to 100 seconds (`1m40s`). Even when users configured higher timeouts (such as 600s in Settings), `HttpClient` internally aborted requests at ~99 seconds (`1m39s`), cancelling Ollama generation mid-inference.
  - Configured `client.Timeout = Timeout.InfiniteTimeSpan` on `IOllamaClient` HTTP client registration in DI and safely initialized in `OllamaClient` constructor so request lifetime is strictly governed by the user-configured `CancellationTokenSource`.
- **Accurate Elapsed Time Reporting on Inference Timeouts:**
  - Replaced hardcoded timeout string reporting in `OllamaClient` with `Stopwatch.StartNew()` elapsed measurement: `"Ollama inference timed out after {elapsedSec}s (configured limit: {timeoutSeconds}s) while generating response..."`, preventing misleading messages claiming 600s elapsed when an earlier disconnect occurred.
- **VRAM Footprint & Inference Speed Optimization (4096 Context Window):**
  - Adjusted `num_ctx = 4096` in `OllamaClient.GenerateJsonAsync` and `SendChatAsync`. An 8192 context window allocated >1.2 GB of KV cache, exceeding the free VRAM on 4GB consumer GPUs (such as NVIDIA GeForce RTX 3050 Laptop GPU) and forcing Ollama to offload layers to host CPU, which throttled generation speed from ~18 tokens/sec down to ~6 tokens/sec. 4096 context allows the entire model and KV cache to fit comfortably inside GPU VRAM.
  - Bounded candidate review payload in `AiAnalysisService.BuildInferencePayload` to 25 items max and trimmed rule explanations to 80 chars, keeping prompt and response tokens compact (~1000 prompt tokens, ~500 response tokens).
- **Configurable Chat Turn Timeout & Timeout Handling:**
  - Updated `AiReportsViewModel.SendChatMessageAsync` to honor `_configurationService.Current.AI.TimeoutSeconds` rather than a hardcoded 120-second timeout, and added explicit `TimeoutException` handling with input restoration.
- **UI History Auto-Refresh on Inference Completion or Failure:**
  - Ensured `RefreshHistoryAsync` runs unconditionally in `AiReportsViewModel.RunAiAnalysisAsync` following try/catch/finally, so failed or timed-out inference runs appear immediately in the AI Report History table without requiring a manual refresh.
- **Settings View Helper Label Synchronization:**
  - Corrected helper text labels in `SettingsView.xaml` to accurately reflect `TimeoutSeconds` default (180s) and `MaxPayloadCandidates` default (25).
- **Safeguard Timeout for `GetAvailableModelsAsync`:**
  - Added linked 15-second cancellation timeout to `OllamaClient.GetAvailableModelsAsync` to prevent hanging if the Ollama daemon stalls while enumerating models.

---

## [1.3.7] - 2026-10-21

### Fixed
- **AI Decision Support Review Accessibility & Open Review Feedback:**
  - Resolved issue where clicking "📄 Open Review" appeared to do nothing: the button was active for failed or incomplete runs that had no generated report file.
  - Added `HasReportFile` computed property on `AiReportRecord` and bound `IsEnabled="{Binding HasReportFile}"` on the "Open Review" button.
  - Displayed `ErrorMessage` with tooltips directly in the AI Report History table for failed runs instead of an empty path column.
  - Enhanced `OpenReportFileCommand` to provide immediate, descriptive status feedback if a report is missing or cannot be opened.
- **Local AI Inference Timeout & Context Window Optimization:**
  - Increased default `TimeoutSeconds` from 60s to 180s (3 minutes) to provide adequate headroom for local LLM loading and JSON generation on consumer GPUs and CPUs.
  - Specified `num_ctx = 8192` in Ollama options for inference and chat, preventing llama-server from truncating prompts to 2048 tokens.
  - Bounded inference candidate payload (default 25 candidates with concise rule explanations) to reduce token count by half, accelerating generation speed 2-3x.
  - Differentiated client-side timeout from user cancellation: when the inference timeout elapses, the application now throws and reports a clear `TimeoutException` explaining that inference timed out and can be adjusted in Settings, instead of erroneously stating "AI inference cancelled by user".
- **AI Reports Database State Reconciliation:**
  - Fixed duplicate/zombie rows in `AiReports` by updating existing records by ID (`UPDATE AiReports ... WHERE Id = @Id`) rather than repeatedly inserting new rows on status transitions.
  - Added startup initialization query to automatically reconcile any interrupted `Generating` records to `Failed`.

---

## [1.3.6] - 2026-10-20

### Fixed
- **Recursive Directory Roll-Up Boundary & Non-Overlapping Treemap Partitioning:**
  - Displayed `RecursiveSizeBytes` in the Dashboard "Largest Folders" list to ensure nested-only directories show true total storage usage rather than 0 B direct bytes.
  - Constrained hierarchical roll-up in `StorageAnalysisEngine` to stop strictly at scan roots (`SelectedDrives`) and scanned paths, preventing ancestor folders outside the scan scope (e.g. `D:\` when scanning `D:\Users`) from appearing with partial totals.
  - Introduced `TreemapPartitionHelper.FilterNonOverlappingDirectories` to feed the Treemap non-overlapping partitions, ensuring parent and child folder byte counts are not double-counted and tooltips accurately reflect non-overlapping storage shares.
- **PowerShell Script Generation Hardening (cleanName Log Filename Injection):**
  - Sanitized and escaped `cleanName` in `ArchivePlannerService` using `EscapePowerShellSingleQuotedLiteral`, and updated `SanitizeIdentifier` to strip single quotes (`'`), semicolons (`;`), hashes (`#`), dollar signs (`$`), and backticks (`` ` ``).
  - Added automated PowerShell AST verification tests (`System.Management.Automation.Language.Parser`) asserting no extra statements or command injections occur even with crafted batch names.
- **Candidate Analysis Cache Invalidation on In-Progress / Resume / Discard:**
  - Enforced that persistent database-backed `AnalysisResults` are cached only for completed scans (`isComplete == true`). In-progress scans bypass persistent cache reads and writes.
  - Implemented `DeleteAnalysisResultsAsync` in `SqliteScanRepository`, automatically purging stale candidate records whenever a scan run is resumed, discarded, or invalidated.
- **UTC Timestamp Display Consistency Across Views:**
  - Introduced `UtcToLocalDateTimeConverter` (registered in `App.xaml` and leveraging `UtcDateTimeHelper.ToLocal`), applying consistent local-time formatting across `ScansView`, `InventoryView`, `DuplicateAnalysisView`, and `AiReportsView` to eliminate timezone display offsets on non-UTC machines.
- **Thread-Safe Archive Estimation & Input Lockout:**
  - Captured form inputs (`NewSourcePath`, `NewBatchName`, `NewDestinationPath`) into immutable local variables before awaiting the directory estimate in `ArchivePlanViewModel.AddBatchAsync`, preventing race conditions where user edits during estimation could point the batch at an unmeasured folder.
  - Added `CanEditForm` (`!IsEstimating`) property binding in `ArchivePlanView.xaml` to disable source/destination inputs and the Add button while estimation is in progress.
- **Documentation Synchronization for AI Loopback Policy:**
  - Updated `README.md`, `PRIVACY.md`, and `ARCHITECTURE.md` to accurately and consistently reflect that AI loopback enforcement is enabled by default with an optional user-controlled setting for remote/LAN Ollama endpoints.
- **Cloud Placeholder Detection in Archive Estimation:**
  - Added `FilesystemScanner.IsCloudPlaceholder` check before skipping `ReparsePoint` items during archive directory estimation, preventing OneDrive and other cloud placeholder files and directories from being mistakenly skipped and undercounted.

---

## [1.3.5] - 2026-10-20

### Fixed
- **SQLite Dependency Security Fix (CVE-2025-6965):** Upgraded `Microsoft.Data.Sqlite` to 9.0.21 in Infrastructure and CrashWorker projects, resolving transitive dependency `SQLitePCLRaw.bundle_e_sqlite3` 2.1.13 and eliminating High-severity CVE-2025-6965. Added automated `dotnet list package --vulnerable --include-transitive` audit gates to CI build and release workflows.
- **Recursive Directory Roll-Up for Storage Analysis & Treemap:** Implemented recursive hierarchical roll-up in `StorageAnalysisEngine.GetLargestDirectoriesAsync` so directory size and file counts recursively accumulate all descendant files, surfacing directories containing nested subfolders and providing accurate hotspot and treemap metrics. Scan state is now reflected dynamically for `IsComplete`.
- **Timezone-Agnostic UTC Timestamp Parsing:** Introduced `UtcDateTimeHelper` parsing ISO-8601 UTC timestamps using invariant culture with `DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal`, ensuring database-stored UTC timestamps consistently produce `DateTimeKind.Utc` and accurate file ages regardless of local host timezone.
- **Analysis Cache In-Progress Scan Poisoning Guard:** Constrained analysis caching strictly to completed scans (`ScanState.Completed`), preventing in-progress scans from poisoning the cache, and wired automatic cache invalidation into scan completion, resume, discard, and history deletion routines.
- **Local AI Endpoint Network Policy & Audit:** Added prominent UI security warning callouts and audit logging when the loopback-only AI enforcement policy is disabled, and updated `SECURITY.md` to document the network boundary and the intentional advanced bypass behavior.
- **PowerShell Script Injection Defense in Archive Planner:** Secured generated `preview-archive.ps1` and `copy-archive.ps1` scripts by strictly escaping PowerShell single-quoted literals and eliminating `$()` command interpolation in double-quoted strings for batch names, source paths, and destination paths. Added validation in `ArchivePlanViewModel` to reject control characters and newlines in batch names.
- **Asynchronous Archive Batch Estimation & Junction Skipping:** Offloaded directory size estimation in `ArchivePlanViewModel` to a background worker with cancellation support, added progress reporting, skipped `ReparsePoint` entries (directory junctions/symlinks) to prevent infinite loops, and added explicit warnings when inaccessible paths produce partial estimates.

---

## [1.3.4] - 2026-10-20

### Fixed
- **Single-Instance Mutex & Focus Activation:** Added named system mutex (`Local\LocalFileAudit_SingleInstance_Mutex`) in `App.xaml.cs`. When an existing instance is detected, the new launch restores and focuses the active window via Win32 `ShowWindow`/`SetForegroundWindow` and cleanly exits without touching the database, preventing concurrent process contention.
- **SQLite Concurrency & Busy Timeout Hardening:**
  - Configured `DefaultTimeout = 30` (and `60` in database initializer) on all `SqliteConnectionStringBuilder` instances, coupled with `PRAGMA busy_timeout = 30000;` on all opened connections.
  - Replaced `Cache = SqliteCacheMode.Shared` with `Cache = SqliteCacheMode.Private` across all database connections to avoid table-level lock collisions in WAL mode.
  - Switched startup checkpoint from blocking `PRAGMA wal_checkpoint(TRUNCATE)` to non-blocking `PRAGMA wal_checkpoint(PASSIVE)` to eliminate startup deadlocks when database handles are active.
- **Startup Query Storm Elimination & Lazy Initialization:** Removed eager unawaited background queries from ViewModel constructors (`ScansViewModel`, `InventoryViewModel`, `DuplicateAnalysisViewModel`, `AiReportsViewModel`, `CompareRunsViewModel`, `ArchivePlanViewModel`) and introduced `EnsureInitializedAsync()` invoked upon first navigation.
- **Repository-Level Analysis Results Caching:** Routed `AnalysisResults` reads and writes through `IScanRepository` (`GetCachedAnalysisResultsAsync` and `SaveAnalysisResultsAsync`), synchronizing writes via `_writeLock` to prevent concurrent database lock conflicts during report generation.

---

## [1.3.3] - 2026-10-20

### Fixed
- **UI Responsiveness & Analysis Background Threading (P0):** Offloaded synchronous SQLite analysis queries (`AnalyzeScanAsync`) to background worker threads via `Task.Run` in `StorageAnalysisViewModel` and `DashboardViewModel`, and made storage analysis initialization lazy (`EnsureInitializedAsync`) to guarantee stutter-free UI interactions.
- **Analysis Results Caching (P0):** Activated the previously unused `AnalysisResults` SQLite table to cache review candidates per scan run, paired with an in-memory `StorageMetricsSummary` cache and automatic cache invalidation (`InvalidateCache`) when new scans complete.
- **Current Folder Text Reset (P1):** Ensured `CurrentDirectory` in `ScansViewModel` cleanly resets to `"Ready"` upon scan completion, cancellation, and resume termination.
- **Async Navigation Callback Safety (P1):** Replaced risky `async void` / `Action<Guid>` navigation callbacks with `Func<Guid, Task>` accompanied by robust exception handling and logging to prevent application crashes during view transitions.
- **Navigation Tab Active Highlighting (P1):** Implemented `NavTabStyle` using `RadioButton` navigation controls with active-state tracking, glowing accent border indicators, and contrasting background highlights for the selected tab.
- **Inaccessible Path Scan Error Reporting (P2):** Expanded scan completion status and desktop notification toasts to explicitly report the number of inaccessible paths and point directly to `ScanErrors.csv`.
- **Documentation & User Guide Synchronization (P2):**
  - Updated `USER_GUIDE.md` navigation headers, button labels ("▶ Start File Inventory Scan"), and safety documentation to match the current UI.
  - Corrected `README.md:153` tab reference from "AI Insights" to "AI Reports".
- **ViewModel Title Standardization (P3):** Standardized titles across ViewModels and page headers to match sidebar navigation labels ("Run Comparison", "Archive Plan", "Archive Verifier").
- **Safety Mode Terminology Consistency (P3):** Standardized safety wording throughout the UI to consistent read-only phrasing ("Strict Read-Only Mode Active", "Read-Only Protected (Zero Mutations)").
- **Database Overhead & Checkpoint Optimization (P3):** Dropped redundant duplicate index `idx_files_scan_path` (already indexed by table uniqueness constraint), added `idx_files_scan_cloud`, and introduced automatic `PRAGMA wal_checkpoint(TRUNCATE)` and `PRAGMA optimize` on startup and scan discards to keep database and WAL sizes compact.
- **Analysis View UI Virtualization (P3):** Enabled UI virtualization with item container recycling and smooth pixel scrolling for `ReviewCandidates` and `LargestFiles` in `StorageAnalysisView.xaml` to eliminate rendering lag on large file collections.

---

## [1.3.2] - 2026-10-20

### Fixed
- **UI Command Enablement & Visibility (`Converter={x:Null}`):** Eliminated broken placeholder bindings across all XAML views:
  - Enabled "Start File Inventory Scan" button at idle by removing inverted raw `IsEnabled` binding.
  - Fixed phantom "Incomplete Scan Detected" banner by binding to `HasIncompleteScan` with `BooleanToVisibilityConverter`.
  - Restored idle enablement to "Run Local AI Analysis", "Find by Size", "Verify Hashes", and "Export Audit Reports" buttons.
  - Properly bound progress bars, results panels, and status messages to `BooleanToVisibilityConverter`.
- **CSV Full Inventory Export Escaping:** Wrapped `file.Extension` in `EscapeCsv` alongside `file.FullPath` to prevent filenames containing commas from corrupting CSV column alignment.
- **AI Chat Prompt Injection Defense:** Appended strict untrusted-input security rules to the AI chat system prompt to prevent prompt injection via candidate filenames.
- **AI Chat Clean Failure Handling:** Prevented un-responded user messages from dangling in SQLite history when inference fails, restoring the typed message to the input box for effortless retries.
- **Dynamic SemVer Test Assertions:** Replaced hardcoded version literals in tests with dynamic format validation to prevent test failures on release version bumps.
- **XAML Null Converter Regression Guard:** Added automated test scanning all views to ensure `Converter={x:Null}` cannot be reintroduced.

---

## [1.3.1] - 2026-10-20

### Fixed
- **Storage Metrics Honesty:** Removed fallback C:\ volume capacity/free space calculation when scan roots cannot be resolved from scan run metadata, reporting 0 (unknown) to prevent showing misleading drive statistics.
- **Cancellation Progress Jitter:** Suppressed cancellation-unwind progress increments in `ArchiveVerificationService` when cancellation has been requested.

---

## [1.3.0] - 2026-10-20

### Added
- **Storage Squarified Treemap (F2):** High-performance interactive visual treemap on the Dashboard rendering top directory consumers (`DirectorySummary.RecursiveSizeBytes`) using the squarified layout algorithm (Bruls, Huizing, van Wijk). Features extension-mix palette, hover tooltips, and right-click "Reveal in Explorer" / "Copy Path".
- **Archive Verification Engine (F3):** In-app verification service (`ArchiveVerificationService`) and view (`ArchiveVerificationView`) providing read-only relative path matching between source and destination archive folders. Supports on-demand SHA-256 cryptographic hash verification, nested-path recursion safety guard, and CSV export (`ArchiveVerification-<ts>.csv`) categorizing items into `Match`, `MissingAtDestination`, `SizeMismatch`, `HashMismatch`, and `ExtraAtDestination`.
- **Archive Batch Planner (F4):** Interactive planner view (`ArchivePlanView`) to organize and stage files into archive batches. Verifies destination drive capacity (`DriveInfo`), persists atomically to `archive-plans.json`, and generates:
  - `ArchiveManifest.csv`: Machine-readable batch manifest.
  - `preview-archive.ps1`: Robocopy script running strictly in `/L` (list-only / preview) mode.
  - `copy-archive.ps1`: Reviewable copy script with prominent safety headers ("REVIEW BEFORE RUNNING — this app never executes file operations").
  - Verification workflow marking batches verified once confirmed by the Archive Verifier.
- **AI Follow-up Chat (F5):** Conversational back-and-forth chat panel in AI Insights scoped to the selected scan run. Injects bounded, data-minimized candidate metadata with loopback security enforcement and strict advisory-only system prompts ("NEVER recommend automatic deletions"). Chat history is persisted in a new additive SQLite `ChatMessages` table with clear history commands and "Ask AI" context buttons on candidate cards and file inventory rows.

---

## [1.2.0] - 2026-10-20

### Added
- **Full Inventory Streaming CSV Export (F1):** Export every file discovered in a scan to a streaming CSV with progress reporting, cooperative cancellation, and optional filters for file extension and minimum size.
- **Run Comparison & Change Tracking (F6):** Compare any two historical scan runs to detect `New`, `Deleted`, `Modified`, and `Unchanged` files with size and timestamp deltas. Includes per-extension aggregation deltas and CSV export.
- **Storage Accuracy & Allocated Size (F7):**
  - Logical vs Allocated Size: Displays volume capacity and free space on Dashboard KPIs, plus on-demand on-disk allocated size in the File Inspector via Win32 `GetCompressedFileSizeW`.
  - Cloud Placeholders: Detects OneDrive and cloud-only placeholder files (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` / `FILE_ATTRIBUTE_OFFLINE`) without forcing sync downloads, tracking them safely and reporting placeholder counts and sizes separately.

---

## [1.1.0] - 2026-10-20

### Added
- **Centralized Assembly Versioning (S1):** Unified version configuration in `Directory.Build.props` (1.1.0+) dynamically read by UI badges, file scanners, startup logs, and report manifests.
- **AI Timeout & Payload Settings (S2):** Exposed inference timeout (seconds) and maximum request payload limits directly in the Settings view.
- **Duplicate Hash Reuse (S3):** Duplicate analysis engine checks previous scan runs for matching `(FullPath, SizeBytes, LastWriteTimeUtc)` to reuse existing cryptographic hashes, reducing recurring scan times from hours to minutes while maintaining pre/post hash modification guards.
- **One-Click AI Review Tables (S4):** Export AI recommendations, prioritized review plans, and safety warnings directly to `AI-Review-Table.csv` and `AI-Review-Table.md`.
- **Deep-Link from AI Recommendations to Inventory (S5):** Clicking "🔍 Inspect in Inventory" on any AI recommendation navigates directly to the File Inventory view and highlights the file in the inspector.
- **Scan-Completion Desktop Notifications (S6):** Added system tray notifications and audio chimes upon completion of scans and full inventory exports.

### Security Invariants Preserved
- Permanently 100% read-only analysis of source directories.
- Zero file deletion, movement, or automatic execution APIs.
- Loopback-only AI enforcement with strict local data minimization.
