import { api } from "./client";
import { cleanParams, downloadFile } from "./http";

/** Spec 8.4: access reports and bulk operations. */
export type AccessReportKind =
  "check" | "user-access" | "external" | "links" | "unique" | "expiring";

export interface AccessReportParams {
  kind: AccessReportKind;
  workspaceId?: string | null;
  userId?: string | null;
  groupId?: string | null;
  itemId?: string | null;
  days?: number;
}

export interface AccessReportRow {
  itemId: string | null;
  workspaceId: string | null;
  cells: (string | null)[];
}

export interface AccessReport {
  kind: AccessReportKind;
  title: string;
  columns: string[];
  rows: AccessReportRow[];
  truncated: boolean;
  summary: string | null;
}

export interface ReportItem {
  id: string;
  name: string;
  kind: "space" | "drive" | "library" | "folder" | "file" | "shortcut";
  workspaceId: string | null;
  workspaceName: string | null;
}

export async function runAccessReport(
  p: AccessReportParams,
): Promise<AccessReport> {
  const { data } = await api.get<AccessReport>(`/admin/reports/${p.kind}`, {
    params: cleanParams({ ...p, kind: undefined }),
  });
  return data;
}

export function exportAccessReport(
  p: AccessReportParams,
  format: "xlsx" | "pdf",
): Promise<void> {
  return downloadFile(
    `/admin/reports/${p.kind}/export`,
    { ...p, kind: undefined, format },
    `bao-cao-${p.kind}`,
  );
}

export async function searchReportItems(
  workspaceId: string | null,
  query: string,
): Promise<ReportItem[]> {
  const { data } = await api.get<ReportItem[]>("/admin/reports/items", {
    params: cleanParams({ workspaceId, query }),
  });
  return data;
}

export interface BulkResult {
  count: number;
  message: string;
}

export async function bulkRevokeAnyoneLinks(
  workspaceId: string | null,
): Promise<BulkResult> {
  const { data } = await api.post<BulkResult>(
    "/admin/bulk/revoke-anyone-links",
    { workspaceId },
  );
  return data;
}

export async function bulkRemovePrincipal(userId: string): Promise<BulkResult> {
  const { data } = await api.post<BulkResult>(
    "/admin/bulk/remove-user-permissions",
    { userId },
  );
  return data;
}

export async function bulkDeleteInactiveGuests(
  days: number,
): Promise<BulkResult> {
  const { data } = await api.post<BulkResult>(
    "/admin/bulk/delete-inactive-guests",
    { days },
  );
  return data;
}
