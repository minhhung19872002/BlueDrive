import { useQuery } from '@tanstack/react-query';

import { driveApi } from '@/api/endpoints';
import { ItemList } from '@/components/ItemList';
import { useRefresh } from '@/hooks/useRefresh';
import { t } from '@/i18n';

/** Danh sách mục đã đánh dấu quan trọng (#78). */
export default function StarredScreen() {
  const q = useQuery({ queryKey: ['favorites'], queryFn: driveApi.favorites });
  const { refreshing, onRefresh } = useRefresh(q.refetch);
  return (
    <ItemList
      data={q.data ?? []}
      getItem={(x) => x}
      isLoading={q.isLoading}
      isError={q.isError}
      error={q.error}
      refreshing={refreshing}
      onRefresh={onRefresh}
      emptyTitle={t('starred.empty')}
      emptyIcon="star-outline"
      bottomPadding={24}
    />
  );
}
