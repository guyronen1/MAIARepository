# MAIA AI Assistant System — Manager Specification Document

**Version:** 1.0  
**Date:** 2026-05-01  
**Audience:** Product Owners, Project Managers, Stakeholders

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Business Problem](#2-business-problem)
3. [Solution Overview](#3-solution-overview)
4. [Key Stakeholders & User Roles](#4-key-stakeholders--user-roles)
5. [System Capabilities](#5-system-capabilities)
6. [Operator Workflow](#6-operator-workflow)
7. [Auto-Heal Workflow](#7-auto-heal-workflow)
8. [Configuration Management Workflow](#8-configuration-management-workflow)
9. [Feature Breakdown](#9-feature-breakdown)
10. [Current Status & Roadmap](#10-current-status--roadmap)
11. [Risk & Mitigation](#11-risk--mitigation)
12. [Glossary](#12-glossary)

---

## 1. Executive Summary

**MAIA AI Assistant** is an intelligent monitoring and auto-healing platform for automated job pipelines (DTSX/SSIS). It continuously watches for failures, classifies them by type, generates fix recommendations, and either resolves them automatically or routes them to an operator for approval — all through a web-based dashboard.

**Key outcomes it delivers:**
- Faster incident detection (seconds vs. hours of manual log review)
- Reduced manual intervention through configurable auto-heal rules
- Full audit trail for every action taken — manual or automated
- Operator self-service: configure monitoring rules without touching the database

---

## 2. Business Problem

```mermaid
flowchart LR
    A["Job Pipeline Fails\n(DTSX / SSIS)"] --> B["Operator manually\nreviews log files"]
    B --> C["Hours pass before\nfailure is noticed"]
    C --> D["Manual diagnosis\n(no classification)"]
    D --> E["Manual fix attempt\n(no audit trail)"]
    E --> F["Downtime &\noperational cost"]
    
    style A fill:#ff6b6b,color:#fff
    style F fill:#ff6b6b,color:#fff
```

**Pain points:**
- Log files are large, spread across directories, and require expert knowledge to diagnose.
- No consistent classification of failure types across jobs.
- Fixes are applied manually with no record of what was done or why.
- Recurrent failures are not automatically recognized as candidates for auto-healing.
- Configuring monitoring requires direct database access.

---

## 3. Solution Overview

```mermaid
flowchart TD
    subgraph "MAIA AI Assistant System"
        MONITOR["Automatic Monitor\nScans logs & DB every 60s"]
        CLASSIFY["Smart Classifier\nMatches failures to known error types"]
        RECOMMEND["AI Recommender\nSuggests the right fix action"]
        EXECUTE["Auto-Healer\nExecuts approved fixes automatically"]
        UI["Operator Dashboard\nWeb UI for review, approval & config"]
        AUDIT["Audit Trail\nEvery action recorded immutably"]
    end

    JOBS["Job Pipelines"] -->|failures| MONITOR
    MONITOR --> CLASSIFY
    CLASSIFY --> RECOMMEND
    RECOMMEND -->|auto-approved| EXECUTE
    RECOMMEND -->|needs review| UI
    UI -->|operator approves| EXECUTE
    EXECUTE --> AUDIT
    UI -->|configure rules| MONITOR
    UI -->|configure rules| CLASSIFY
    UI -->|configure rules| EXECUTE
```

**In one sentence:** MAIA AI watches your pipelines, understands what went wrong, knows how to fix it, and either fixes it automatically or tells the operator exactly what to do.

---

## 4. Key Stakeholders & User Roles

| Role | Who They Are | What They Do in MAIA AI |
|------|-------------|------------------------|
| **Operator** | Data engineer / support engineer | Reviews failures, approves recommendations, sets auto-heal rules |
| **Admin / Config Manager** | Senior engineer or team lead | Configures which jobs to monitor, defines classification rules, sets fix policies |
| **System (Auto)** | MAIA AI itself | Runs scans, classifies, generates recommendations, executes auto-heal actions |
| **Auditor** | Compliance / management | Reads the audit log to verify what actions were taken and when |

---

## 5. System Capabilities

```mermaid
mindmap
  root((MAIA AI\\nAssistant))
    Monitoring
      FileSystem scan
      Database table scan
      API endpoint scan
      Configurable polling interval
      Incremental watermark tracking
    Classification
      Regex-based rule matching
      Per-job rule overrides
      Confidence scoring
      Job type + error type tagging
    Recommendations
      Rule-based fix suggestions
      Confidence scores
      Auto-heal eligibility flag
      Operator approval flow
    Fix Execution
      HTTP API call
      Stored procedure
      Script execution
      Manual instruction
      Fallback handlers
    Operator UI
      Failure dashboard
      Recommendation review
      Auto-heal toggle
      Job configuration
      Rule management
    Audit & Compliance
      Immutable audit log
      Fix execution log
      Operator action history
```

---

## 6. Operator Workflow

This is the primary day-to-day workflow for an operator responding to a pipeline failure.

```mermaid
flowchart TD
    START([Operator opens Dashboard]) --> CHECK{New failures\nor alerts?}
    CHECK -- No --> DONE([Monitor continues])
    CHECK -- Yes --> LIST[View Failures List\nSorted by severity & time]
    LIST --> SELECT[Select a failure\nto investigate]
    SELECT --> DETAIL[Failure Detail View\nErrorMessage · StepName · DetectedAt\nErrorType · MonitoredJob name]
    DETAIL --> RECS{Recommendations\nAvailable?}
    RECS -- No --> MANUAL[Operator investigates\nand resolves manually]
    RECS -- Yes --> REVIEW[Review Recommendations\nSuggestedAction · FixCategory\nConfidence score]
    REVIEW --> DECISION{Operator\nDecision}
    DECISION -- Approve\none-time --> APPROVE[Click Approve\nFix executes immediately]
    DECISION -- Set Auto-Heal --> AUTOHEAL[Toggle Auto-Heal ON\nFix runs automatically\nnext time this error occurs]
    DECISION -- Reject --> REJECT[Mark as Manual\nAdd operator note]
    APPROVE & AUTOHEAL --> RESULT{Fix\nSucceeded?}
    RESULT -- Yes --> RESOLVED[Failure marked Resolved\nAudit log updated]
    RESULT -- No --> ESCALATE[Failure marked ManualRequired\nOperator notified]
    REJECT --> MANUAL
    MANUAL --> DONE
    RESOLVED --> DONE
    ESCALATE --> MANUAL
```

---

## 7. Auto-Heal Workflow

How MAIA AI handles failures without any human intervention, once auto-heal rules are set.

```mermaid
flowchart LR
    subgraph "Background — runs every 60s"
        S1["Scan: read logs\nor query DB tables"]
        S2["Detect new\nerror lines"]
        S3["Match against\nClassification Rules"]
        S4["Generate\nRecommendation"]
        S5{"Is Auto-Heal\neligible?"}
        S6["Execute fix\nautomatically"]
        S7["Save result\nto Audit Log"]
        S8["Failure → Resolved"]
        S9["Wait for\nOperator approval"]
    end

    S1 --> S2 --> S3 --> S4 --> S5
    S5 -- Yes --> S6 --> S7 --> S8
    S5 -- No --> S9
```

**Auto-Heal is enabled when:**
- An operator has previously approved a recommendation and toggled "Set as Auto-Heal", OR
- The Fix Policy Rule for that error type has `IsAutoHealEligible = true` configured by an admin.

---

## 8. Configuration Management Workflow

How an admin or config manager sets up a new job for monitoring.

```mermaid
flowchart TD
    START([Admin logs into UI]) --> NAV[Navigate to\nConfig → Monitored Jobs]
    NAV --> NEW[Click 'Add New Job']
    NEW --> FORM1[Fill in Job Details\nName · JobType · Description]
    FORM1 --> SCANTYPE{Select Scan Type}

    SCANTYPE -- FileSystem --> FS[Set Log Folder path\nSet search patterns e.g. *.log\nSet polling interval]
    SCANTYPE -- Database --> DB[Set DB connection name\nSet source table]
    SCANTYPE -- ApiEndpoint --> API[Set endpoint URL\nSet polling interval]

    FS & DB & API --> RULES[Add Check Rules\nWhat conditions count as a failure?\ne.g. COUNT < 100 · value out of range · field mismatch]

    RULES --> CLASSRULES[Assign Classification Rules\nWhich regex patterns map to which error types?\nSet confidence & priority per rule]

    CLASSRULES --> FIXPOLICY[Set Fix Policy\nFor each error type:\nWhat action? API call · Stored proc · Script · Manual\nIs it auto-heal eligible?]

    FIXPOLICY --> SAVE[Save configuration]
    SAVE --> ACTIVE[MonitoringWorker picks up\nnew job on next tick]
    ACTIVE --> MONITOR([Job is now actively monitored])
```

---

## 9. Feature Breakdown

### 9.1 Monitoring

| Feature | Description | Status |
|---------|-------------|--------|
| FileSystem scan | Reads log files from a folder, extracts error lines | ✅ Built |
| Database scan | Queries DB tables, applies check rules (min/max/expected) | ✅ Built |
| API endpoint scan | Polls an HTTP endpoint for error signals | ✅ Built |
| Incremental watermarks | Prevents re-processing already-scanned records/files | ✅ Built |
| Configurable polling interval | Per-job setting (seconds) | ✅ Built |

### 9.2 Classification

| Feature | Description | Status |
|---------|-------------|--------|
| Regex rule matching | Matches error text against configured patterns | ✅ Built |
| Per-job rule overrides | Each monitored job can have its own classification rules | ✅ Built |
| Confidence scoring | Rules carry a confidence value (0.0–1.0) | ✅ Built |
| Job type & error type tagging | Failures tagged with structured JobType + ErrorType | ✅ Built |

### 9.3 Recommendations & Fix Execution

| Feature | Description | Status |
|---------|-------------|--------|
| Rule-based recommendations | Fix suggestions from FixPolicyRules DB table | ✅ Built |
| Static fallback catalogue | Built-in dictionary when no DB rule exists | ✅ Built |
| HTTP API call executor | Calls external REST endpoint with failureId | ✅ Built |
| Stored procedure executor | Runs SQL stored procedure with failureId | ✅ Built |
| Script executor | Runs command-line script, 120s timeout | ✅ Built |
| Manual action handler | Logs instruction for operator, no auto-execution | ✅ Built |

### 9.4 Operator UI

| Feature | Description | Status |
|---------|-------------|--------|
| Failures dashboard | Paginated list of all job failures with status indicators | ✅ Built |
| Failure detail view | Full context + linked recommendations | ✅ Built |
| Recommendations screen | Review AI suggestions, approve or reject | ✅ Built |
| Operator actions history | Log of operator decisions | ✅ Built |
| Scan jobs view | Monitor active scan jobs | ✅ Built |
| MonitoredJob config UI | Add/edit monitored jobs from the browser | 🔄 In Progress |
| Check rules config UI | Configure ScanCheckRules per job | 🔄 In Progress |
| Classification rules config UI | Assign regex rules to jobs from UI | 🔄 In Progress |
| Fix policy rules config UI | Configure fix actions and auto-heal per error type | 🔄 In Progress |
| Auto-heal toggle on recommendations | Operator sets a recommendation as auto-heal for next time | 🔄 In Progress |

---

## 10. Current Status & Roadmap

```mermaid
gantt
    title MAIA AI Development Roadmap
    dateFormat  YYYY-MM-DD
    section Backend
    Core domain & entities          :done,    b1, 2025-01-01, 2025-03-01
    Infrastructure & repositories   :done,    b2, 2025-02-01, 2025-04-01
    Scan strategies (FS/DB/API)     :done,    b3, 2025-03-01, 2025-05-01
    Fix engine & executors          :done,    b4, 2025-04-01, 2025-06-01
    API controllers                 :done,    b5, 2025-05-01, 2025-07-01
    Config CRUD endpoints           :active,  b6, 2026-04-01, 2026-05-15

    section Frontend
    Shell layout & routing          :done,    f1, 2025-10-01, 2025-11-01
    Failures & recommendations UI   :done,    f2, 2025-11-01, 2026-01-01
    Scan jobs UI                    :done,    f3, 2026-01-01, 2026-02-01
    MonitoredJob config forms       :active,  f4, 2026-04-01, 2026-05-20
    Check rules & classification UI :active,  f5, 2026-04-15, 2026-05-25
    Auto-heal toggle on recs UI     :active,  f6, 2026-05-01, 2026-05-20
    Fix policy rules UI             :         f7, 2026-05-15, 2026-06-01
```

### Sprint Focus (May 2026)

```mermaid
flowchart LR
    P1["MonitoredJob\nCRUD Forms\n(Angular UI)"] --> P2["ScanCheckRules\nconfig per job"]
    P2 --> P3["ClassificationRules\nassignment per job"]
    P3 --> P4["Auto-heal toggle\non Recommendations screen"]
    P4 --> P5["FixPolicyRules\nconfig UI"]

    style P1 fill:#4CAF50,color:#fff
    style P2 fill:#4CAF50,color:#fff
    style P3 fill:#2196F3,color:#fff
    style P4 fill:#2196F3,color:#fff
    style P5 fill:#9E9E9E,color:#fff
```

**Legend:** Green = in progress · Blue = next · Grey = upcoming

---

## 11. Risk & Mitigation

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Auto-heal executes wrong fix | Medium | High | Confidence threshold before auto-heal; full AuditLog; operator can review after |
| False-positive failure detection | Medium | Medium | Tune regex ClassificationRules; confidence scores filter low-quality matches |
| DB connection failure during scan | Low | Medium | Watermarks ensure retry on next tick; GlobalExceptionHandler logs all errors |
| Script executor times out | Low | Low | 120-second timeout; failure logged; job marked ManualRequired |
| Operator misconfigures rules | Medium | Medium | UI validation; preview of rule matches before save; audit trail tracks all changes |
| External API call for fix fails | Low | Medium | FixExecutionLog captures failure; job marked ManualRequired for fallback |

---

## 12. Glossary

| Term | Definition |
|------|-----------|
| **MonitoredJob** | A configured job pipeline that MAIA AI watches. Has a scan type, polling interval, and associated rules. |
| **ScanCheckRule** | A condition that MAIA AI evaluates during a DB scan (e.g. "row count must be > 100"). |
| **ClassificationRule** | A regex pattern that, when matched against an error message, assigns a JobType and ErrorType to the failure. |
| **JobFailure** | A detected failure instance — one error event from a pipeline, saved with its context. |
| **AiRecommendation** | A suggested fix action generated for a JobFailure, with confidence score and auto-heal eligibility. |
| **FixPolicyRule** | An admin-configured rule: for ErrorType X on JobType Y, run this action (API call, stored proc, script, or manual). |
| **Auto-Heal** | The ability for MAIA AI to execute a fix automatically without operator approval, governed by `IsAutoHealEligible`. |
| **OperatorAction** | A human decision recorded in the system — approve, reject, or set auto-heal on a recommendation. |
| **FixExecutionLog** | The result record of a fix attempt: success/failure, timestamp, trigger type (auto or manual). |
| **AuditLog** | An immutable append-only log of all significant actions in the system, for compliance. |
| **Watermark** | A saved position (file byte offset or DB row ID) that allows incremental scanning without reprocessing old data. |
| **Confidence Score** | A 0.0–1.0 value on a ClassificationRule or Recommendation indicating how certain the match or suggestion is. |
| **FixCategory** | A broad category of fix type: Retry, FileRepair, DbFix, or Manual — used as a fallback when no FixPolicyRule exists. |
| **DTSX** | The file format for SQL Server Integration Services (SSIS) packages — the job type this system primarily monitors. |
