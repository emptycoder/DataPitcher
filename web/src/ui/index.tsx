import {
    cloneElement,
    useEffect,
    useId,
    useState,
    type ComponentPropsWithoutRef,
    type ReactElement,
    type ReactNode,
} from 'react';
import { createPortal } from 'react-dom';
import { Icons } from './icons';

export function cx(...parts: readonly (string | false | null | undefined)[]) {
    return parts.filter(Boolean).join(' ');
}

/* ---------------------------------- Button --------------------------------- */

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger' | 'success' | 'outline';
export type ButtonSize = 'sm' | 'md' | 'lg';
export type ButtonProps = ComponentPropsWithoutRef<'button'> &
    Readonly<{ variant?: ButtonVariant; size?: ButtonSize; loading?: boolean; icon?: ReactNode; block?: boolean }>;

const buttonVariants: Record<ButtonVariant, string> = {
    primary: 'bg-accent text-accent-fg hover:brightness-110 shadow-sm border border-transparent',
    secondary: 'bg-surface-2 text-fg hover:bg-surface-3 border border-border',
    outline: 'bg-transparent text-fg hover:bg-surface-2 border border-border-strong',
    ghost: 'bg-transparent text-fg-muted hover:text-fg hover:bg-surface-2 border border-transparent',
    danger: 'bg-danger text-white hover:brightness-110 border border-transparent',
    success: 'bg-success text-white hover:brightness-110 border border-transparent',
};
const buttonSizes: Record<ButtonSize, string> = {
    sm: 'h-8 px-2.5 text-[13px] gap-1.5 rounded-lg',
    md: 'h-9.5 px-3.5 text-sm gap-2 rounded-lg',
    lg: 'h-11 px-5 text-[15px] gap-2 rounded-xl',
};

export function Button({
    type = 'button',
    variant = 'secondary',
    size = 'md',
    loading = false,
    icon,
    block = false,
    className,
    children,
    disabled,
    ...props
}: ButtonProps) {
    return (
        <button
            {...props}
            type={type}
            disabled={disabled || loading}
            aria-busy={loading || undefined}
            className={cx(
                'inline-flex items-center justify-center font-medium whitespace-nowrap select-none transition-[background,color,box-shadow,filter] duration-150',
                'disabled:cursor-not-allowed disabled:opacity-55 disabled:hover:brightness-100',
                buttonVariants[variant],
                buttonSizes[size],
                block && 'w-full',
                className,
            )}
        >
            {loading ? <Spinner size={size === 'sm' ? 12 : 14} /> : icon}
            {children}
        </button>
    );
}

export function IconButton({
    label,
    className,
    size = 'md',
    variant = 'ghost',
    ...props
}: Omit<ButtonProps, 'children' | 'icon'> & Readonly<{ label: string; children?: ReactNode }>) {
    return (
        <Button
            {...props}
            aria-label={label}
            title={label}
            size={size}
            variant={variant}
            className={cx('!px-0', size === 'sm' ? 'w-8' : size === 'lg' ? 'w-11' : 'w-9.5', className)}
        />
    );
}

/* --------------------------------- Spinner --------------------------------- */

export function Spinner({ size = 16, className }: Readonly<{ size?: number; className?: string }>) {
    return (
        <svg
            aria-hidden="true"
            className={cx('dp-spin shrink-0', className)}
            fill="none"
            height={size}
            viewBox="0 0 24 24"
            width={size}
        >
            <circle cx="12" cy="12" opacity="0.25" r="9" stroke="currentColor" strokeWidth="3" />
            <path d="M21 12a9 9 0 0 0-9-9" stroke="currentColor" strokeLinecap="round" strokeWidth="3" />
        </svg>
    );
}

/* ---------------------------------- Inputs --------------------------------- */

const controlClass =
    'w-full rounded-lg border border-border bg-surface px-3 text-sm text-fg placeholder:text-fg-faint shadow-none transition-[border,box-shadow] focus:border-accent focus:ring-2 focus:ring-accent/25 focus:outline-none disabled:opacity-60 aria-[invalid=true]:border-danger';

export type TextInputProps = ComponentPropsWithoutRef<'input'>;
export function TextInput({ type = 'text', className, ...props }: TextInputProps) {
    return <input {...props} type={type} className={cx(controlClass, 'h-9.5', className)} />;
}

/** Password-style input with a show/hide toggle. Works inside <Field>, which supplies the id. */
export function SecretInput({ className, ...props }: Omit<TextInputProps, 'type'>) {
    const [visible, setVisible] = useState(false);
    return (
        <div className="relative">
            <input
                {...props}
                type={visible ? 'text' : 'password'}
                className={cx(controlClass, 'h-9.5 pr-10', className)}
            />
            <button
                aria-label={visible ? 'Hide value' : 'Show value'}
                aria-pressed={visible}
                className="absolute top-1/2 right-2 flex size-7 -translate-y-1/2 items-center justify-center rounded-md text-fg-faint hover:bg-surface-2 hover:text-fg"
                onClick={() => setVisible((current) => !current)}
                tabIndex={-1}
                title={visible ? 'Hide value' : 'Show value'}
                type="button"
            >
                {visible ? <Icons.EyeOff size={16} /> : <Icons.Eye size={16} />}
            </button>
        </div>
    );
}

export type TextAreaProps = ComponentPropsWithoutRef<'textarea'>;
export function TextArea({ className, ...props }: TextAreaProps) {
    return <textarea {...props} className={cx(controlClass, 'min-h-24 py-2', className)} />;
}

export type SelectProps = ComponentPropsWithoutRef<'select'>;
export function Select({ className, children, ...props }: SelectProps) {
    return (
        <div className="relative">
            <select {...props} className={cx(controlClass, 'h-9.5 appearance-none pr-9', className)}>
                {children}
            </select>
            <Icons.ChevronDown
                className="pointer-events-none absolute top-1/2 right-3 -translate-y-1/2 text-fg-faint"
                size={16}
            />
        </div>
    );
}

export type FieldProps = Readonly<{
    label: ReactNode;
    hint?: ReactNode;
    error?: ReactNode;
    required?: boolean;
    children: ReactElement<{ id?: string; 'aria-invalid'?: boolean; 'aria-describedby'?: string }>;
    className?: string;
}>;

export function Field({ label, hint, error, required, children, className }: FieldProps) {
    const generatedId = useId();
    const inputId = children.props.id ?? generatedId;
    const hintId = `${inputId}-hint`;
    return (
        <div className={cx('grid gap-1.5', className)}>
            <label className="text-[13px] font-medium text-fg-muted" htmlFor={inputId}>
                {label}
                {required ? <span className="ml-0.5 text-danger">*</span> : null}
            </label>
            {cloneElement(children, {
                id: inputId,
                'aria-invalid': error ? true : undefined,
                'aria-describedby': hint || error ? hintId : undefined,
            })}
            {error ? (
                <p className="text-[13px] text-danger" id={hintId} role="alert">
                    {error}
                </p>
            ) : hint ? (
                <p className="text-[13px] text-fg-faint" id={hintId}>
                    {hint}
                </p>
            ) : null}
        </div>
    );
}

/* ----------------------------------- Card ---------------------------------- */

export type CardProps = ComponentPropsWithoutRef<'section'> & Readonly<{ padded?: boolean; interactive?: boolean }>;
export function Card({ className, padded = true, interactive = false, ...props }: CardProps) {
    return (
        <section
            {...props}
            className={cx(
                'card',
                padded ? 'p-5' : 'overflow-hidden',
                interactive && 'transition-[border,box-shadow,transform] hover:border-border-strong hover:shadow-pop',
                className,
            )}
        />
    );
}

export function CardHeader({
    title,
    description,
    actions,
    icon,
    className,
}: Readonly<{ title: ReactNode; description?: ReactNode; actions?: ReactNode; icon?: ReactNode; className?: string }>) {
    return (
        <div className={cx('mb-4 flex items-start justify-between gap-4', className)}>
            <div className="flex min-w-0 items-start gap-3">
                {icon ? (
                    <div className="mt-0.5 flex size-8 shrink-0 items-center justify-center rounded-lg bg-accent-soft text-accent">
                        {icon}
                    </div>
                ) : null}
                <div className="min-w-0">
                    <h3 className="text-[15px] font-semibold text-fg">{title}</h3>
                    {description ? <p className="mt-0.5 text-[13px] text-fg-muted">{description}</p> : null}
                </div>
            </div>
            {actions ? <div className="flex shrink-0 items-center gap-2">{actions}</div> : null}
        </div>
    );
}

/* ---------------------------------- Badge ---------------------------------- */

export type Tone = 'neutral' | 'info' | 'success' | 'warning' | 'danger' | 'accent';

const toneClasses: Record<Tone, string> = {
    neutral: 'bg-surface-3 text-fg-muted border-border',
    info: 'bg-info-soft text-info border-transparent',
    success: 'bg-success-soft text-success border-transparent',
    warning: 'bg-warning-soft text-warning border-transparent',
    danger: 'bg-danger-soft text-danger border-transparent',
    accent: 'bg-accent-soft text-accent border-transparent',
};
const dotClasses: Record<Tone, string> = {
    neutral: 'bg-fg-faint',
    info: 'bg-info',
    success: 'bg-success',
    warning: 'bg-warning',
    danger: 'bg-danger',
    accent: 'bg-accent',
};

export function Badge({
    tone = 'neutral',
    dot = false,
    pulse = false,
    className,
    children,
    ...props
}: ComponentPropsWithoutRef<'span'> & Readonly<{ tone?: Tone; dot?: boolean; pulse?: boolean }>) {
    return (
        <span
            {...props}
            className={cx(
                'inline-flex h-6 items-center gap-1.5 rounded-full border px-2.5 text-xs font-medium whitespace-nowrap',
                toneClasses[tone],
                className,
            )}
        >
            {dot ? <span className={cx('size-1.5 rounded-full', dotClasses[tone], pulse && 'dp-pulse')} /> : null}
            {children}
        </span>
    );
}

export function toneForState(state: string): Tone {
    const normalized = state.toLowerCase();
    if (['healthy', 'succeeded', 'completed', 'sealed', 'enforced', 'trusted', 'ready'].includes(normalized))
        return 'success';
    if (
        ['checking', 'queued', 'preparing', 'running', 'verifying', 'connecting', 'connected', 'live'].includes(
            normalized,
        )
    )
        return 'info';
    if (
        [
            'degraded',
            'pausing',
            'paused',
            'cancelling',
            'invalidated',
            'draft',
            'unsealed',
            'not enforced',
            'not trusted',
            'reconnecting',
        ].includes(normalized)
    )
        return 'warning';
    if (['unhealthy', 'cancelled', 'failed', 'verificationfailed', 'error', 'blocked'].includes(normalized))
        return 'danger';
    return 'neutral';
}

export function humanizeState(state: string) {
    if (state === 'verificationfailed' || state === 'VerificationFailed') return 'Verification failed';
    return state.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, (c) => c.toUpperCase());
}

export function StatusBadge({
    state,
    pulse,
    className,
}: Readonly<{ state: string; pulse?: boolean; className?: string }>) {
    const tone = toneForState(state);
    const active =
        pulse ??
        ['checking', 'queued', 'preparing', 'running', 'verifying', 'pausing', 'cancelling'].includes(
            state.toLowerCase(),
        );
    return (
        <Badge className={className} dot pulse={active} role="status" tone={tone}>
            {humanizeState(state)}
        </Badge>
    );
}

/* ------------------------------- Progress bar ------------------------------ */

export type ProgressBarProps = Readonly<{
    /** 0..1; null renders an indeterminate bar. */
    value: number | null;
    tone?: Tone;
    size?: 'xs' | 'sm' | 'md' | 'lg';
    label?: ReactNode;
    detail?: ReactNode;
    showPercent?: boolean;
    striped?: boolean;
    className?: string;
}>;

const barSizes = { xs: 'h-1', sm: 'h-1.5', md: 'h-2.5', lg: 'h-3.5' } as const;
const barFill: Record<Tone, string> = {
    neutral: 'bg-fg-faint',
    info: 'bg-info',
    success: 'bg-success',
    warning: 'bg-warning',
    danger: 'bg-danger',
    accent: 'brand-gradient',
};

export function ProgressBar({
    value,
    tone = 'accent',
    size = 'md',
    label,
    detail,
    showPercent = false,
    striped = false,
    className,
}: ProgressBarProps) {
    const clamped = value === null ? null : Math.min(1, Math.max(0, value));
    const percent = clamped === null ? null : Math.round(clamped * 1000) / 10;
    return (
        <div className={cx('w-full', className)}>
            {label || detail || showPercent ? (
                <div className="mb-1.5 flex items-baseline justify-between gap-3 text-[13px]">
                    <span className="font-medium text-fg">{label}</span>
                    <span className="tnum text-fg-muted">
                        {detail}
                        {showPercent && percent !== null ? (
                            <span className="ml-2 font-semibold text-fg">
                                {percent.toFixed(percent >= 10 ? 0 : 1)}%
                            </span>
                        ) : null}
                    </span>
                </div>
            ) : null}
            <div
                aria-label={typeof label === 'string' ? label : undefined}
                aria-valuemax={100}
                aria-valuemin={0}
                aria-valuenow={percent ?? undefined}
                className={cx('relative w-full overflow-hidden rounded-full bg-surface-3', barSizes[size])}
                role="progressbar"
            >
                {clamped === null ? (
                    <div className={cx('dp-indeterminate absolute inset-y-0 w-1/3 rounded-full', barFill[tone])} />
                ) : (
                    <div
                        className={cx(
                            'h-full rounded-full transition-[width] duration-500 ease-out',
                            barFill[tone],
                            striped && 'dp-striped',
                        )}
                        style={{ width: `${Math.max(clamped > 0 ? 1.5 : 0, clamped * 100)}%` }}
                    />
                )}
            </div>
        </div>
    );
}

/* --------------------------------- Stepper --------------------------------- */

export type StepStatus = 'done' | 'active' | 'todo' | 'error';
export type StepperStep = Readonly<{
    key: string;
    label: string;
    description?: ReactNode;
    status: StepStatus;
    href?: string;
    onClick?: () => void;
}>;

export function Stepper({ steps, className }: Readonly<{ steps: readonly StepperStep[]; className?: string }>) {
    return (
        <ol className={cx('scrollbar-thin flex w-full items-stretch gap-2 overflow-x-auto pb-1', className)}>
            {steps.map((step, index) => {
                const done = step.status === 'done';
                const active = step.status === 'active';
                const error = step.status === 'error';
                const content = (
                    <>
                        <div className="flex items-center gap-2">
                            <span
                                className={cx(
                                    'flex size-6 shrink-0 items-center justify-center rounded-full text-[11px] font-bold',
                                    done && 'bg-success text-white',
                                    active && 'bg-accent text-accent-fg ring-4 ring-accent/20',
                                    error && 'bg-danger text-white',
                                    step.status === 'todo' && 'bg-surface-3 text-fg-faint',
                                )}
                            >
                                {done ? (
                                    <Icons.Check size={13} strokeWidth={3} />
                                ) : error ? (
                                    <Icons.X size={13} strokeWidth={3} />
                                ) : (
                                    index + 1
                                )}
                            </span>
                            <span
                                className={cx(
                                    'text-[13px] font-semibold',
                                    step.status === 'todo' ? 'text-fg-faint' : 'text-fg',
                                )}
                            >
                                {step.label}
                            </span>
                        </div>
                        {step.description ? (
                            <div className="mt-1.5 pl-8 text-xs text-fg-muted">{step.description}</div>
                        ) : null}
                        <div
                            className={cx(
                                'mt-3 h-1 w-full rounded-full',
                                done && 'bg-success',
                                active && 'brand-gradient',
                                error && 'bg-danger',
                                step.status === 'todo' && 'bg-surface-3',
                            )}
                        />
                    </>
                );
                const base = 'block min-w-0 flex-1 rounded-xl p-2 -m-2 transition-colors';
                return (
                    <li className="min-w-[150px] flex-1 md:min-w-0" key={step.key}>
                        {step.onClick ? (
                            <button
                                className={cx(base, 'w-full text-left hover:bg-surface-2')}
                                onClick={step.onClick}
                                type="button"
                            >
                                {content}
                            </button>
                        ) : (
                            <div className={base}>{content}</div>
                        )}
                    </li>
                );
            })}
        </ol>
    );
}

/* ------------------------------- Stat tile --------------------------------- */

export function Stat({
    label,
    value,
    hint,
    icon,
    tone = 'neutral',
    className,
}: Readonly<{
    label: ReactNode;
    value: ReactNode;
    hint?: ReactNode;
    icon?: ReactNode;
    tone?: Tone;
    className?: string;
}>) {
    return (
        <div className={cx('card flex items-start gap-3 p-4', className)}>
            {icon ? (
                <div className={cx('flex size-9 shrink-0 items-center justify-center rounded-lg', toneClasses[tone])}>
                    {icon}
                </div>
            ) : null}
            <div className="min-w-0">
                <div className="text-xs font-medium tracking-wide text-fg-muted uppercase">{label}</div>
                <div className="tnum mt-0.5 text-2xl leading-tight font-bold text-fg">{value}</div>
                {hint ? <div className="mt-0.5 text-xs text-fg-faint">{hint}</div> : null}
            </div>
        </div>
    );
}

/* ---------------------------------- Alert ---------------------------------- */

export function Alert({
    tone = 'info',
    title,
    children,
    className,
    actions,
}: Readonly<{ tone?: Tone; title?: ReactNode; children?: ReactNode; className?: string; actions?: ReactNode }>) {
    const Icon = tone === 'danger' || tone === 'warning' ? Icons.Alert : tone === 'success' ? Icons.Check : Icons.Info;
    return (
        <div
            className={cx('flex items-start gap-3 rounded-xl border px-4 py-3 text-sm', toneClasses[tone], className)}
            role={tone === 'danger' ? 'alert' : 'status'}
        >
            <Icon className="mt-0.5 shrink-0" size={16} />
            <div className="min-w-0 flex-1">
                {title ? <div className="font-semibold">{title}</div> : null}
                {children ? <div className={cx(title ? 'mt-0.5' : null, 'text-current/90')}>{children}</div> : null}
            </div>
            {actions}
        </div>
    );
}

export function InlineError({ children }: Readonly<{ children: ReactNode }>) {
    return (
        <p className="text-[13px] text-danger" role="alert">
            {children}
        </p>
    );
}

/* -------------------------------- Empty state ------------------------------ */

export function EmptyState({
    icon,
    title,
    description,
    action,
    className,
}: Readonly<{ icon?: ReactNode; title: ReactNode; description?: ReactNode; action?: ReactNode; className?: string }>) {
    return (
        <div className={cx('flex flex-col items-center justify-center px-6 py-12 text-center', className)}>
            {icon ? (
                <div className="mb-4 flex size-12 items-center justify-center rounded-2xl bg-accent-soft text-accent">
                    {icon}
                </div>
            ) : null}
            <h3 className="text-[15px] font-semibold text-fg">{title}</h3>
            {description ? <p className="mt-1 max-w-md text-sm text-fg-muted">{description}</p> : null}
            {action ? <div className="mt-5 flex gap-2">{action}</div> : null}
        </div>
    );
}

/* --------------------------------- Skeleton -------------------------------- */

export function Skeleton({ className }: Readonly<{ className?: string }>) {
    return <div aria-hidden="true" className={cx('dp-skeleton', className)} />;
}

export function LoadingIndicator({ label = 'Loading…' }: Readonly<{ label?: ReactNode }>) {
    return (
        <div
            aria-busy="true"
            aria-live="polite"
            className="flex items-center gap-2 py-6 text-sm text-fg-muted"
            role="status"
        >
            <Spinner size={16} /> {label}
        </div>
    );
}

/* ----------------------------------- Tabs ---------------------------------- */

export type TabItem<T extends string> = Readonly<{ value: T; label: ReactNode; count?: number }>;
export function Tabs<T extends string>({
    items,
    value,
    onChange,
    className,
}: Readonly<{ items: readonly TabItem<T>[]; value: T; onChange: (value: T) => void; className?: string }>) {
    return (
        <div className={cx('flex gap-1 rounded-xl bg-surface-2 p-1', className)} role="tablist">
            {items.map((item) => {
                const selected = item.value === value;
                return (
                    <button
                        aria-selected={selected}
                        className={cx(
                            'flex h-8 items-center gap-2 rounded-lg px-3 text-[13px] font-medium transition-colors',
                            selected ? 'bg-surface text-fg shadow-sm' : 'text-fg-muted hover:text-fg',
                        )}
                        key={item.value}
                        onClick={() => onChange(item.value)}
                        role="tab"
                        type="button"
                    >
                        {item.label}
                        {item.count !== undefined ? (
                            <span
                                className={cx(
                                    'tnum rounded-full px-1.5 text-[11px]',
                                    selected ? 'bg-accent-soft text-accent' : 'bg-surface-3 text-fg-faint',
                                )}
                            >
                                {item.count}
                            </span>
                        ) : null}
                    </button>
                );
            })}
        </div>
    );
}

/* ---------------------------------- Modal ---------------------------------- */

export function Modal({
    open,
    onClose,
    title,
    description,
    children,
    footer,
    size = 'md',
    tone,
}: Readonly<{
    open: boolean;
    onClose: () => void;
    title: ReactNode;
    description?: ReactNode;
    children?: ReactNode;
    footer?: ReactNode;
    size?: 'sm' | 'md' | 'lg';
    tone?: Tone;
}>) {
    useEffect(() => {
        if (!open) return;
        const onKey = (event: KeyboardEvent) => {
            if (event.key === 'Escape') onClose();
        };
        window.addEventListener('keydown', onKey);
        const previousOverflow = document.body.style.overflow;
        document.body.style.overflow = 'hidden';
        return () => {
            window.removeEventListener('keydown', onKey);
            document.body.style.overflow = previousOverflow;
        };
    }, [open, onClose]);
    if (!open) return null;
    // Portal to <body>: animated ancestors create containing blocks that would otherwise trap position: fixed.
    return createPortal(
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
            <button
                aria-label="Close dialog"
                className="absolute inset-0 bg-black/45 backdrop-blur-[2px]"
                onClick={onClose}
                type="button"
            />
            <div
                aria-modal="true"
                className={cx(
                    'dp-fade-up relative w-full overflow-hidden rounded-2xl border border-border bg-surface shadow-pop',
                    size === 'sm' && 'max-w-md',
                    size === 'md' && 'max-w-xl',
                    size === 'lg' && 'max-w-3xl',
                )}
                role="dialog"
            >
                {tone ? <div className={cx('h-1', barFill[tone])} /> : <div className="brand-gradient h-1" />}
                <div className="flex items-start justify-between gap-4 px-6 pt-5">
                    <div>
                        <h2 className="text-lg font-semibold text-fg">{title}</h2>
                        {description ? <p className="mt-1 text-sm text-fg-muted">{description}</p> : null}
                    </div>
                    <IconButton label="Close" onClick={onClose} size="sm">
                        <Icons.X size={16} />
                    </IconButton>
                </div>
                <div className="px-6 py-5">{children}</div>
                {footer ? (
                    <div className="flex justify-end gap-2 border-t border-border bg-surface-2 px-6 py-4">{footer}</div>
                ) : null}
            </div>
        </div>,
        document.body,
    );
}

/* ------------------------------- Copy button ------------------------------- */

export function CopyButton({
    value,
    label = 'Copy',
    size = 'sm',
}: Readonly<{ value: string; label?: string; size?: ButtonSize }>) {
    const [copied, setCopied] = useState(false);
    useEffect(() => {
        if (!copied) return;
        const handle = window.setTimeout(() => setCopied(false), 1500);
        return () => window.clearTimeout(handle);
    }, [copied]);
    return (
        <Button
            icon={copied ? <Icons.Check size={14} /> : <Icons.Copy size={14} />}
            onClick={() => {
                void navigator.clipboard?.writeText(value).then(() => setCopied(true));
            }}
            size={size}
            variant="ghost"
        >
            {copied ? 'Copied' : label}
        </Button>
    );
}

/* ------------------------------- Key/value list ---------------------------- */

export function KeyValue({
    items,
    className,
}: Readonly<{ items: readonly Readonly<{ label: ReactNode; value: ReactNode }>[]; className?: string }>) {
    return (
        <dl className={cx('grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 text-sm', className)}>
            {items.map((item, index) => (
                <div className="contents" key={index}>
                    <dt className="text-fg-muted">{item.label}</dt>
                    <dd className="min-w-0 truncate font-medium text-fg">{item.value}</dd>
                </div>
            ))}
        </dl>
    );
}

/* ---------------------------------- Table ---------------------------------- */

export function DataTable({ className, ...props }: ComponentPropsWithoutRef<'table'>) {
    return (
        <div className="scrollbar-thin w-full overflow-x-auto">
            <table
                {...props}
                className={cx(
                    'w-full border-collapse text-sm [&_td]:border-t [&_td]:border-border [&_td]:px-3 [&_td]:py-2.5 [&_th]:px-3 [&_th]:py-2 [&_th]:text-left [&_th]:text-xs [&_th]:font-semibold [&_th]:tracking-wide [&_th]:text-fg-muted [&_th]:uppercase',
                    className,
                )}
            />
        </div>
    );
}

/* ---------------------------------- Kbd/Code ------------------------------- */

export function Code({ children, className }: Readonly<{ children: ReactNode; className?: string }>) {
    return (
        <code className={cx('rounded-md bg-surface-3 px-1.5 py-0.5 font-mono text-[12.5px] text-fg', className)}>
            {children}
        </code>
    );
}

export function Mono({ children, className }: Readonly<{ children: ReactNode; className?: string }>) {
    return <span className={cx('font-mono text-[12.5px]', className)}>{children}</span>;
}

export function shortId(id: string) {
    return id.length > 12 ? `${id.slice(0, 8)}…` : id;
}

/* -------------------------------- Page header ------------------------------ */

export function PageHeader({
    title,
    description,
    actions,
    eyebrow,
}: Readonly<{ title: ReactNode; description?: ReactNode; actions?: ReactNode; eyebrow?: ReactNode }>) {
    return (
        <div className="mb-6 flex flex-wrap items-end justify-between gap-4">
            <div className="min-w-0">
                {eyebrow ? (
                    <div className="mb-1 text-xs font-semibold tracking-wide text-accent uppercase">{eyebrow}</div>
                ) : null}
                <h1 className="text-2xl font-bold text-fg">{title}</h1>
                {description ? <p className="mt-1 max-w-2xl text-sm text-fg-muted">{description}</p> : null}
            </div>
            {actions ? <div className="flex flex-wrap items-center gap-2">{actions}</div> : null}
        </div>
    );
}

/* ---------------------------------- Divider -------------------------------- */

export function Divider({ className }: Readonly<{ className?: string }>) {
    return <hr className={cx('border-border', className)} />;
}
