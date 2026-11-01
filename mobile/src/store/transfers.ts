import { create } from 'zustand';

export interface Transfer {
  id: string;
  kind: 'upload' | 'download';
  name: string;
  /** 0..1, or -1 when the total size is unknown. */
  progress: number;
  label: string;
  cancel?: () => void;
}

interface TransfersState {
  items: Transfer[];
  start: (t: Transfer) => void;
  update: (id: string, patch: Partial<Transfer>) => void;
  finish: (id: string) => void;
}

export const useTransfers = create<TransfersState>((set) => ({
  items: [],
  start: (t) => set((s) => ({ items: [...s.items.filter((x) => x.id !== t.id), t] })),
  update: (id, patch) => set((s) => ({ items: s.items.map((x) => (x.id === id ? { ...x, ...patch } : x)) })),
  finish: (id) => set((s) => ({ items: s.items.filter((x) => x.id !== id) })),
}));

let seq = 0;
export const newTransferId = () => `${Date.now().toString(36)}-${(seq++).toString(36)}`;
