import { useInfiniteQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { Pressable, StyleSheet, Text, TextInput, View } from 'react-native';

import { driveApi } from '@/api/endpoints';
import type { SearchHit } from '@/api/types';
import { HighlightedText } from '@/components/HighlightedText';
import { ItemList } from '@/components/ItemList';
import { EmptyState, Icon } from '@/components/ui';
import { useRefresh } from '@/hooks/useRefresh';
import { t } from '@/i18n';
import { useAuth } from '@/store/auth';
import { useColors } from '@/theme';
import { formatBytes } from '@/utils/format';

/** #75 Tìm kiếm (GET /search?q=): có dấu/không dấu, kết quả tô đậm phần khớp, cuộn vô hạn. */
export default function SearchScreen() {
  const c = useColors();
  const personalWs = useAuth((s) => s.user?.personalWorkspaceId);
  const [text, setText] = useState('');
  const [q, setQ] = useState('');
  const [mineOnly, setMineOnly] = useState(false);

  useEffect(() => {
    const id = setTimeout(() => setQ(text.trim()), 400);
    return () => clearTimeout(id);
  }, [text]);

  const workspaceId = mineOnly ? personalWs ?? undefined : undefined;
  const query = useInfiniteQuery({
    queryKey: ['search', q, workspaceId],
    queryFn: ({ pageParam }) => driveApi.search(q, pageParam, workspaceId),
    initialPageParam: 1,
    getNextPageParam: (last, all) => {
      const loaded = all.reduce((n, p) => n + p.items.length, 0);
      return loaded < last.total && last.items.length > 0 ? last.page + 1 : undefined;
    },
    enabled: q.length > 0,
  });
  const hits = useMemo(() => query.data?.pages.flatMap((p) => p.items) ?? [], [query.data]);
  const first = query.data?.pages[0];
  const { refreshing, onRefresh } = useRefresh(query.refetch);

  const header = (
    <View style={{ backgroundColor: c.surface, borderBottomColor: c.border, borderBottomWidth: StyleSheet.hairlineWidth }}>
      <View style={styles.chips}>
        {[false, true].map((mine) => (
          <Pressable
            key={String(mine)}
            onPress={() => setMineOnly(mine)}
            style={[styles.chip, { borderColor: mineOnly === mine ? c.primary : c.border, backgroundColor: mineOnly === mine ? c.primary : 'transparent' }]}
          >
            <Text style={{ color: mineOnly === mine ? c.primaryText : c.text, fontSize: 13 }}>{mine ? t('search.scopeMine') : t('search.scopeAll')}</Text>
          </Pressable>
        ))}
      </View>
      {first ? (
        <Text style={{ color: c.textMuted, fontSize: 12, paddingHorizontal: 16, paddingBottom: 8 }}>
          {t(first.totalCapped ? 'search.resultsMore' : 'search.results', { count: first.total.toLocaleString('vi-VN'), ms: first.elapsedMs })}
        </Text>
      ) : null}
    </View>
  );

  return (
    <View style={{ flex: 1, backgroundColor: c.background }}>
      <View style={[styles.searchBar, { backgroundColor: c.surface }]}>
        <View style={[styles.inputWrap, { backgroundColor: c.surfaceAlt }]}>
          <Icon name="magnify" color={c.textMuted} />
          <TextInput
            value={text}
            onChangeText={setText}
            placeholder={t('search.placeholder')}
            placeholderTextColor={c.textMuted}
            style={[styles.input, { color: c.text }]}
            autoFocus
            returnKeyType="search"
            onSubmitEditing={() => setQ(text.trim())}
            autoCorrect={false}
          />
          {text ? (
            <Pressable onPress={() => setText('')} hitSlop={10}>
              <Icon name="close-circle" size={18} color={c.textMuted} />
            </Pressable>
          ) : null}
        </View>
      </View>
      {q.length === 0 ? (
        <EmptyState icon="text-search" title={t('search.hint')} />
      ) : (
        <ItemList<SearchHit>
          data={hits}
          getItem={(h) => h.item}
          isLoading={query.isLoading}
          isError={query.isError}
          error={query.error}
          refreshing={refreshing}
          onRefresh={onRefresh}
          onEndReached={() => {
            if (query.hasNextPage && !query.isFetchingNextPage) query.fetchNextPage();
          }}
          loadingMore={query.isFetchingNextPage}
          emptyTitle={t('search.empty')}
          emptyIcon="file-search-outline"
          header={header}
          renderTitle={(h) =>
            h.highlightedName ? (
              <HighlightedText html={h.highlightedName} numberOfLines={1} style={{ color: c.text, fontSize: 15, fontWeight: '500' }} />
            ) : undefined
          }
          renderSubtitle={(h) => [formatBytes(h.item.sizeBytes), h.location].filter(Boolean).join(' • ')}
          bottomPadding={24}
        />
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  searchBar: { padding: 10 },
  inputWrap: { flexDirection: 'row', alignItems: 'center', borderRadius: 10, paddingHorizontal: 10 },
  input: { flex: 1, fontSize: 16, paddingVertical: 10, marginHorizontal: 8 },
  chips: { flexDirection: 'row', gap: 8, padding: 10 },
  chip: { borderWidth: 1, borderRadius: 16, paddingHorizontal: 12, paddingVertical: 5 },
});
