import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';

import { driveApi } from '@/api/endpoints';
import { ItemList } from '@/components/ItemList';
import { useRefresh } from '@/hooks/useRefresh';
import { t } from '@/i18n';

const STEP = 50;
const MAX = 200; // server caps `take` at 200

/** #73 Gần đây: GET /recent (không phân trang → tải thêm bằng cách tăng `take`). */
export default function RecentScreen() {
  const [take, setTake] = useState(STEP);
  const q = useQuery({ queryKey: ['recent', 'all', take], queryFn: () => driveApi.recent(take), placeholderData: keepPreviousData });
  const { refreshing, onRefresh } = useRefresh(q.refetch);
  const data = q.data ?? [];
  const canMore = data.length >= take && take < MAX;
  return (
    <ItemList
      data={data}
      getItem={(x) => x}
      isLoading={q.isLoading}
      isError={q.isError}
      error={q.error}
      refreshing={refreshing}
      onRefresh={onRefresh}
      onEndReached={() => {
        if (canMore && !q.isFetching) setTake((v) => Math.min(MAX, v + STEP));
      }}
      loadingMore={q.isPlaceholderData && q.isFetching}
      emptyTitle={t('recent.empty')}
      emptyIcon="history"
      bottomPadding={24}
    />
  );
}
