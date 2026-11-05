import { api } from "./client";

/** Spec 7.1: sensitivity labels (not for information classified as state secret). */
export interface Label {
  id: string;
  name: string;
  color: string;
  description: string;
  priority: number;
  blockExternal: boolean;
  blockAnyoneLinks: boolean;
  viewOnlyOutsideSpace: boolean;
  watermark: boolean;
  blockSync: boolean;
  isActive: boolean;
}

export interface ItemLabelInfo {
  label: Label | null;
  source: string | null;
  own: Label | null;
  canChange: boolean;
}

export async function listLabels(): Promise<Label[]> {
  const { data } = await api.get<Label[]>("/labels");
  return data;
}

export async function saveLabel(
  id: string | null,
  body: Omit<Label, "id">,
): Promise<void> {
  if (id) await api.put(`/admin/labels/${id}`, body);
  else await api.post("/admin/labels", body);
}

export async function getItemLabel(itemId: string): Promise<ItemLabelInfo> {
  const { data } = await api.get<ItemLabelInfo>(`/items/${itemId}/label`);
  return data;
}

export async function setItemLabel(
  itemId: string,
  labelId: string | null,
  reason: string | null,
): Promise<void> {
  await api.put(`/items/${itemId}/label`, { labelId, reason });
}
