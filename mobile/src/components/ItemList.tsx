import type { ReactElement } from 'react';
import { ActivityIndicator, FlatList, RefreshControl, View } from 'react-native';

import { errorMessage } from '@/api/client';
import type { DriveItem } from '@/api/types';
import { useItemActions } from '@/features/ItemActions';
import { useColors } from '@/theme';
import { openItem } from '@/utils/navigation';
import { ItemRow } from './ItemRow';
import { EmptyState, ErrorState, LoadingView, type IconName } from './ui';

interface Props<T> {
  data: T[];
  getItem: (row: T) => DriveItem;
  keyOf?: (row: T) => string;
  isLoading: boolean;
  isError: boolean;
  error: unknown;
  refreshing: boolean;
  onRefresh: () => void;
  onEndReached?: () => void;
  loadingMore?: boolean;
  emptyTitle: string;
  emptyHint?: string;
  emptyIcon?: IconName;
  header?: ReactElement | null;
  renderTitle?: (row: T) => ReactElement | undefined;
  renderSubtitle?: (row: T) => string | undefined;
  bottomPadding?: number;
}

/** Drive item list with pull-to-refresh, infinite scroll and the shared action sheet. */
export function ItemList<T>({
  data, getItem, keyOf, isLoading, isError, error, refreshing, onRefresh, onEndReached, loadingMore, emptyTitle, emptyHint, emptyIcon,
  header, renderTitle, renderSubtitle, bottomPadding = 96,
}: Props<T>) {
  const c = useColors();
  const { openActions } = useItemActions();
  if (isLoading) return <LoadingView />;
  return (
    <FlatList
      data={isError ? [] : data}
      keyExtractor={(row) => (keyOf ? keyOf(row) : `${getItem(row).type}-${getItem(row).id}`)}
      renderItem={({ item: row }) => (
        <ItemRow item={getItem(row)} onPress={openItem} onMenu={(it) => openActions(it)} title={renderTitle?.(row)} subtitle={renderSubtitle?.(row)} />
      )}
      ListHeaderComponent={header ?? undefined}
      ListEmptyComponent={
        isError ? <ErrorState message={errorMessage(error)} onRetry={onRefresh} /> : <EmptyState title={emptyTitle} hint={emptyHint} icon={emptyIcon} />
      }
      ListFooterComponent={
        loadingMore ? (
          <View style={{ padding: 16 }}>
            <ActivityIndicator color={c.primary} />
          </View>
        ) : (
          <View style={{ height: bottomPadding }} />
        )
      }
      refreshControl={<RefreshControl refreshing={refreshing} onRefresh={onRefresh} colors={[c.primary]} tintColor={c.primary} />}
      onEndReached={onEndReached}
      onEndReachedThreshold={0.4}
      initialNumToRender={20}
      windowSize={11}
      style={{ backgroundColor: c.background }}
    />
  );
}
