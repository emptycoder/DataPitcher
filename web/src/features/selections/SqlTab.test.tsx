import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { SqlTab } from './SqlTab';
import { createMonacoAdapter, type MonacoAdapter } from './monacoAdapter';

const monaco = vi.hoisted(() => {
  let value = '';
  let listener: (() => void) | undefined;
  const model = {
    getValue: () => value,
    setValue: vi.fn((next: string) => { value = next; }),
    dispose: vi.fn(),
  };
  const editor = {
    onDidChangeModelContent: vi.fn((next: () => void) => {
      listener = next;
      return { dispose: vi.fn() };
    }),
    layout: vi.fn(),
    dispose: vi.fn(),
  };
  return {
    createModel: vi.fn((next: string) => {
      value = next;
      return model;
    }),
    createEditor: vi.fn(() => editor),
    emitChange: () => listener?.(),
    model,
    editor,
  };
});

vi.mock('monaco-editor', () => ({
  editor: { createModel: monaco.createModel, create: monaco.createEditor },
}));

type FakeMonaco = {
  adapter: MonacoAdapter;
  model: { setValue: ReturnType<typeof vi.fn>; dispose: ReturnType<typeof vi.fn> };
  editor: { onDidChangeModelContent: ReturnType<typeof vi.fn>; layout: ReturnType<typeof vi.fn>; dispose: ReturnType<typeof vi.fn> };
  emitChange: (value: string) => void;
  disposeListener: ReturnType<typeof vi.fn>;
};

function createFakeMonaco(): FakeMonaco {
  let value = '';
  let onChange: (() => void) | undefined;
  const disposeListener = vi.fn();
  const model = {
    getValue: () => value,
    setValue: vi.fn((next: string) => {
      value = next;
      onChange?.();
    }),
    dispose: vi.fn(),
  };
  const editor = {
    onDidChangeModelContent: vi.fn((listener: () => void) => {
      onChange = listener;
      return { dispose: disposeListener };
    }),
    layout: vi.fn(),
    dispose: vi.fn(),
  };
  return {
    adapter: {
      createModel: vi.fn((snapshot: string) => {
        value = snapshot;
        return model;
      }),
      createEditor: vi.fn(() => editor),
    },
    model,
    editor,
    emitChange: (next) => {
      value = next;
      onChange?.();
    },
    disposeListener,
  };
}

function createActions() {
  return {
    editRawSql: vi.fn(),
    requestVisualMode: vi.fn(),
    cancelVisualMode: vi.fn(),
    confirmDiscardRawSql: vi.fn(),
  };
}

let notifyResize: (() => void) | undefined;
const disconnect = vi.fn();

class FakeResizeObserver {
  constructor(callback: ResizeObserverCallback) {
    notifyResize = () => callback([], this as unknown as ResizeObserver);
  }

  observe = vi.fn();
  disconnect = disconnect;
  unobserve = vi.fn();
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  disconnect.mockClear();
  notifyResize = undefined;
});

it('keeps the production Monaco API behind the adapter', () => {
  const adapter = createMonacoAdapter();
  const model = adapter.createModel('SELECT 1');
  const editor = adapter.createEditor(document.createElement('div'), model, { readOnly: true });
  const changed = vi.fn();
  const subscription = editor.onDidChangeModelContent(changed);

  monaco.emitChange();
  model.setValue('SELECT 2');
  editor.layout();
  subscription.dispose();
  editor.dispose();
  model.dispose();

  expect(monaco.createModel).toHaveBeenCalledWith('SELECT 1', 'sql');
  expect(monaco.createEditor).toHaveBeenCalledWith(expect.any(HTMLDivElement), { model, readOnly: true });
  expect(changed).toHaveBeenCalledOnce();
  expect(monaco.model.setValue).toHaveBeenCalledWith('SELECT 2');
  expect(monaco.editor.layout).toHaveBeenCalledOnce();
  expect(monaco.editor.dispose).toHaveBeenCalledOnce();
  expect(monaco.model.dispose).toHaveBeenCalledOnce();
});

it('shows a generated read-only snapshot when raw SQL permission is absent', () => {
  vi.stubGlobal('ResizeObserver', FakeResizeObserver);
  const fake = createFakeMonaco();

  render(<SqlTab adapter={fake.adapter} permissions={new Set()} snapshot="SELECT DISTINCT o.id" mode="visual" pendingVisualConfirmation={false} actions={createActions()} />);

  expect(screen.getByText('Generated SQL snapshot')).toBeVisible();
  expect(screen.getByText('Raw SQL requires Selections.RawSql.')).toBeVisible();
  expect(fake.adapter.createModel).toHaveBeenCalledWith('SELECT DISTINCT o.id');
  expect(fake.adapter.createEditor).toHaveBeenCalledWith(expect.any(HTMLDivElement), expect.anything(), { readOnly: true });
});

it('guards raw edits, synchronizes snapshots, and disposes Monaco lifecycle resources', () => {
  vi.stubGlobal('ResizeObserver', FakeResizeObserver);
  const fake = createFakeMonaco();
  const actions = createActions();
  const view = render(<SqlTab adapter={fake.adapter} permissions={new Set(['Selections.RawSql'])} snapshot="SELECT DISTINCT o.id" mode="raw" pendingVisualConfirmation={false} actions={actions} />);

  fake.emitChange('SELECT DISTINCT o.id WHERE o.id = @p0');
  expect(actions.editRawSql).toHaveBeenCalledWith('SELECT DISTINCT o.id WHERE o.id = @p0');

  view.rerender(<SqlTab adapter={fake.adapter} permissions={new Set(['Selections.RawSql'])} snapshot="SELECT DISTINCT o.id WHERE o.id = @p1" mode="raw" pendingVisualConfirmation={false} actions={actions} />);
  expect(fake.model.setValue).toHaveBeenCalledWith('SELECT DISTINCT o.id WHERE o.id = @p1');
  expect(actions.editRawSql).toHaveBeenCalledTimes(1);

  notifyResize?.();
  expect(fake.editor.layout).toHaveBeenCalledOnce();

  fireEvent.click(screen.getByRole('button', { name: 'Return to Visual Builder' }));
  expect(actions.requestVisualMode).toHaveBeenCalledOnce();
  expect(screen.queryByRole('dialog')).toBeNull();

  view.rerender(<SqlTab adapter={fake.adapter} permissions={new Set(['Selections.RawSql'])} snapshot="SELECT DISTINCT o.id WHERE o.id = @p1" mode="raw" pendingVisualConfirmation actions={actions} />);
  expect(screen.getByRole('dialog', { name: 'Discard raw SQL' })).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Keep raw SQL' }));
  expect(actions.cancelVisualMode).toHaveBeenCalledOnce();
  fireEvent.click(screen.getByRole('button', { name: 'Discard raw SQL' }));
  expect(actions.confirmDiscardRawSql).toHaveBeenCalledOnce();

  view.unmount();
  expect(disconnect).toHaveBeenCalledOnce();
  expect(fake.disposeListener).toHaveBeenCalledOnce();
  expect(fake.editor.dispose).toHaveBeenCalledOnce();
  expect(fake.model.dispose).toHaveBeenCalledOnce();
});
