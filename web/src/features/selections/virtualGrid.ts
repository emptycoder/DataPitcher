import { useVirtualizer } from '@tanstack/react-virtual';
import type { RefObject } from 'react';

export type VirtualGrid = {
  totalHeight: number;
  items: readonly { index: number; start: number }[];
};

export type VirtualizerAdapter = {
  useGrid: (rowCount: number, scrollElement: RefObject<HTMLElement | null>) => VirtualGrid;
};

export const productionVirtualizerAdapter: VirtualizerAdapter = {
  useGrid: (rowCount, scrollElement) => {
    const virtualizer = useVirtualizer({ count: rowCount, getScrollElement: () => scrollElement.current, estimateSize: () => 32 });
    return { totalHeight: virtualizer.getTotalSize(), items: virtualizer.getVirtualItems().map(({ index, start }) => ({ index, start })) };
  },
};
