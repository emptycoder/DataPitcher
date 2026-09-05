import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { PlanMapping } from '../../api/plans';

const save = vi.fn().mockResolvedValue({ planId: 'p', version: 2, canonicalHash: null, eTag: '"2"' });
const mappingQuery: { data: PlanMapping | undefined; isPending: boolean; isError: boolean; error: unknown } = { data: undefined, isPending: false, isError: false, error: null };

vi.mock('../../auth/AuthContext', () => ({ useAuth: () => ({ authentication: {} }) }));
vi.mock('../../auth/permissions', () => ({ usePermissions: () => ({ hasPermission: () => true }) }));
vi.mock('../../ui/toast', () => ({ useToast: () => ({ success: vi.fn(), error: vi.fn(), push: vi.fn() }) }));
vi.mock('../shared/queries', () => ({ usePlanMapping: () => mappingQuery }));
vi.mock('../../api/plans', () => ({ plansApi: { save: (...args: unknown[]) => save(...args) } }));

import { MappingPanel } from './MappingPanel';

afterEach(cleanup);

const mapping: PlanMapping = {
  planId: 'p',
  version: 1,
  eTag: '"1"',
  targetSnapshotId: 's',
  problems: [],
  tables: [
    {
      source: { schema: 'dbo', name: 'Orders' },
      target: { schema: 'dbo', name: 'Orders' },
      targetExists: true,
      isRoot: true,
      targetColumns: ['Id', 'Remark', 'Region'],
      columns: [
        { source: 'Id', sourceType: 'int', sourceNullable: false, target: 'Id', targetType: 'int', targetNullable: false, isKey: true, isForeignKey: false, origin: 'default', problems: [] },
        {
          source: 'Note',
          sourceType: 'text',
          sourceNullable: true,
          target: null,
          targetType: null,
          targetNullable: null,
          isKey: false,
          isForeignKey: false,
          origin: 'unmapped',
          problems: [{ code: 'column_unmapped', message: 'dbo.Orders.Note has no target column of the same name.', isBlocker: false }],
        },
      ],
      targetOnlyColumns: [{ name: 'Region', type: 'text', isNullable: false, problems: [{ code: 'target_required_unfilled', message: 'Region is NOT NULL.', isBlocker: false }] }],
      problems: [],
    },
  ],
};

function renderPanel() {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <MappingPanel planId="p" sealed={false} />
    </QueryClientProvider>,
  );
}

describe('MappingPanel', () => {
  it('shows the prefilled mapping with its problems and saves only the operator choices', async () => {
    mappingQuery.data = mapping;
    renderPanel();
    expect(screen.getByText('2 warnings')).toBeInTheDocument();
    fireEvent.click(screen.getByText('dbo.Orders'));
    expect(screen.getByText('dbo.Orders.Note has no target column of the same name.')).toBeInTheDocument();
    expect(screen.getByText('matched by name')).toBeInTheDocument();
    const saveButton = screen.getByRole('button', { name: /save mapping/i });
    expect(saveButton).toBeDisabled();

    fireEvent.change(screen.getByLabelText('Target column for Note'), { target: { value: 'Remark' } });
    expect(screen.getByText('1 customised')).toBeInTheDocument();
    fireEvent.click(saveButton);

    await vi.waitFor(() => expect(save).toHaveBeenCalledTimes(1));
    expect(save.mock.calls[0]![1]).toMatchObject({ ifMatch: '"1"', mappings: [{ source: { schema: 'dbo', name: 'Orders' }, target: null, columns: [{ source: 'Note', target: 'Remark' }] }] });
  });

  it('lets a table go back to its defaults', () => {
    mappingQuery.data = mapping;
    renderPanel();
    fireEvent.click(screen.getByText('dbo.Orders'));
    fireEvent.change(screen.getByLabelText('Target column for Note'), { target: { value: 'Remark' } });
    fireEvent.click(screen.getByRole('button', { name: /reset this table/i }));
    expect(screen.queryByText('1 customised')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /save mapping/i })).toBeEnabled();
  });

  it('explains when there is nothing to map', () => {
    mappingQuery.data = { ...mapping, tables: [], problems: [{ code: 'selection_missing', message: 'Associate a selection first.', isBlocker: false }] };
    renderPanel();
    expect(screen.getByText('Nothing to map yet')).toBeInTheDocument();
    expect(screen.getByText('Associate a selection first.')).toBeInTheDocument();
  });
});
