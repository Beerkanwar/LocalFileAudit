# Security Policy & Safety Model 🛡️

The design and development of **LocalFileAudit** are strictly governed by principles of safety, privacy, and non-destructive execution.

---

## 1. Security Architecture & Threat Model

LocalFileAudit operates under a defense-in-depth security model:

```
┌────────────────────────────────────────────────────────┐
│                   LocalFileAudit App                   │
├────────────────────────────────────────────────────────┤
│ • Execution Level: asInvoker (Standard User)           │
│ • Filesystem Operations: Read-Only (FileShare.Read)    │
│ • Network Boundaries: Loopback Only (127.0.0.1)        │
│ • AI Prompt Boundaries: Data Isolation & Reconciliation│
└────────────────────────────────────────────────────────┘
```

### 1.1 Read-Only Safety Guarantees
- **No File Mutation:** Scanning and duplicate hashing use `FileAccess.Read` and `FileShare.Read`. The application codebase does not include file deletion, modification, or movement APIs on user files.
- **Code Audit:** All `File.Move` and temporary `File.Delete` operations in the codebase are strictly restricted to application-owned configuration files (`config.json`), temporary report staging directories (`.staging_<guid>`), archive planner state (`.staging_plan_<guid>.tmp`), and archive verification staging exports (`.staging_verify_<guid>.tmp`). The application contains zero destructive APIs targeting user files.
- **No Automatic Actions:** When duplicates or cleanup candidates are identified, LocalFileAudit provides advisory reports only. File manipulation must be manually performed by the user using Windows Explorer or administrative shell commands.

### 1.2 Network Isolation & Loopback Enforcement
- **Zero Remote Telemetry:** The application makes **zero** outbound network requests to external servers, cloud providers, or analytics services.
- **Loopback Validation:** By default (`AI.RequireLocalEndpoint = true`), the `OllamaClient` strictly enforces loopback verification on the AI endpoint URL:
  ```csharp
  if (_configurationService.Current.AI.RequireLocalEndpoint)
  {
      if (!uri.IsLoopback && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
      {
          throw new InvalidOperationException("Remote AI endpoints are strictly prohibited for privacy safety.");
      }
  }
  ```
  Attempting to configure a remote hostname or external IP without intentionally changing configuration triggers an immediate `InvalidOperationException`. Users can explicitly configure a remote server only if desired, but the default is safe and loopback-only.

### 1.3 Least Privilege Model
- **No Administrator Elevation:** LocalFileAudit does not request elevation. The application manifest (`app.manifest`) declares:
  ```xml
  <requestedExecutionLevel level="asInvoker" uiAccess="false" />
  ```
- **Permission Boundary:** If an inaccessible path (such as `System Volume Information`) is encountered during traversal, the scanner logs an `UnauthorizedAccessException` to the `ScanErrors` table and continues scanning remaining directories without crashing or elevating privileges.

### 1.4 AI Prompt Injection Defense
- **Untrusted Metadata Framing:** File names, extensions, and directory paths on user drives may contain adversarial text designed to manipulate Large Language Models. LocalFileAudit treats all filesystem strings as untrusted data.
- **Defensive Prompt Construction:** System prompts explicitly instruct the AI model to treat metadata strictly as text records, ignore instructions contained within file paths, and output structured JSON conforming to a strict schema.
- **Candidate Reconciliation:** The application validates every recommended item in the AI response against real candidate IDs in the SQLite database. Any hallucinated ID or recommendation without an exact match is discarded.
- **Zero Command Execution:** AI recommendations are never passed to `Process.Start`, `cmd.exe`, or `powershell.exe`. They are presented as pure text in the UI and Markdown exports.

---

## 2. Reporting a Vulnerability

If you discover a security vulnerability or safety issue in LocalFileAudit:

1. **Do not create a public GitHub issue.**
2. Send an email to the repository maintainer or open a GitHub Security Advisory via the repository's "Security" tab.
3. Include detailed steps to reproduce the issue and expected vs. actual behavior.
4. Vulnerability reports will be acknowledged within 48 hours, and a patch will be prioritized.

---

## 3. Verified Security Acceptance Criteria

LocalFileAudit has passed all 10 safety acceptance criteria specified in Section 16.5 of the engineering development specification:

- [x] **Criterion 1:** No source file is created, deleted, moved, renamed, or modified by scanning.
- [x] **Criterion 2:** Forced termination does not damage the database beyond recoverable incomplete work.
- [x] **Criterion 3:** AI failure does not prevent deterministic reports.
- [x] **Criterion 4:** No remote service receives inventory data.
- [x] **Criterion 5:** No administrator permission is required.
- [x] **Criterion 6:** The application never executes AI-generated commands.
- [x] **Criterion 7:** Every candidate has an explainable reason for its classification.
- [x] **Criterion 8:** Incomplete scans and inaccessible paths are clearly reported.
- [x] **Criterion 9:** No automatic deletion or movement exists.
- [x] **Criterion 10:** Report generation cannot overwrite a previously successful report with partial content.
