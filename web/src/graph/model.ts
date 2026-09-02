export type GraphTableState = 'unselected' | 'root-selected' | 'required-dependency' | 'explicit-dependent' | 'target-satisfied' | 'blocked' | 'conflict' | 'cycle-member';
export type GraphTable = Readonly<{ id: string; schema: string; name: string; componentId: string; state: GraphTableState }>;
export type GraphRelationship = Readonly<{ id: string; name: string; childTableId: string; parentTableId: string }>;
export type GraphTopology = Readonly<{ revision: string; plannedTableIds: readonly string[]; tables: readonly GraphTable[]; relationships: readonly GraphRelationship[] }>;
export type VisibleItem = Readonly<{ id: string; kind: 'table' | 'schema' | 'scc'; memberIds: readonly string[] }>;
export type VisibleRelationship = Readonly<{ id: string; name: string; childItemId: string; parentItemId: string }>;
export type VisibleSubgraph = Readonly<{ items: readonly VisibleItem[]; relationships: readonly VisibleRelationship[]; tableIds: readonly string[] }>;
export const maximumVisibleNodes = 200;
