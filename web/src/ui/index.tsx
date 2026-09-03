import { cloneElement, useId, type ComponentPropsWithoutRef, type ReactElement, type ReactNode } from 'react';
import './ui.css';

export type ButtonProps = ComponentPropsWithoutRef<'button'>;

export function Button({ type = 'button', ...props }: ButtonProps) {
  return <button {...props} type={type} data-ui="button" />;
}

export type TextInputProps = ComponentPropsWithoutRef<'input'>;

export function TextInput({ type = 'text', ...props }: TextInputProps) {
  return <input {...props} type={type} data-ui="text-input" />;
}

export type FieldProps = Readonly<{ label: ReactNode; children: ReactElement<{ id?: string }> }>;

export function Field({ label, children }: FieldProps) {
  const generatedId = useId();
  const inputId = children.props.id ?? generatedId;
  return <div data-ui="field"><label htmlFor={inputId}>{label}</label>{cloneElement(children, { id: inputId })}</div>;
}

export type DataTableProps = ComponentPropsWithoutRef<'table'>;

export function DataTable(props: DataTableProps) {
  return <table {...props} data-ui="data-table" />;
}

export type StatusTone = 'neutral' | 'info' | 'success' | 'warning' | 'danger';

function toneFor(state: string): StatusTone {
  const normalized = state.toLowerCase();
  if (['healthy', 'succeeded'].includes(normalized)) return 'success';
  if (['checking', 'queued', 'preparing', 'running', 'verifying'].includes(normalized)) return 'info';
  if (['degraded', 'pausing', 'paused', 'cancelling'].includes(normalized)) return 'warning';
  if (['unhealthy', 'cancelled', 'failed', 'verificationfailed'].includes(normalized)) return 'danger';
  return 'neutral';
}

export type StatusBadgeProps = Readonly<{ state: string }>;

export function StatusBadge({ state }: StatusBadgeProps) {
  return <span role="status" data-ui="status-badge" data-tone={toneFor(state)}>{state}</span>;
}

export type InlineErrorProps = Readonly<{ children: ReactNode }>;

export function InlineError({ children }: InlineErrorProps) {
  return <p role="alert" data-ui="inline-error">{children}</p>;
}

export type LoadingIndicatorProps = Readonly<{ label?: ReactNode }>;

export function LoadingIndicator({ label = 'Loading…' }: LoadingIndicatorProps) {
  return <p role="status" aria-live="polite" aria-busy="true" data-ui="loading-indicator">{label}</p>;
}
