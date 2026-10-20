# Changelog

All notable changes to the LocalFileAudit project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
