import * as monaco from 'monaco-editor';

export type MonacoModel = {
  getValue: () => string;
  setValue: (value: string) => void;
  dispose: () => void;
};

export type MonacoDisposable = { dispose: () => void };

export type MonacoEditor = {
  onDidChangeModelContent: (listener: () => void) => MonacoDisposable;
  layout: () => void;
  dispose: () => void;
};

export type MonacoAdapter = {
  createModel: (value: string) => MonacoModel;
  createEditor: (container: HTMLElement, model: MonacoModel, options: { readOnly: boolean }) => MonacoEditor;
};

export function createMonacoAdapter(): MonacoAdapter {
  return {
    createModel: (value) => monaco.editor.createModel(value, 'sql'),
    createEditor: (container, model, options) => monaco.editor.create(container, { model: model as monaco.editor.ITextModel, readOnly: options.readOnly }),
  };
}
