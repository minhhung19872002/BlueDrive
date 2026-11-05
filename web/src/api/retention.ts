import { api } from "./client";
import type { Paged, PageParams } from "@/types/common";
import type { ItemRef } from "@/types/drive";

/** Spec 7.2–7.3: retention policies, legal holds, the preservation store. */
export type RetentionScope = "workspace" | "library" | "label" | "catalog";
export type RetentionBasis = "created" | "modified";
export type RetentionExpiry = "none" | "review" | "delete";
export type HoldScope = "workspace" | "folder" | "user";

export interface RetentionPolicy {
  id: string;
  name: string;
  description: string;
  scopeKind: RetentionScope;
  scopeId: string;
  workspaceId: string | null;
  scopeName: string;
  years: number;
  basis: RetentionBasis;
  onExpiry: RetentionExpiry;
  isActive: boolean;
  createdAt: string;
  createdByName: string | null;
}

export type SaveRetentionPolicy = Pick<
  RetentionPolicy,
  | "name"
  | "description"
  | "scopeKind"
  | "scopeId"
  | "workspaceId"
  | "years"
  | "basis"
  | "onExpiry"
  | "isActive"
>;

export interface LegalHold {
  id: string;
  name: string;
  reason: string;
  scopeKind: HoldScope;
  scopeId: string;
  scopeName: string;
  createdAt: string;
  createdByName: string | null;
  releasedAt: string | null;
  releasedByName: string | null;
  releaseReason: string | null;
}

export interface PreservedItem {
  id: string;
  type: "file" | "folder";
  name: string;
  workspaceId: string;
  workspaceName: string;
  sizeBytes: number;
  deletedAt: string | null;
  deletedByName: string | null;
  preservedAt: string;
  policyName: string | null;
  retainUntil: string | null;
  hold: string | null;
}

export interface ReviewItem {
  fileId: string;
  name: string;
  workspaceId: string;
  workspaceName: string;
  policyId: string;
  policyName: string;
  expiredAt: string;
  sizeBytes: number;
}

export interface HoldFolder {
  id: string;
  path: string;
}

export async function listRetentionPolicies(): Promise<RetentionPolicy[]> {
  const { data } = await api.get<RetentionPolicy[]>(
    "/admin/retention/policies",
  );
  return data;
}

export async function saveRetentionPolicy(
  id: string | null,
  body: SaveRetentionPolicy,
): Promise<void> {
  if (id) await api.put(`/admin/retention/policies/${id}`, body);
  else await api.post("/admin/retention/policies", body);
}

export async function deleteRetentionPolicy(id: string): Promise<void> {
  await api.delete(`/admin/retention/policies/${id}`);
}

export async function listHolds(
  includeReleased: boolean,
): Promise<LegalHold[]> {
  const { data } = await api.get<LegalHold[]>("/admin/holds", {
    params: { includeReleased },
  });
  return data;
}

export async function createHold(body: {
  name: string;
  reason: string;
  scopeKind: HoldScope;
  scopeId: string;
}): Promise<void> {
  await api.post("/admin/holds", body);
}

export async function releaseHold(id: string, reason: string): Promise<void> {
  await api.post(`/admin/holds/${id}/release`, { reason });
}

export async function listHoldFolders(
  workspaceId: string,
): Promise<HoldFolder[]> {
  const { data } = await api.get<HoldFolder[]>("/admin/holds/folders", {
    params: { workspaceId },
  });
  return data;
}

export async function listPreserved(
  params: PageParams,
): Promise<Paged<PreservedItem>> {
  const { data } = await api.get<Paged<PreservedItem>>(
    "/admin/retention/preserved",
    { params },
  );
  return data;
}

export async function releasePreserved(items: ItemRef[]): Promise<void> {
  await api.post("/admin/retention/preserved/release", { items });
}

export async function purgePreserved(items: ItemRef[]): Promise<void> {
  await api.post("/admin/retention/preserved/purge", { items });
}

export async function listRetentionReview(): Promise<ReviewItem[]> {
  const { data } = await api.get<ReviewItem[]>("/admin/retention/review");
  return data;
}

export async function approveRetentionDelete(
  fileIds: string[],
): Promise<{ deleted: number }> {
  const { data } = await api.post<{ deleted: number }>(
    "/admin/retention/review/approve",
    { fileIds },
  );
  return data;
}
