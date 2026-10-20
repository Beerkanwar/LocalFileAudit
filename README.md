# LocalFileAudit 🔍

**A high-performance, crash-safe, and privacy-first local file inventory and storage auditing desktop application for Windows.**

Built with C# and .NET 9 (WPF), LocalFileAudit provides deep visibility into your storage consumption, large file hotspots, duplicate files, and archive candidates—with **100% local execution, zero telemetry, and permanent read-only safety**.

[![Build and Test](https://github.com/Beerkanwar/LocalFileAudit/actions/workflows/build.yml/badge.svg)](https://github.com/Beerkanwar/LocalFileAudit/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.txt)
[![Target: .NET 9](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform: Windows 10 / 11](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20x64-0078D6.svg)](https://microsoft.com)

---

## 🛡️ Core Philosophy: Safety, Privacy & Determinism

> **"The scanner discovers facts, deterministic rules calculate metrics, optional local AI explains possibilities, and the user makes decisions."**

1. **Permanently Read-Only:** LocalFileAudit will **NEVER** delete, modify, move, or rename your source files. There is zero code in the scanning or analysis engines capable of mutating user files.
2. **Absolute Privacy:** 100% offline and local. Zero tracking, zero telemetry, and zero data leaving your machine. Network connections to non-loopback endpoints are strictly blocked by default configuration at the client architecture level.
3. **Crash-Safe Persistence:** Powered by SQLite in Write-Ahead Logging (WAL) mode with bounded channel traversal. If interrupted by power loss or system crash, committed records are safely preserved and incomplete scans can be resumed.
4. **Deterministic Foundation:** Storage analysis, space aggregation, extension statistics, and candidate classification are computed using deterministic math and rule engines—**completely independent of AI**.
5. **Optional Local AI Review:** Connect to a locally hosted [Ollama](https://ollama.ai) instance for natural language analysis and storage advice. AI recommendations are strictly advisory and never treated as automated action triggers.

---

## ✨ Features

- **⚡ Fast Bounded Filesystem Scanner:**
  - Asynchronous traversal using `System.Threading.Channels` with bounded concurrency and backpressure.
  - Safe reparse-point / symbolic link skipping by default to prevent infinite directory recursion loops.
  - Non-blocking cooperative pause, resume, and cancellation.
  - Low-impact scanning mode with throttling intervals for background operation.
- **📊 Interactive Desktop Dashboard & Storage Treemap:**
  - Storage consumption KPIs (Volume Capacity, Total Logical Size, Discovered Files, Elapsed Time, Cloud Placeholders).
  - **Squarified Visual Treemap:** Visual hierarchy of recursive directory storage consumption with extension palettes, hover inspection, and click-to-explore actions.
  - Extension distribution visual bars and size share percentages.
  - Top storage hotspot directories and largest files preview.
- **🔎 Parameterized Searchable Inventory & Streaming Full Export:**
  - Filter across millions of files by path, extension, file size, modification date, and filename.
  - Parameterized SQLite queries with indexed sorting and virtualized pagination.
  - Built-in file inspector displaying logical size, on-disk allocated size (`GetCompressedFileSize`), attributes, and classification badges.
  - **Full Inventory Streaming CSV Export:** Stream-writes entire file inventories to CSV with real-time progress, cancellation, and optional extension/size filters.
- **⚖ Run Comparison (Change Tracking):**
  - Select and compare any two historical scans to track `New`, `Deleted`, `Modified`, and `Unchanged` files.
  - Summary metrics and extension-level size delta breakdowns with exportable comparison CSVs.
- **📦 Archive Planning & Read-Only Verification Pipeline:**
  - **Archive Batch Planner:** Organize files into USB/external drive batches, monitor destination free space, and generate reviewable PowerShell scripts (`preview-archive.ps1` with robocopy `/L` and `copy-archive.ps1`) alongside an `ArchiveManifest.csv`.
  - **Archive Verifier:** Strictly read-only verification comparing source and destination trees by relative paths, file sizes, and on-demand SHA-256 hashes with nested-path guards and CSV reporting.
- **📑 Two-Stage Duplicate Detection with Hash Reuse:**
  - **Stage 1:** Lightning-fast grouping by exact byte length.
  - **Stage 2:** Cryptographic verification using asynchronous SHA-256 hashing.
  - **Historical Hash Reuse:** Reuses existing hashes from prior scans for unchanged files, accelerating audits from hours to minutes.
  - Pre- and post-hash timestamp verification to detect concurrent file modifications.
- **📄 Atomic Report Generation (Offline & Deterministic):**
  - Generates audit artifacts atomically via staging directories (`Summary.md`, `Summary.html`, `ExtensionSummary.csv`, `DirectorySummary.csv`, `ReviewCandidates.csv`, `ScanErrors.csv`, `Inventory.csv`, `Manifest.json`).
- **🤖 Local AI Decision Support & Follow-up Chat (Ollama):**
  - Offline Ollama integration over local loopback (`http://127.0.0.1:11434`) with strict loopback IP enforcement.
  - **Interactive Follow-up Chat:** Scoped conversational analysis per scan run grounded in candidate metadata, with persisted chat history and one-click "Ask AI" context links from inventory rows.
  - Exportable `AI-Review.md` narrative report and one-click `AI-Review-Table.csv` / `.md`.
  - System prompts strictly advisory-only with zero mutation authorities.

---

## 🖥️ System Requirements

- **Operating System:** Windows 10 (Version 1809 or higher) or Windows 11 (64-bit).
- **Architecture:** x64.
- **Privileges:** Standard user privileges (`asInvoker`). Administrator elevation is **never** required.
- **Runtime:** [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (unless using the self-contained build).
- **Optional for AI:** [Ollama](https://ollama.ai) installed and running locally on `http://127.0.0.1:11434`.

---

## 🚀 Getting Started

### 1. Download Latest Release (Recommended)

Pre-built self-contained binaries for 64-bit Windows are available on GitHub Releases:

1. Go to the **[Latest Release on GitHub](https://github.com/Beerkanwar/LocalFileAudit/releases/latest)**.
2. Download `LocalFileAudit-v<version>-win-x64.zip` and the corresponding `.sha256` checksum file.
3. *(Optional)* Verify archive integrity in PowerShell:
   ```powershell
   (Get-FileHash .\LocalFileAudit-v*.zip -Algorithm SHA256).Hash
   ```
4. **Extract the full ZIP archive** to a folder of your choice (do not run `LocalFileAudit.App.exe` inside the archive without extracting, as native satellite libraries like `e_sqlite3.dll` must remain alongside it).
5. Double-click **`LocalFileAudit.App.exe`** to launch. No separate .NET runtime installation is required.

---

### 2. Build from Source (Alternative)

If you prefer building from source, clone the repository and build using the .NET 9 SDK:

```powershell
# Clone the repository
git clone https://github.com/Beerkanwar/LocalFileAudit.git
cd LocalFileAudit\LocalFileAudit

# Restore dependencies
dotnet restore LocalFileAudit.slnx

# Run automated test suite (all unit, integration, and safety acceptance tests)
dotnet test LocalFileAudit.slnx --configuration Release

# Build the desktop application
dotnet build LocalFileAudit.slnx --configuration Release

# Publish self-contained executable to ./publish/
dotnet publish LocalFileAudit.App/LocalFileAudit.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:DebugType=none `
  -o ./publish/
```

The resulting application files will be available in `./publish/`. Extract or keep the folder contents intact when moving.

---

## ⚠️ Windows SmartScreen & Antivirus Notice

LocalFileAudit is an open-source utility published transparently on GitHub without an expensive commercial Extended Validation (EV) code-signing certificate. Consequently, Windows SmartScreen may display:

> *"Windows protected your PC — Microsoft Defender SmartScreen prevented an unrecognized app from starting."*

To run the application:
1. Click **More info**.
2. Click **Run anyway**.

You can review the full source code, inspect all dependencies in [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md), compile the application directly from source using the instructions above, or verify binary integrity using the `.sha256` checksum on the [Releases page](https://github.com/Beerkanwar/LocalFileAudit/releases).

---

## 🦙 Setting Up Local AI (Ollama)

LocalFileAudit works completely offline without AI. If you wish to enable the optional natural language storage review:

1. Download and install Ollama from [ollama.ai](https://ollama.ai).
2. Pull a recommended model (7B parameter models work exceptionally well):
   ```bash
   ollama pull qwen2.5:7b
   # or
   ollama pull llama3.2:3b
   ```
3. Start the Ollama server:
   ```bash
   ollama serve
   ```
4. In **LocalFileAudit**, navigate to **Settings** and ensure the Ollama Endpoint is set to `http://127.0.0.1:11434` and your chosen model is selected.
5. Visit the **AI Insights** tab to generate explainable narrative summaries.

---

## 🔒 Safety and Privacy Model

| Safety Guarantee | Implementation Mechanism |
| :--- | :--- |
| **No File Deletion** | Zero `File.Delete` or destructive APIs in scanning/analysis modules. |
| **No File Modification** | File streams are opened strictly with `FileAccess.Read` and `FileShare.Read`. |
| **No Remote Leakage** | `OllamaClient` enforces `uri.IsLoopback`; external URLs throw `InvalidOperationException`. |
| **Crash Resilience** | SQLite WAL mode checkpoints committed batches; `PRAGMA integrity_check` verified on restart. |
| **Prompt Injection Defense** | File metadata is passed as strictly bounded JSON data blocks with defensive prompts. |
| **Path Privacy** | Internal rolling file logs redact user directory structures in log messages. |
| **Atomic Reports** | Reports stage in `.staging_<guid>` and commit only after `Manifest.json` is verified. |

---

## 📚 Documentation

- [Architecture Guide](ARCHITECTURE.md) — Comprehensive technical design, bounded pipeline, and database schema.
- [Security Policy](SECURITY.md) — Security model, non-destructive guarantees, and vulnerability reporting.
- [Privacy Policy](PRIVACY.md) — Complete data handling details and loopback enforcement.
- [User Guide](USER_GUIDE.md) — Step-by-step instructions for running audits and reviewing results.
- [Third-Party Licenses](THIRD_PARTY_LICENSES.md) — Open-source dependencies and license notices.

---

## 🤝 Contributing & Bug Reports

Contributions and suggestions are welcome! Please open an issue or pull request on GitHub:
- **Issues:** [https://github.com/Beerkanwar/LocalFileAudit/issues](https://github.com/Beerkanwar/LocalFileAudit/issues)
- Please ensure all tests pass (`dotnet test LocalFileAudit.slnx`) before submitting PRs.

---

## 📄 License

LocalFileAudit is licensed under the [MIT License](LICENSE.txt).