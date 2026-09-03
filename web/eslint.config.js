import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import jsxA11y from 'eslint-plugin-jsx-a11y';
import react from 'eslint-plugin-react';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';

const isFreshSelector = (node) => {
  if (node.type !== 'ArrowFunctionExpression' && node.type !== 'FunctionExpression') return false;
  if (node.body.type === 'ObjectExpression' || node.body.type === 'ArrayExpression') return true;
  return node.body.type === 'BlockStatement' && node.body.body.some(
    (statement) => statement.type === 'ReturnStatement'
      && (statement.argument?.type === 'ObjectExpression' || statement.argument?.type === 'ArrayExpression'),
  );
};

const zustandSelectorRule = {
  meta: {
    type: 'problem',
    schema: [],
    messages: {
      freshSelector: 'Zustand selectors returning an array or object require a shallow comparator.',
    },
  },
  create(context) {
    const creates = new Set();
    const hooks = new Set(['useStore']);
    const selectors = new Set();

    const isZustandStore = (node) => {
      let callee = node;
      while (callee.type === 'CallExpression') callee = callee.callee;
      return callee.type === 'Identifier' && creates.has(callee.name);
    };

    return {
      ImportDeclaration(node) {
        if (node.source.value !== 'zustand') return;
        for (const specifier of node.specifiers) {
          if (specifier.type !== 'ImportSpecifier') continue;
          if (specifier.imported.name === 'create') creates.add(specifier.local.name);
          if (specifier.imported.name === 'useStore') hooks.add(specifier.local.name);
        }
      },
      VariableDeclarator(node) {
        if (node.id.type !== 'Identifier' || !node.init) return;
        if (isZustandStore(node.init)) hooks.add(node.id.name);
        if (isFreshSelector(node.init)) selectors.add(node.id.name);
      },
      FunctionDeclaration(node) {
        if (node.id && isFreshSelector(node)) selectors.add(node.id.name);
      },
      CallExpression(node) {
        if (node.callee.type !== 'Identifier' || !hooks.has(node.callee.name) || node.arguments.length > 1) return;
        const [selector] = node.arguments;
        if (isFreshSelector(selector) || (selector?.type === 'Identifier' && selectors.has(selector.name))) {
          context.report({ node: selector, messageId: 'freshSelector' });
        }
      },
    };
  },
};

export default tseslint.config(
  { ignores: ['coverage/**', 'dist/**', 'src/api/generated/**'] },
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      react.configs.flat.recommended,
      react.configs.flat['jsx-runtime'],
      reactHooks.configs.flat.recommended,
      jsxA11y.flatConfigs.recommended,
      prettier,
    ],
    languageOptions: { globals: globals.browser },
    settings: { react: { version: 'detect' } },
    rules: { 'react/prop-types': 'off' },
  },
  {
    files: ['src/**/*.{ts,tsx}'],
    ignores: ['src/**/*.test.{ts,tsx}', 'src/test/**'],
    plugins: { datapitcher: { rules: { 'zustand-selector-shallow': zustandSelectorRule } } },
    rules: {
      'datapitcher/zustand-selector-shallow': 'error',
      'no-restricted-globals': ['error', { name: 'localStorage', message: 'Inject storage instead of using localStorage.' }],
      'no-restricted-properties': ['error', { object: 'window', property: 'localStorage', message: 'Inject storage instead of using localStorage.' }],
      'no-restricted-syntax': ['error', {
        selector: "MetaProperty[meta.name='import'][property.name='url']",
        message: 'Do not derive filesystem paths from import.meta.url in the frontend.',
      }],
    },
  },
);
