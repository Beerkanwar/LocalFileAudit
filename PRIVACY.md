# Privacy Policy & Data Guarantees 🔒

LocalFileAudit is designed from the ground up as a **100% private, local-first utility**. Your files and file inventory belong exclusively to you.

---

## 1. Absolute Privacy Guarantees

- **No Remote Telemetry:** LocalFileAudit does not collect analytics, crash telemetry, user identifiers, or usage statistics.
- **No Cloud Dependencies:** All indexing, querying, duplicate hashing, and deterministic reporting take place entirely on your device.
- **Zero Internet Access:** The application requires no internet connection to operate.
- **Loopback-Only AI:** By default configuration (`AI.RequireLocalEndpoint = true`), requests are strictly restricted to local loopback endpoints (`127.0.0.1` or `localhost`). Remote endpoints like OpenAI, Anthropic, or external instances are rejected at the client validation layer.

---

## 2. Data Minimization Principles

LocalFileAudit adheres strictly to data minimization:

| Data Type | Collected | Transmitted Over Network | Stored On Disk |
| :--- | :--- | :--- | :--- |
| **File Contents** | ❌ Never | ❌ Never | ❌ Never |
| **File Paths & Names** | ✅ Yes (during scan) | ❌ Never (local only) | ✅ Yes (`inventory.db`) |
| **File Sizes & Dates** | ✅ Yes (during scan) | ❌ Never (local only) | ✅ Yes (`inventory.db`) |
| **Cryptographic Hashes** | ✅ Yes (on duplicate check)| ❌ Never (local only) | ✅ Yes (`inventory.db`) |
| **AI Prompt Metadata** | ✅ Candidate summary | ✅ Only to local Ollama (`127.0.0.1`) by default (or user-configured private LAN Ollama server)| ✅ Yes (`AI-Review.md`) |

When interacting with the local Ollama instance:
- **File contents are NEVER transmitted.**
- Only compact candidate metadata (file name, extension, size in MB, immediate parent folder name, and aggregated hotspot summaries) is provided to explain storage distribution. When path redaction is enabled, file names are masked as `candidate_{id}.ext` and directory structures are masked to prevent personal information leakage.
- Prompts are bounded to compact candidate items (up to `MaxPayloadCandidates`, default 100) to keep memory usage minimal and prevent prompt truncation.

---

## 3. Local Storage Locations

All application data is stored locally in the standard Windows user profile directory:

```
%LOCALAPPDATA%\LocalFileAudit\
├── config\
│   └── config.json              (Local application configuration)
├── data\
│   ├── inventory.db             (SQLite database with WAL journal)
│   └── backups\                 (Online snapshot backups)
├── logs\
│   └── app-YYYYMMDD.log         (Rolling local diagnostic logs, bounded to 10 MB)
└── reports\
    └── scan-<guid>-<timestamp>\ (Exported Markdown, HTML, and CSV reports)
```

You can view or purge any of these folders at any time directly through the **Settings** view in the application.

---

## 4. Log Privacy & Path Redaction

Local log files generated in `%LOCALAPPDATA%\LocalFileAudit\logs` are rotated daily and bounded in size (10 MB maximum per file). To protect user privacy, the rolling logger strips raw newlines to prevent log injection and applies redaction to minimize logging personal file system paths in diagnostic messages.

---

## 5. Contact

For privacy inquiries or technical verification, please open a GitHub Discussion or Issue in the [LocalFileAudit repository](https://github.com/Beerkanwar/LocalFileAudit).
