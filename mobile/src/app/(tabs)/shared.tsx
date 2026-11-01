import { driveApi } from '@/api/endpoints';
import type { SharedItem } from '@/api/types';
import { ItemList } from '@/components/ItemList';
import { usePagedList } from '@/hooks/usePagedList';
import { useRefresh } from '@/hooks/useRefresh';
import { t } from '@/i18n';
import { formatDate } from '@/utils/format';

/** #72 Được chia sẻ với tôi (GET /shares/with-me, phân trang). */
export default function SharedScreen() {
  const list = usePagedList<SharedItem>(['shared'], (page) => driveApi.sharedWithMe(page));
  const { refreshing, onRefresh } = useRefresh(list.refetch);
  return (
    <ItemList
      data={list.items}
      getItem={(s) => s.item}
      keyOf={(s) => s.shareId}
      isLoading={list.isLoading}
      isError={list.isError}
      error={list.error}
      refreshing={refreshing}
      onRefresh={onRefresh}
      onEndReached={list.loadMore}
      loadingMore={list.isFetchingNextPage}
      emptyTitle={t('shared.empty')}
      emptyIcon="account-multiple-outline"
      renderSubtitle={(s) => t('shared.sharedBy', { name: s.sharedByName, date: formatDate(s.sharedAt) })}
      bottomPadding={24}
    />
  );
}
