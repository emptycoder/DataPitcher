import { expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { App } from './App';

it('renders the application landmark and name', () => {
  render(<App />);
  expect(screen.getByRole('main')).toBeVisible();
  expect(screen.getByRole('heading', { name: 'DataPitcher' })).toBeVisible();
});
