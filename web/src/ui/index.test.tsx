import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { ProgressBar, StatusBadge, Stepper, humanizeState, toneForState } from './index';

afterEach(cleanup);

describe('ui primitives', () => {
  it('renders determinate and indeterminate progress bars', () => {
    render(<ProgressBar label="Rows" showPercent value={0.256} />);
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '25.6');
    expect(screen.getByText('26%')).toBeInTheDocument();
    cleanup();
    render(<ProgressBar label="Working" value={null} />);
    expect(screen.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow');
  });

  it('maps states to tones and labels', () => {
    expect(toneForState('Healthy')).toBe('success');
    expect(toneForState('running')).toBe('info');
    expect(toneForState('Paused')).toBe('warning');
    expect(toneForState('VerificationFailed')).toBe('danger');
    expect(humanizeState('VerificationFailed')).toBe('Verification failed');
    expect(humanizeState('queued')).toBe('Queued');
    render(<StatusBadge state="running" />);
    expect(screen.getByRole('status')).toHaveTextContent('Running');
  });

  it('renders stepper steps in order', () => {
    render(
      <Stepper
        steps={[
          { key: 'a', label: 'Connect', status: 'done' },
          { key: 'b', label: 'Scan', status: 'active' },
          { key: 'c', label: 'Seal', status: 'todo' },
        ]}
      />,
    );
    expect(screen.getAllByRole('listitem')).toHaveLength(3);
    expect(screen.getByText('Scan')).toBeInTheDocument();
  });
});
