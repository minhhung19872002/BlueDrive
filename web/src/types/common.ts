/** Server paged response. */
export interface Paged<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

/** Common paging query parameters (page is 1-based). */
export interface PageParams {
  page?: number
  pageSize?: number
  sort?: string
  q?: string
}

export interface IdResponse {
  id: string
}

export interface RefDto {
  id: string
  name: string
}

export type ExportFormat = 'xlsx' | 'pdf'
