import { vi } from './vi';

type Leaves<T, P extends string = ''> = {
  [K in keyof T & string]: T[K] extends string ? `${P}${K}` : Leaves<T[K], `${P}${K}.`>;
}[keyof T & string];

export type TKey = Leaves<typeof vi>;

/** Tra chuỗi tiếng Việt theo khóa "nhom.khoa", thay thế tham số {ten}. */
export function t(key: TKey, params?: Record<string, string | number>): string {
  let node: unknown = vi;
  for (const part of key.split('.')) node = (node as Record<string, unknown>)[part];
  let s = typeof node === 'string' ? node : key;
  if (params) for (const [k, v] of Object.entries(params)) s = s.split(`{${k}}`).join(String(v));
  return s;
}
