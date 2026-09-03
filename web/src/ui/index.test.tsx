import { afterEach, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Button, DataTable, Field, InlineError, LoadingIndicator, StatusBadge, TextInput } from './index';

afterEach(cleanup);

it('renders accessible button and text input primitives', () => {
  let clicks = 0;
  render(<><Button onClick={() => { clicks += 1; }}>Save</Button><TextInput aria-label="Connection name" /></>);

  fireEvent.click(screen.getByRole('button', { name: 'Save' }));
  expect(clicks).toBe(1);
  expect(screen.getByRole('button', { name: 'Save' })).toHaveAttribute('type', 'button');
  expect(screen.getByRole('textbox', { name: 'Connection name' })).toHaveAttribute('type', 'text');
});

it('ties field labels to generated and supplied input ids', () => {
  render(<><Field label="Generated"><TextInput /></Field><Field label="Supplied"><TextInput id="connection-name" /></Field></>);

  expect(screen.getByLabelText('Generated')).toHaveAttribute('data-ui', 'text-input');
  expect(screen.getByLabelText('Supplied')).toHaveAttribute('id', 'connection-name');
});

it('renders a semantic data table', () => {
  render(<DataTable><caption>Connections</caption><thead><tr><th>Name</th></tr></thead><tbody><tr><td>Warehouse</td></tr></tbody></DataTable>);

  expect(screen.getByRole('table', { name: 'Connections' })).toHaveTextContent('Warehouse');
});

it.each([
  ['Healthy', 'success'], ['Queued', 'info'], ['Degraded', 'warning'], ['Failed', 'danger'], ['Unknown', 'neutral'],
])('conveys %s state with a %s badge tone', (state, tone) => {
  render(<StatusBadge state={state} />);

  expect(screen.getByRole('status')).toHaveAttribute('data-tone', tone);
});

it('renders inline errors and loading status accessibly', () => {
  render(<><InlineError>Unable to save.</InlineError><LoadingIndicator label="Loading connections" /></>);

  expect(screen.getByRole('alert')).toHaveTextContent('Unable to save.');
  expect(screen.getByRole('status')).toHaveAttribute('aria-busy', 'true');
  expect(screen.getByRole('status')).toHaveTextContent('Loading connections');
});
