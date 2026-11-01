import { create } from 'zustand';

export type ToastKind = 'info' | 'success' | 'error';

interface ToastState {
  message: string | null;
  kind: ToastKind;
  key: number;
  show: (message: string, kind?: ToastKind) => void;
  hide: () => void;
}

export const useToast = create<ToastState>((set) => ({
  message: null,
  kind: 'info',
  key: 0,
  show: (message, kind = 'info') => set((s) => ({ message, kind, key: s.key + 1 })),
  hide: () => set({ message: null }),
}));

export const toast = {
  info: (m: string) => useToast.getState().show(m, 'info'),
  success: (m: string) => useToast.getState().show(m, 'success'),
  error: (m: string) => useToast.getState().show(m, 'error'),
};
