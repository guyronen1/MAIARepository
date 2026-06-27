export interface AuditLogEntry {
  auditId: number;
  failureId: number | null;
  entityType: string | null;
  entityId: string | null;
  eventType: string;
  actor: string;
  detail: string | null;
  timestamp: string;
}

export interface AuditLogPage {
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
  items: AuditLogEntry[];
}
