import { useEffect, useRef } from 'react';
import type { MonacoAdapter, MonacoEditor, MonacoModel } from './monacoAdapter';

type SqlTabActions = {
  editRawSql: (rawSql: string) => void;
  requestVisualMode: () => void;
  cancelVisualMode: () => void;
  confirmDiscardRawSql: () => void;
};

type SqlTabProps = {
  adapter: MonacoAdapter;
  permissions: ReadonlySet<string>;
  snapshot: string;
  mode: 'visual' | 'raw';
  pendingVisualConfirmation: boolean;
  actions: SqlTabActions;
};

export function SqlTab({ adapter, permissions, snapshot, mode, pendingVisualConfirmation, actions }: SqlTabProps) {
  const container = useRef<HTMLDivElement>(null);
  const model = useRef<MonacoModel | null>(null);
  const editor = useRef<MonacoEditor | null>(null);
  const synchronizing = useRef(false);
  const previousSnapshot = useRef(snapshot);
  const currentActions = useRef(actions);
  const canEdit = permissions.has('Selections.RawSql');
  const currentCanEdit = useRef(canEdit);
  currentActions.current = actions;
  currentCanEdit.current = canEdit;

  useEffect(() => {
    const currentModel = adapter.createModel(snapshot);
    const currentEditor = adapter.createEditor(container.current!, currentModel, { readOnly: !canEdit });
    const listener = currentEditor.onDidChangeModelContent(() => {
      if (!synchronizing.current && currentCanEdit.current) currentActions.current.editRawSql(currentModel.getValue());
    });
    const resizeObserver = new ResizeObserver(() => currentEditor.layout());
    resizeObserver.observe(container.current!);
    model.current = currentModel;
    editor.current = currentEditor;
    return () => {
      resizeObserver.disconnect();
      listener.dispose();
      currentEditor.dispose();
      currentModel.dispose();
      editor.current = null;
      model.current = null;
    };
  }, [adapter, canEdit]);

  useEffect(() => {
    if (snapshot !== previousSnapshot.current) {
      previousSnapshot.current = snapshot;
      synchronizing.current = true;
      model.current!.setValue(snapshot);
      synchronizing.current = false;
    }
  }, [snapshot]);

  return (
    <section aria-label="SQL">
      <h2>Generated SQL snapshot</h2>
      {!canEdit ? <p>Raw SQL requires Selections.RawSql.</p> : null}
      <div ref={container} />
      {mode === 'raw' ? <button type="button" onClick={actions.requestVisualMode}>Return to Visual Builder</button> : null}
      {pendingVisualConfirmation ? (
        <div role="dialog" aria-label="Discard raw SQL">
          <p>Discard raw SQL and return to Visual Builder?</p>
          <button type="button" onClick={actions.cancelVisualMode}>Keep raw SQL</button>
          <button type="button" onClick={actions.confirmDiscardRawSql}>Discard raw SQL</button>
        </div>
      ) : null}
    </section>
  );
}
