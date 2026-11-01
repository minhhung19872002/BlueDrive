import { create } from 'zustand'

const GB = 1024 ** 3

interface StorageState {
  usedBytes: number
  quotaBytes: number
  setUsage: (usedBytes: number, quotaBytes: number) => void
}

/** Personal storage usage. Placeholder values until the quota API is wired (later phase). */
export const useStorageStore = create<StorageState>()((set) => ({
  usedBytes: 0,
  quotaBytes: 10 * GB,
  setUsage: (usedBytes, quotaBytes) => set({ usedBytes, quotaBytes }),
}))
