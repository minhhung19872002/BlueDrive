import { useCallback } from 'react'
import { useSearchParams } from 'react-router-dom'

/** Current folder kept in the URL (?folder=<id>) so links, refresh and back/forward work. */
export function useFolderSearchParam(): [string | null, (folderId: string | null) => void] {
  const [params, setParams] = useSearchParams()
  const folderId = params.get('folder')
  const setFolder = useCallback(
    (id: string | null) =>
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev)
          if (id) next.set('folder', id)
          else next.delete('folder')
          return next
        },
        { replace: false },
      ),
    [setParams],
  )
  return [folderId, setFolder]
}
