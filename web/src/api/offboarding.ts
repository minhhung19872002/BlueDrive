import { api } from "./client";
import { downloadFile } from "./http";

/** Spec 8.3: a user leaving or moving to another unit. */
export interface OffboardingSpace {
  id: string;
  name: string;
  role: string;
  soleOwner: boolean;
}

export interface OffboardingPreview {
  userId: string;
  fullName: string;
  username: string;
  isActive: boolean;
  files: number;
  folders: number;
  bytes: number;
  activeLinks: number;
  groups: string[];
  spaces: OffboardingSpace[];
}

export interface OffboardingReport {
  id: string;
  userId: string;
  userName: string;
  recipientId: string;
  recipientName: string;
  performedAt: string;
  performedByName: string;
  lines: { item: string; detail: string }[];
}

export interface UnitChangeReport {
  fromUnit: string;
  toUnit: string;
  lost: string[];
  gained: string[];
}

export async function getOffboardingPreview(
  userId: string,
): Promise<OffboardingPreview> {
  const { data } = await api.get<OffboardingPreview>(
    `/admin/offboarding/${userId}/preview`,
  );
  return data;
}

export async function offboardUser(
  userId: string,
  body: { recipientId: string; revokeLinks: boolean; deactivate: boolean },
): Promise<OffboardingReport> {
  const { data } = await api.post<OffboardingReport>(
    `/admin/offboarding/${userId}`,
    body,
  );
  return data;
}

export async function listOffboardings(): Promise<OffboardingReport[]> {
  const { data } = await api.get<OffboardingReport[]>("/admin/offboarding");
  return data;
}

export function exportOffboarding(
  id: string,
  format: "xlsx" | "pdf",
): Promise<void> {
  return downloadFile(
    `/admin/offboarding/records/${id}/export`,
    { format },
    "bien-ban-ban-giao",
  );
}

export async function changeUserUnit(
  userId: string,
  organizationId: string | null,
): Promise<UnitChangeReport> {
  const { data } = await api.post<UnitChangeReport>(
    `/admin/offboarding/${userId}/unit`,
    { organizationId },
  );
  return data;
}

/** Spec 8.5: an administrator's emergency access (at most 24 hours). */
export interface EmergencyAccess {
  id: string;
  workspaceId: string;
  workspaceName: string;
  isPersonal: boolean;
  adminId: string;
  adminName: string;
  reason: string;
  investigation: boolean;
  createdAt: string;
  expiresAt: string;
  endedAt: string | null;
  endedByName: string | null;
  active: boolean;
}

export async function listEmergencyAccess(): Promise<EmergencyAccess[]> {
  const { data } = await api.get<EmergencyAccess[]>("/admin/emergency-access");
  return data;
}

export async function grantEmergencyAccess(body: {
  workspaceId: string;
  hours: number;
  reason: string;
  investigation: boolean;
}): Promise<{ id: string }> {
  const { data } = await api.post<{ id: string }>(
    "/admin/emergency-access",
    body,
  );
  return data;
}

export async function endEmergencyAccess(id: string): Promise<void> {
  await api.post(`/admin/emergency-access/${id}/end`);
}
