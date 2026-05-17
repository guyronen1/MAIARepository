# MAIA AI Assistant System — Technical Design Document

**Version:** 1.0  
**Date:** 2026-05-01  
**Status:** Active Development

---

## Table of Contents

1. [System Overview](#1-system-overview)
2. [Architecture Layers](#2-architecture-layers)
3. [Component Interaction Diagram](#3-component-interaction-diagram)
4. [End-to-End Data Flow](#4-end-to-end-data-flow)
5. [Background Worker Pipeline](#5-background-worker-pipeline)
6. [Fix Execution Engine](#6-fix-execution-engine)
7. [Scan Strategies](#7-scan-strategies)
8. [Database Schema (ER Diagram)](#8-database-schema-er-diagram)
9. [API Endpoint Catalog](#9-api-endpoint-catalog)
10. [Angular Frontend Architecture](#10-angular-frontend-architecture)
11. [Dependency Injection Wiring](#11-dependency-injection-wiring)
12. [Non-Functional Requirements](#12-non-functional-requirements)

---

## 1. System Overview

MAIA AI Assistant is a backend-driven monitoring system that watches automated job pipelines (DTSX/SSIS), detects failures, classifies them, generates fix recommendations, and executes auto-heal actions — all governed by configurable rules stored in SQL Server.

```mermaid
graph LR
    JOBS["Job Pipelines\n(DTSX / SSIS)"]
    LOGS["Log Files\n& DB Tables"]
    ENGINE["MAIA AI Engine\n(Backend Services)"]
    DB[("SQL Server")]
    UI["Operator UI\n(Angular 20)"]
    FIXES["External Systems\n(APIs / SPs / Scripts)"]

    JOBS -->|produce| LOGS
    LOGS -->|scanned by| ENGINE
    ENGINE -->|reads/writes| DB
    ENGINE -->|triggers| FIXES
    UI -->|queries / configures| ENGINE
    UI -->|operator approvals| DB
```

---

## 2. Architecture Layers

The backend follows **Clean Architecture** — dependencies point inward only.

```mermaid
graph TD
    subgraph API ["AIEngineAPI (Presentation)"]
        CTR["Controllers\nDataController · ConfigController\nClassificationController · FixController\nPipelineController · JobScanController"]
        MW["GlobalExceptionHandler\n(RFC 7807 ProblemDetails)"]
    end

    subgraph APP ["Application (Use Cases)"]
        UC1["ClassifyJobsUseCase"]
        UC2["GenerateSuggestionsUseCase"]
        UC3["ExecuteFixesUseCase"]
        UC4["DirectoryPipelineUseCase"]
    end

    subgraph CORE ["Core (Domain)"]
        ENT["Entities\n(JobFailure · MonitoredJob · AiRecommendation\nClassificationRule · FixPolicyRule · …)"]
        IFACE["Interfaces\n(IJobRepository · IFixEngine\nIScanStrategy · IClassificationStrategy · …)"]
        ENUM["Enums\n(FixActionType · FixCategory\nJobStatus · ScanType · …)"]
    end

    subgraph INFRA ["Infrastructure (Data & Execution)"]
        REPO["Sql* Repositories\n(EF Core + SQL Server)"]
        SCAN["Scan Strategies\nFileSystem · Database · ApiEndpoint"]
        CLASS["RuleBasedClassifier\nDbFixCatalogue"]
        FIX["Fix Handlers & Executors\n(ApiCall · StoredProc · Script · Manual)"]
        WORK["Background Workers\nMonitoringWorker · AIClassifierWorker\nLogParserWorker · FixSuggestionWorker"]
    end

    API --> APP
    APP --> CORE
    INFRA --> CORE
    API --> CORE
```

---

## 3. Component Interaction Diagram

```mermaid
sequenceDiagram
    participant W  as MonitoringWorker
    participant SC as IScanStrategy
    participant JP as ILogParser
    participant CR as IClassificationStrategy
    participant GS as GenerateSuggestionsUseCase
    participant FE as IFixEngine
    participant DB as SQL Server
    participant OP as Operator UI

    loop Every 60 seconds
        W->>DB: GetActiveMonitoredJobs()
        loop Each active job
            W->>SC: ScanAsync(job)
            SC->>JP: ReadLines / QueryTable
            JP-->>SC: raw error lines
            SC->>DB: SaveJobFailures()
            W->>CR: ClassifyAsync(failures)
            CR->>DB: GetClassificationRules()
            CR-->>W: ClassificationResults
            W->>GS: ExecuteAsync(classifications)
            GS->>DB: GetFixPolicyRules()
            GS->>DB: SaveAiRecommendations()
            W->>FE: ExecuteAsync(autoApproved recs)
            FE->>DB: SaveFixExecutionLog + AuditLog
        end
    end

    OP->>DB: ApproveRecommendation(id)
    OP->>FE: POST /api/fix/execute-fixes
    FE->>DB: SaveFixExecutionLog + AuditLog
```

---

## 4. End-to-End Data Flow

```mermaid
flowchart TD
    A([Job Pipeline Runs]) --> B{Failure\nOccurs?}
    B -- No --> Z([Nothing to do])
    B -- Yes --> C[Scan Strategy detects error\nFileSystem / Database / ApiEndpoint]
    C --> D[JobFailure saved to DB\nSourceId · StepName · ErrorMessage · DetectedAt]
    D --> E[RuleBasedClassifier\nRegex match ClassificationRules]
    E --> F{Rule\nMatched?}
    F -- No --> G[JobFailure stays\nstatus = Failed / Unclassified]
    F -- Yes --> H[JobFailure updated\nJobTypeId + ErrorTypeId set]
    H --> I[GenerateSuggestionsUseCase\nLook up FixPolicyRule by ErrorTypeId]
    I --> J{FixPolicyRule\nfound?}
    J -- No --> K[DbFixCatalogue fallback\nstatic dictionary lookup]
    J -- Yes --> L[AiRecommendation saved\nSuggestedAction · FixCategory\nAutoFixAvailable = IsAutoHealEligible]
    K --> L
    L --> M{AutoFixAvailable\nOR OperatorApproved?}
    M -- No --> N[Recommendation waits\nfor operator action in UI]
    N --> O([Operator reviews in UI\nApproves fix or sets auto-heal])
    O --> M
    M -- Yes --> P[ExecuteFixesUseCase\nDispatch by ActionType]
    P --> Q{ActionType}
    Q -- ApiCall --> R1[ApiCallExecutor\nHTTP POST url/failureId]
    Q -- StoredProcedure --> R2[StoredProcedureExecutor\nEXEC SpName @FailureId]
    Q -- Script --> R3[ScriptExecutor\nProcess.Start — timeout 120s]
    Q -- Manual --> R4[ManualActionExecutor\nLog warning — return false]
    R1 & R2 & R3 & R4 --> S[Save FixExecutionLog\nSave AuditLog]
    S --> T{Success?}
    T -- Yes --> U[JobFailure → Resolved]
    T -- No --> V[JobFailure → ManualRequired]
```

---

## 5. Background Worker Pipeline

```mermaid
flowchart LR
    subgraph Workers
        MW["MonitoringWorker\n(60s tick)"]
        ACW["AIClassifierWorker"]
        LPW["LogParserWorker"]
        FSW["FixSuggestionWorker"]
    end

    subgraph UseCases
        DPU["DirectoryPipelineUseCase"]
        CJU["ClassifyJobsUseCase"]
        GSU["GenerateSuggestionsUseCase"]
        EFU["ExecuteFixesUseCase"]
    end

    MW -->|drives full pipeline| DPU
    DPU --> CJU
    CJU --> GSU
    GSU --> EFU

    ACW -->|runs classify standalone| CJU
    LPW -->|runs log scan standalone| DPU
    FSW -->|runs suggestions standalone| GSU
```

---

## 6. Fix Execution Engine

```mermaid
flowchart TD
    START([ExecuteFixesUseCase]) --> QUERY[Query AiRecommendations\nIsExecuted=false\nAND AutoFix OR OperatorApproved]
    QUERY --> LOOP{For each\nrecommendation}
    LOOP --> POLICY[IFixPolicyRepository\nGetByErrorTypeIdAsync]
    POLICY --> FOUND{Policy\nfound &\nenabled?}

    FOUND -- Yes --> DISPATCH{ActionType}
    DISPATCH -- ApiCall --> E1["ApiCallExecutor\nHTTP POST {url}/{failureId}\nvia named HttpClient 'FixEngine'"]
    DISPATCH -- StoredProcedure --> E2["StoredProcedureExecutor\nEXEC {SpName} @FailureId"]
    DISPATCH -- Script --> E3["ScriptExecutor\nProcess.Start(exe, args)\ntimeout 120s · exitCode 0 = OK"]
    DISPATCH -- Manual --> E4["ManualActionExecutor\nLog warning · return false"]

    FOUND -- No --> FALLBACK{Fallback by\nFixCategory}
    FALLBACK -- Retry --> F1[RetryFixHandler]
    FALLBACK -- FileRepair --> F2[FileRepairFixHandler]
    FALLBACK -- DbFix --> F3[DbFixHandler]
    FALLBACK -- Manual --> F4[ManualFixHandler]

    E1 & E2 & E3 & E4 & F1 & F2 & F3 & F4 --> LOG[Save FixExecutionLog\nSave AuditLog]
    LOG --> STATUS{Execution\nResult}
    STATUS -- Success --> RES[Failure → Resolved\nRecommendation → IsExecuted=true]
    STATUS -- Failure --> MAN[Failure → ManualRequired]
    RES & MAN --> LOOP
```

---

## 7. Scan Strategies

```mermaid
flowchart LR
    MJ["MonitoredJob\n(ScanType config)"] --> FACTORY{ScanType}

    FACTORY -- FileSystem --> FS["FileSystemScanStrategy\nDirectory.EnumerateFiles(LogFolder, SearchPatterns)\nILogParser extracts error lines\nScanFileWatermark tracks position"]

    FACTORY -- Database --> DB["DatabaseScanStrategy\nQuery SourceTable WHERE Id > watermark\nApply ScanCheckRules: Min/Max/Expected\nScanDbWatermark tracks last processed Id"]

    FACTORY -- ApiEndpoint --> API["ApiEndpointScanStrategy\nHTTP GET LogSourceUrl\nParse response for error signals"]

    FS & DB & API --> FAIL["JobFailure records\nsaved to DB"]
```

---

## 8. Database Schema (ER Diagram)

```mermaid
erDiagram
    JobTypes {
        int JobTypeId PK
        string Name
        string Code
    }
    ErrorTypes {
        int ErrorTypeId PK
        string Name
        string Code
    }
    ClassificationRules {
        int RuleId PK
        string Pattern
        decimal Confidence
        int Priority
        int JobTypeId FK
        int ErrorTypeId FK
    }
    MonitoredJobs {
        int MonitoredJobId PK
        string Name
        string DisplayName
        int JobTypeId FK
        int ScanTypeId
        string LogFolder
        string SearchPatterns
        string ConnectionName
        string LogSourceUrl
        int PollingIntervalSeconds
        bool IsActive
        datetime CreatedAt
    }
    MonitoredJobRules {
        int Id PK
        int MonitoredJobId FK
        int RuleId FK
        bool IsActive
    }
    ScanCheckRules {
        int CheckRuleId PK
        int MonitoredJobId FK
        string CheckType
        string SourceTable
        string TargetField
        decimal MinValue
        decimal MaxValue
        string ExpectedValue
        string WatermarkColumn
        string Severity
        bool IsActive
    }
    JobFailures {
        int FailureId PK
        int MonitoredJobId FK
        int JobTypeId FK
        int ErrorTypeId FK
        string SourceId
        string StepName
        string ErrorMessage
        string Status
        datetime DetectedAt
    }
    AiRecommendations {
        int RecommendationId PK
        int FailureId FK
        string SuggestedAction
        string FixCategory
        decimal ConfidenceScore
        bool AutoFixAvailable
        bool OperatorApproved
        bool IsExecuted
        datetime RecommendedAt
    }
    FixPolicyRules {
        int PolicyId PK
        int JobTypeId FK
        int ErrorTypeId FK
        string ActionType
        string ActionPayload
        bool IsAutoHealEligible
        bool Enabled
    }
    FixExecutionLog {
        int LogId PK
        int RecommendationId FK
        bool Success
        string TriggerType
        string Notes
        datetime ExecutedAt
    }
    OperatorActions {
        int ActionId PK
        int RecommendationId FK
        string ActionTaken
        string Notes
        datetime ActionAt
    }
    AuditLog {
        int AuditId PK
        string EntityName
        string EntityId
        string Action
        string Details
        datetime Timestamp
    }
    ScanDbWatermarks {
        int Id PK
        int MonitoredJobId FK
        string TableName
        long LastProcessedId
    }
    ScanFileWatermarks {
        int Id PK
        int MonitoredJobId FK
        string FilePath
        long LastPosition
    }

    JobTypes ||--o{ ClassificationRules : "classified by"
    ErrorTypes ||--o{ ClassificationRules : "classified by"
    JobTypes ||--o{ MonitoredJobs : "typed as"
    MonitoredJobs ||--o{ MonitoredJobRules : "has"
    ClassificationRules ||--o{ MonitoredJobRules : "used by"
    MonitoredJobs ||--o{ ScanCheckRules : "checked by"
    MonitoredJobs ||--o{ JobFailures : "produces"
    JobTypes ||--o{ JobFailures : "typed as"
    ErrorTypes ||--o{ JobFailures : "classified as"
    JobFailures ||--o{ AiRecommendations : "triggers"
    AiRecommendations ||--o{ FixExecutionLog : "logged in"
    AiRecommendations ||--o{ OperatorActions : "actioned by"
    JobTypes ||--o{ FixPolicyRules : "governs"
    ErrorTypes ||--o{ FixPolicyRules : "governs"
    MonitoredJobs ||--o{ ScanDbWatermarks : "tracks"
    MonitoredJobs ||--o{ ScanFileWatermarks : "tracks"
```

---

## 9. API Endpoint Catalog

### DataController — Read-only dashboard queries

| Method | Route | Description | Response |
|--------|-------|-------------|----------|
| GET | `/api/data/failures` | Paginated job failures | `PagedResult<JobFailureDto>` |
| GET | `/api/data/failures/{id}/status` | Failure detail + recommendations | Failure status object |
| GET | `/api/data/recommendations` | Paginated AI recommendations | `PagedResult<RecommendationDto>` |
| GET | `/api/data/monitored-jobs` | All active monitored jobs with rules | `MonitoredJobDto[]` |

### ConfigController — Operator configuration

| Method | Route | Description |
|--------|-------|-------------|
| GET/POST/PUT/DELETE | `/api/config/monitored-jobs` | CRUD for MonitoredJob |
| GET/POST/PUT/DELETE | `/api/config/scan-check-rules` | CRUD for ScanCheckRules |
| GET/POST/PUT/DELETE | `/api/config/classification-rules` | CRUD for ClassificationRules |
| GET/POST/PUT/DELETE | `/api/config/fix-policy-rules` | CRUD for FixPolicyRules |

### Processing Controllers

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/classification/classify` | Run classification on pending failures |
| POST | `/api/fix/execute-fixes` | Execute approved/auto-heal fixes |
| POST | `/api/pipeline/run-pipeline` | Run full directory pipeline |
| POST | `/api/process/process` | Full pipeline via all use cases |
| POST | `/api/logparser/parse` | Parse a log file on demand |

---

## 10. Angular Frontend Architecture

```mermaid
graph TD
    subgraph Shell ["Shell Layout"]
        TB["TopBarComponent"]
        SM["SideMenuComponent"]
        RO["<router-outlet>"]
    end

    subgraph Features ["Lazy-Loaded Feature Components"]
        DASH["DashboardComponent\n/dashboard"]
        FAIL["FailuresListComponent\n/failures"]
        FDET["FailureDetailComponent\n/failures/:id"]
        REC["RecommendationsComponent\n/recommendations\n/operator-actions"]
        SCAN["ScanJobsComponent\n/scan-jobs"]
        CFG["MonitoredJobsComponent\n/config/monitored-jobs\n/config/classification-rules"]
    end

    subgraph Services ["Core Services (providedIn: root)"]
        FS["FailuresService\nGET /data/failures\nGET /data/failures/:id/status"]
        RS["RecommendationsService\nGET /data/recommendations"]
        MJS["MonitoredJobsService\nGET /data/monitored-jobs\n[CRUD — in progress]"]
        SS["ScanService"]
        CS["ConfigService"]
    end

    subgraph Models ["Core Models"]
        MF["MonitoredJob\nScanCheckRule\nRuleOverride"]
        FF["JobFailure"]
        RF["Recommendation"]
    end

    RO --> DASH & FAIL & FDET & REC & SCAN & CFG
    FAIL --> FS
    FDET --> FS & RS
    REC --> RS
    CFG --> MJS & CS
    Services --> Models
```

---

## 11. Dependency Injection Wiring

```mermaid
graph LR
    subgraph "Program.cs"
        H1["AddHttpClient('FixEngine')"]
        H2["AddMaiaAI(connStr)"]
        H3["AddApplicationServices()"]
        H4["AddGlobalExceptionHandling()"]
    end

    subgraph "AddMaiaAI"
        R1["9× Sql* Repositories"]
        R2["RuleBasedClassifier\n→ IClassificationStrategy"]
        R3["DbFixCatalogue\n→ IFixCatalogue"]
        R4["DefaultFixEngine\n→ IFixEngine"]
        R5["4× Fix Handlers\n→ IFixHandler"]
        R6["4× Fix Executors\n→ IFixActionExecutor"]
        R7["3× Scan Strategies\n→ IScanStrategy"]
        R8["SimpleLogParser, FileLogReader"]
        R9["MonitoringWorker (Hosted)"]
    end

    subgraph "AddApplicationServices"
        A1["ClassifyJobsUseCase"]
        A2["GenerateSuggestionsUseCase"]
        A3["ExecuteFixesUseCase"]
        A4["DirectoryPipelineUseCase"]
    end

    H2 --> R1 & R2 & R3 & R4 & R5 & R6 & R7 & R8 & R9
    H3 --> A1 & A2 & A3 & A4
```

---

## 12. Non-Functional Requirements

| Concern | Approach |
|---------|----------|
| **Reliability** | MonitoringWorker restarts on crash (hosted service); AuditLog is append-only |
| **Offline support** | Rules and config stored locally in SQL Server; no cloud dependency at runtime |
| **Scalability** | Worker projects (AIClassifierWorker etc.) can run as separate services/containers |
| **Auditability** | Every fix execution writes to `FixExecutionLog` and immutable `AuditLog` |
| **Error handling** | `GlobalExceptionHandler` returns RFC 7807 ProblemDetails on all unhandled exceptions |
| **Extensibility** | New scan types: implement `IScanStrategy`. New fix types: implement `IFixActionExecutor` |
| **Incremental scanning** | `ScanDbWatermarks` / `ScanFileWatermarks` prevent re-processing already-seen records |
| **Confidence scoring** | ClassificationRules carry `Confidence` (0–1); recommendations inherit the score |
