import { useCallback, useEffect, useRef, useState } from 'react';

/** Pull-to-refresh state that only reflects user-initiated refreshes. */
export function useRefresh(...refetchers: (() => Promise<unknown>)[]) {
  const [refreshing, setRefreshing] = useState(false);
  const latest = useRef(refetchers);
  useEffect(() => {
    latest.current = refetchers;
  });
  const onRefresh = useCallback(async () => {
    setRefreshing(true);
    try {
      await Promise.all(latest.current.map((r) => r()));
    } finally {
      setRefreshing(false);
    }
  }, []);
  return { refreshing, onRefresh };
}
