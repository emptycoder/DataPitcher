import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import { Icons } from './icons';
import { cx, type Tone } from './index';

export type Toast = Readonly<{ id: number; tone: Tone; title: string; description?: string }>;
type ToastInput = Readonly<{ tone?: Tone; title: string; description?: string; durationMs?: number }>;
type ToastApi = Readonly<{ push: (toast: ToastInput) => void; success: (title: string, description?: string) => void; error: (title: string, description?: string) => void; info: (title: string, description?: string) => void }>;

const ToastContext = createContext<ToastApi>({ push: () => undefined, success: () => undefined, error: () => undefined, info: () => undefined });

let nextId = 1;

export function ToastProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [toasts, setToasts] = useState<readonly Toast[]>([]);
  const dismiss = useCallback((id: number) => setToasts((current) => current.filter((toast) => toast.id !== id)), []);
  const push = useCallback(
    (input: ToastInput) => {
      const id = nextId++;
      setToasts((current) => [...current.slice(-4), { id, tone: input.tone ?? 'info', title: input.title, description: input.description }]);
      window.setTimeout(() => dismiss(id), input.durationMs ?? (input.tone === 'danger' ? 8000 : 4500));
    },
    [dismiss],
  );
  const api = useMemo<ToastApi>(
    () => ({
      push,
      success: (title, description) => push({ tone: 'success', title, description }),
      error: (title, description) => push({ tone: 'danger', title, description }),
      info: (title, description) => push({ tone: 'info', title, description }),
    }),
    [push],
  );
  return (
    <ToastContext value={api}>
      {children}
      <div aria-live="polite" className="pointer-events-none fixed right-4 bottom-4 z-[70] flex w-[min(380px,calc(100vw-2rem))] flex-col gap-2">
        {toasts.map((toast) => {
          const Icon = toast.tone === 'success' ? Icons.Check : toast.tone === 'danger' ? Icons.Alert : Icons.Info;
          return (
            <div
              className={cx(
                'dp-fade-up pointer-events-auto flex items-start gap-3 rounded-xl border border-border bg-surface p-3.5 shadow-pop',
              )}
              key={toast.id}
              role="status"
            >
              <span
                className={cx(
                  'mt-0.5 flex size-6 shrink-0 items-center justify-center rounded-full',
                  toast.tone === 'success' && 'bg-success-soft text-success',
                  toast.tone === 'danger' && 'bg-danger-soft text-danger',
                  (toast.tone === 'info' || toast.tone === 'accent' || toast.tone === 'neutral') && 'bg-info-soft text-info',
                  toast.tone === 'warning' && 'bg-warning-soft text-warning',
                )}
              >
                <Icon size={13} strokeWidth={2.5} />
              </span>
              <div className="min-w-0 flex-1">
                <div className="text-sm font-semibold text-fg">{toast.title}</div>
                {toast.description ? <div className="mt-0.5 text-[13px] text-fg-muted">{toast.description}</div> : null}
              </div>
              <button aria-label="Dismiss" className="text-fg-faint hover:text-fg" onClick={() => dismiss(toast.id)} type="button">
                <Icons.X size={14} />
              </button>
            </div>
          );
        })}
      </div>
    </ToastContext>
  );
}

export function useToast() {
  return useContext(ToastContext);
}
