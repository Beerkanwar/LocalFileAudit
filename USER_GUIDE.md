# LocalFileAudit — User Guide 📖

Welcome to **LocalFileAudit**! This user guide walks you through every screen and feature of the application to help you audit your storage, discover space-wasting files, identify duplicates, and generate comprehensive reports.

---

## 1. Getting Started

### Launching the Application
Launch `LocalFileAudit.App.exe`. The application runs as a standard desktop program without requiring administrative privileges.

> **Note on Windows SmartScreen:** Because this open-source application is not signed with a paid commercial code-signing certificate, Windows SmartScreen may show a prompt saying *"Windows protected your PC"*. Simply click **More info** and then click **Run anyway**. You can also verify the SHA-256 checksum of your download against the release checksum file.

### Main Navigation
The left sidebar provides access to all major features:
- **📊 Dashboard** — High-level storage metrics, squarified storage treemap, and space hotspots.
- **🔍 Scans** — Configure drives, start, pause, resume, and monitor scans.
- **📋 File Inventory** — Search, filter, export full inventory (CSV), and inspect discovered files.
- **📈 Storage Analysis** — Review deterministic metrics and generate offline reports.
- **📑 Duplicate Analysis** — Identify duplicate files by size clustering with historical hash reuse.
- **🤖 AI Reports** — Narrative reviews, one-click table exports, and interactive follow-up chat via Ollama.
- **⚖ Run Comparison** — Track filesystem changes (New, Deleted, Modified) between any two scans.
- **📦 Archive Plan** — Organize cold files into batches, monitor disk space, and generate reviewable Robocopy scripts.
- **✓ Archive Verifier** — Read-only verification comparing source and backup folders by relative paths, sizes, and SHA-256.
- **⚙ Settings** — Configure scanning preferences, Ollama limits/timeouts, and database backups.

---

## 2. Running a Filesystem Scan

1. Click on **Scans** in the left sidebar.
2. Under **Select Drives or Folders**, check the drive letters (e.g., `C:\`, `D:\`) or enter a specific directory path you wish to scan.
3. Configure scan options:
   - **Low-Impact Mode (Recommended for background scanning):** Introduces micro-delays between directory reads to keep CPU and disk usage low.
   - **Follow Reparse Points:** Leave unchecked to safely skip NTFS junctions and symbolic links to prevent circular loops. Cloud placeholders (OneDrive/Dropbox) are recorded without triggering forced local hydration.
4. Click **▶ Start Scan**.
5. During the scan, real-time metrics update smoothly. When complete, a desktop notification toast and audio chime alert you.

---

## 3. Exploring Dashboard & Squarified Treemap

The **Dashboard** gives you immediate visual insights:
- **KPI Cards:** Volume capacity, total logical size, total file count, and cloud placeholder counts.
- **Storage Treemap (F2):** Visual squarified treemap rendering the top directory consumers. Rectangles are proportional to recursive directory size with distinct extension color gradients. Hover to see paths and percentages; right-click or click to Reveal in Explorer or Copy Path.
- **Extension Distribution:** Progress bars showing storage share by file type.
- **Storage Hotspots:** Directories consuming the most disk space.

---

## 4. Searching and Exporting the File Inventory

Navigate to **File Inventory**:
- **Search Query:** Search across millions of files by path or file name.
- **Filters:** Filter by extension, minimum/maximum size, or modification date.
- **Inspector Panel:** Displays logical size, on-disk allocated size (`GetCompressedFileSize`), timestamps, and classification badges.
- **Full Inventory CSV Export (F1):** Click **📥 Export Full Inventory (CSV)** to stream-export millions of rows to CSV with real-time progress, cancellation support, and optional extension/size filters.
- **🤖 Ask AI Button:** Click **🤖 Ask AI** in the inspector to inject the selected file directly into the AI Follow-up Chat.

---

## 5. Detecting Duplicates with Hash Reuse

Navigate to **Duplicate Analysis**:
1. Select a completed scan run.
2. Click **🔍 Find Potential Duplicates (By Size)** to cluster identical file sizes.
3. Click **🔐 Verify Cryptographic Hashes (SHA-256)**:
   - Reuses previous hashes for unchanged `(FullPath, SizeBytes, LastWriteTime)` files to finish audits in minutes.
   - Verifies pre/post hash modification timestamps for newly hashed files.

---

## 6. Run Comparison (Change Tracking)

Navigate to **Run Comparison**:
1. Pick a **Baseline Scan Run** and a **Comparison Scan Run**.
2. Click **⚖ Compare Runs**:
   - Compares millions of records by `FullPath`.
   - Identifies `New`, `Deleted`, `Modified` (size or timestamp delta), and `Unchanged` files.
   - Visualizes extension-level storage deltas and offers one-click CSV export.

---

## 7. Archive Batch Planner & Script Generation

Navigate to **Archive Plan**:
1. Click **+ Add Archive Batch**: specify batch name, source directory, planned external destination path (e.g., USB drive `E:\Archive`), and reason.
2. The planner automatically verifies destination free space via `DriveInfo` and warns if the batch exceeds available drive capacity.
3. Click **📝 Generate Scripts & Manifest**:
   - `ArchiveManifest.csv`: Machine-readable batch manifest.
   - `preview-archive.ps1`: Ready-to-review Robocopy preview script using `/L` (list-only mode; zero file operations executed).
   - `copy-archive.ps1`: Safe copy script with prominent warning headers ("REVIEW BEFORE RUNNING — this app never executes file operations").
4. Run scripts manually in PowerShell. When completed, verify the batch using the Archive Verifier and mark it **Verified**.

---

## 8. Archive Verification (Read-Only)

Navigate to **Archive Verifier**:
1. Pick your **Source Directory** and **Destination Directory** (nested directory recursion guard prevents overlapping selections).
2. Check **Calculate SHA-256 Hashes** if cryptographic match verification is desired.
3. Click **✓ Verify Archive**:
   - Opens files strictly with `FileAccess.Read` on both source and destination.
   - Maps relative paths and detects: `Match`, `MissingAtDestination`, `SizeMismatch`, `HashMismatch`, and `ExtraAtDestination`.
4. Click **📥 Export Verification CSV** to save `ArchiveVerification-<timestamp>.csv`.

> **Note on Empty Directories:** The Archive Verifier indexes and compares file records only. Empty folders (even if replicated via Robocopy `/E`) are not tracked or flagged in the verification report.

---

## 9. Local AI Reports & Follow-up Chat (Ollama)

Navigate to **AI Reports**:
1. Test your local Ollama connection on `http://127.0.0.1:11434`.
2. Generate executive summaries and review candidates.
3. Click **📥 Export Review Table (CSV / MD)** for tabular exports.
4. **Follow-up Chat (F5):** Ask questions about candidates, storage patterns, or retention criteria grounded strictly in the current scan's metadata. History is saved locally in SQLite and can be cleared at any time.

---

## 10. Database Backups & Settings

In **Settings**:
- **Ollama Settings:** Configure model name, timeout (seconds), and maximum request payload limits.
- **Database Backup:** Click **📦 Backup Database (Online Snapshot)** to create a clean, consistent snapshot of your SQLite inventory using `VACUUM INTO`.
- **Open Data Directories:** Quick shortcuts to open the database, logs, and reports directories in Windows Explorer.
