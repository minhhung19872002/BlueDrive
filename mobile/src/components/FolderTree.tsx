import { useMemo, useState } from 'react';
import { FlatList, Pressable, RefreshControl, StyleSheet, Text, View } from 'react-native';

import type { FolderTreeNode } from '@/api/types';
import { useColors } from '@/theme';
import { Icon } from './ui';

interface Row {
  node: FolderTreeNode;
  depth: number;
  expanded: boolean;
}

/** Flattened, expandable folder tree (#82) used by the tree screen and the copy/move picker. */
export function FolderTree({
  roots, selectedId, onSelect, disabledIds, header, refreshing, onRefresh, initiallyExpanded,
}: {
  roots: FolderTreeNode[];
  selectedId?: string | null;
  onSelect: (node: FolderTreeNode) => void;
  disabledIds?: Set<string>;
  header?: React.ReactElement;
  refreshing?: boolean;
  onRefresh?: () => void;
  initiallyExpanded?: string[];
}) {
  const c = useColors();
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set(initiallyExpanded ?? []));

  const rows = useMemo(() => {
    const out: Row[] = [];
    const walk = (nodes: FolderTreeNode[], depth: number) => {
      for (const n of nodes) {
        const open = expanded.has(n.id);
        out.push({ node: n, depth, expanded: open });
        if (open && n.children.length) walk(n.children, depth + 1);
      }
    };
    walk(roots, 0);
    return out;
  }, [roots, expanded]);

  const toggle = (id: string) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  return (
    <FlatList
      data={rows}
      keyExtractor={(r) => r.node.id}
      ListHeaderComponent={header}
      refreshControl={onRefresh ? <RefreshControl refreshing={!!refreshing} onRefresh={onRefresh} colors={[c.primary]} tintColor={c.primary} /> : undefined}
      renderItem={({ item: r }) => {
        const disabled = disabledIds?.has(r.node.id);
        const selected = selectedId === r.node.id;
        return (
          <Pressable
            disabled={disabled}
            onPress={() => onSelect(r.node)}
            style={[styles.row, { paddingLeft: 12 + r.depth * 20, backgroundColor: selected ? c.surfaceAlt : 'transparent', opacity: disabled ? 0.4 : 1 }]}
          >
            <Pressable hitSlop={10} onPress={() => toggle(r.node.id)} style={styles.chevron} disabled={r.node.children.length === 0}>
              {r.node.children.length ? <Icon name={r.expanded ? 'chevron-down' : 'chevron-right'} color={c.textMuted} /> : <View style={{ width: 22 }} />}
            </Pressable>
            <Icon name={r.expanded ? 'folder-open' : 'folder'} color={c.folder} />
            <Text numberOfLines={1} style={[styles.name, { color: c.text, fontWeight: selected ? '700' : '400' }]}>
              {r.node.name}
            </Text>
            {selected ? <Icon name="check" color={c.primary} /> : null}
          </Pressable>
        );
      }}
    />
  );
}

/** Ids of a node and all of its descendants. */
export function collectSubtreeIds(roots: FolderTreeNode[], ids: string[]): Set<string> {
  const out = new Set<string>();
  const add = (n: FolderTreeNode) => {
    out.add(n.id);
    n.children.forEach(add);
  };
  const find = (nodes: FolderTreeNode[]) => {
    for (const n of nodes) {
      if (ids.includes(n.id)) add(n);
      else find(n.children);
    }
  };
  find(roots);
  return out;
}

/** Ancestors path ids (root → node) of a folder id. */
export function ancestorIds(roots: FolderTreeNode[], id: string): string[] {
  const path: string[] = [];
  const dfs = (nodes: FolderTreeNode[]): boolean => {
    for (const n of nodes) {
      path.push(n.id);
      if (n.id === id || dfs(n.children)) return true;
      path.pop();
    }
    return false;
  };
  return dfs(roots) ? path : [];
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', alignItems: 'center', paddingVertical: 11, paddingRight: 14 },
  chevron: { width: 26, alignItems: 'center' },
  name: { flex: 1, fontSize: 15, marginLeft: 8 },
});
