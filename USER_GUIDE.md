# LocalFileAudit — User Guide 📖

Welcome to **LocalFileAudit**! This user guide walks you through every screen and feature of the application to help you audit your storage, discover space-wasting files, identify duplicates, and generate comprehensive reports.

---

## 1. Getting Started

### Launching the Application
Launch `LocalFileAudit.App.exe`. The application runs as a standard desktop program without requiring administrative privileges.

> **Note on Windows SmartScreen:** Because this open-source application is not signed with a paid commercial code-signing certificate, Windows SmartScreen may show a prompt saying *"Windows protected your PC"*. Simply click **More info** and then click **Run anyway**. You can also verify the SHA-256 checksum of your download against the release checksum file.

### Main Navigation
The left sidebar provides access to all major features:
- **📊 Dashboard** — High-level storage metrics, extension distributions, and space hotspots.
- **⚡ Scanner** — Configure drives, start, pause, resume, and monitor scans.
- **📁 File Inventory** — Search, filter, and inspect discovered files across runs.
- **📑 Duplicates** — Identify duplicate files by size clustering and SHA-256 verification.
- **📈 Analysis & Reports** — Review deterministic metrics and generate offline reports.
- **🤖 AI Insights** — Optional narrative reviews and storage advice powered by local Ollama.
- **📜 Scan History** — Browse past scan sessions, resumption states, and error records.
- **⚙️ Settings** — Configure scanning preferences, Ollama endpoints, and database backups.

---

## 2. Running a Filesystem Scan

1. Click on **Scanner** in the left sidebar.
2. Under **Select Drives or Folders**, check the drive letters (e.g., `C:\`, `D:\`) or enter a specific directory path you wish to scan.
3. Configure scan options:
   - **Low-Impact Mode (Recommended for background scanning):** Introduces micro-delays between directory reads to keep CPU and disk usage low.
   - **Follow Reparse Points:** Leave unchecked to safely skip NTFS junctions and symbolic links to prevent circular loops.
4. Click **▶ Start Scan**.
5. During the scan, you will see real-time metrics:
   - **Files Discovered** and **Total Size**.
   - **Elapsed Time** and **Current Scanning Directory**.
   - **Pause / Resume:** Temporarily pause the scan at any time to free up disk bandwidth.
   - **Cancel:** Safely abort the scan without losing already committed batches.

---

## 3. Exploring the Dashboard

Once a scan completes (or is paused), the **Dashboard** gives you immediate visual insights:
- **KPI Cards:** Overview of total logical size, total file count, and duration.
- **Extension Distribution:** Progress bars showing storage share by file type (e.g., `.mkv`, `.iso`, `.zip`, `.exe`).
- **Largest Files:** Quick preview of top space consumers.
- **Storage Hotspots:** Directories consuming the most disk space.
- **Quick Links:** Click any card to navigate directly to detailed views.

---

## 4. Searching and Filtering the File Inventory

Navigate to **File Inventory** to search millions of files:
- **Search Query:** Search by file name or partial directory path.
- **Extension Filter:** Filter by specific extension (e.g., `.log`, `.tmp`, `.mp4`).
- **Size Bounds:** Set minimum and maximum file size thresholds (e.g., files > 500 MB).
- **Date Filters:** Filter by last modified timestamp.
- **Sorting:** Sort columns by File Name, Size (Descending/Ascending), or Modification Date.
- **Inspector Panel:** Select any row to view full file path, creation/access timestamps, attributes, and deterministic classification rule badges (e.g., `RULE_LARGE_FILE`, `RULE_OLD_MEDIA`).
- **Safe Actions:** Use **📋 Copy Path** or **📂 Reveal in Explorer** to locate files safely in Windows Explorer.

---

## 5. Detecting Duplicates with SHA-256 Verification

Navigate to **Duplicates**:
1. Select a completed scan run.
2. Set the **Minimum Size** threshold (default is 1 MB).
3. Click **🔍 Find Potential Duplicates (By Size)**:
   - Groups files with identical byte lengths in milliseconds.
   - Shows potential wasted space per group.
4. Click **🔐 Verify Cryptographic Hashes (SHA-256)**:
   - Computes SHA-256 hashes for clustered files asynchronously with real-time progress.
   - Discards files whose contents differ, leaving only cryptographically verified duplicate sets.
5. Review the files in each cluster. Remember: **LocalFileAudit never deletes files**. You can copy paths or open the file in Explorer to manage them manually.

---

## 6. Generating Deterministic Reports

Navigate to **Analysis & Reports**:
1. Click **📄 Generate Reports (Offline)**.
2. The engine atomically creates 8 audit files inside `%LOCALAPPDATA%\LocalFileAudit\reports\scan-<guid>-<timestamp>\`:
   - `Summary.md` — Markdown executive summary.
   - `Summary.html` — Fully styled, offline HTML report with charts and metrics.
   - `ExtensionSummary.csv` — Full spreadsheet of file extension metrics.
   - `DirectorySummary.csv` — Directory size breakdown.
   - `ReviewCandidates.csv` — Files classified for review with human-readable explanations.
   - `ScanErrors.csv` — Any path access or permission errors.
   - `Inventory.csv` — Top largest files summary.
   - `Manifest.json` — Audit verification manifest with SHA-256 hashes.
3. Click **📂 Open Reports Folder** to view and share the files.

---

## 7. Using Optional Local AI (Ollama)

To obtain natural language storage summaries:
1. Ensure Ollama is running locally:
   ```bash
   ollama run qwen2.5:7b
   ```
2. Navigate to **AI Insights** in LocalFileAudit.
3. Confirm that the status indicator shows **● Connected (Local Loopback)**.
4. Click **Generate AI Narrative Review**:
   - The engine minimizes candidate data and generates an executive review explaining storage patterns and archive recommendations.
   - Saves an `AI-Review.md` document in your reports directory.

---

## 8. Database Backups & Settings

In **Settings**:
- **Ollama Settings:** Configure model name, response timeout, and request size limits.
- **Database Backup:** Click **📦 Backup Database (Online Snapshot)** to create a clean, consistent snapshot of your SQLite inventory using `VACUUM INTO`.
- **Open Data Directories:** Quick shortcuts to open the database, logs, and reports directories in Windows Explorer.
