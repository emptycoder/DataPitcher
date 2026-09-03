import { useState, type FormEvent } from 'react';
import { Alert, Button, Code, Field, SecretInput, Select, TextArea, TextInput, cx } from '../ui';
import { Icons } from '../ui/icons';
import { defaultDevelopmentSignIn, type DevelopmentSignIn } from './devSession';
import { mintDevelopmentToken } from './devToken';
import { decodeJwt, isExpired } from './jwt';
import { roleDescriptions, roleNames, type RoleName } from './roles';

export type SignInResult = Readonly<{ token: string; remember: DevelopmentSignIn | null }>;

type Mode = 'development' | 'token';

export function SignInScreen({
    initial,
    onSignedIn,
}: Readonly<{ initial: DevelopmentSignIn | null; onSignedIn: (result: SignInResult) => void }>) {
    const [mode, setMode] = useState<Mode>('development');
    const [settings, setSettings] = useState<DevelopmentSignIn>(initial ?? defaultDevelopmentSignIn);
    const [remember, setRemember] = useState(true);
    const [pastedToken, setPastedToken] = useState('');
    const [error, setError] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);

    function toggleRole(role: RoleName) {
        setSettings((current) => ({
            ...current,
            roles: current.roles.includes(role)
                ? current.roles.filter((value) => value !== role)
                : [...current.roles, role],
        }));
    }

    async function submit(event: FormEvent) {
        event.preventDefault();
        setError(null);
        setBusy(true);
        try {
            if (mode === 'token') {
                const token = pastedToken.trim();
                const claims = decodeJwt(token);
                if (!claims) throw new Error('That does not look like a JWT.');
                if (isExpired(claims)) throw new Error('That token has already expired.');
                onSignedIn({ token, remember: null });
                return;
            }
            if (settings.roles.length === 0) throw new Error('Choose at least one role.');
            const token = await mintDevelopmentToken(settings);
            onSignedIn({ token, remember: remember ? settings : null });
        } catch (caught) {
            setError(caught instanceof Error ? caught.message : 'Unable to sign in.');
        } finally {
            setBusy(false);
        }
    }

    return (
        <div className="flex min-h-screen items-center justify-center bg-bg p-6">
            <div className="pointer-events-none fixed inset-0 overflow-hidden">
                <div className="absolute -top-40 -left-40 size-[520px] rounded-full bg-accent/15 blur-3xl" />
                <div className="absolute -right-40 -bottom-40 size-[520px] rounded-full bg-accent-2/15 blur-3xl" />
            </div>
            <div className="dp-fade-up relative grid w-full max-w-4xl overflow-hidden rounded-3xl border border-border bg-surface shadow-pop md:grid-cols-[1.05fr_1fr]">
                <aside className="brand-gradient relative hidden flex-col justify-between p-10 text-white md:flex">
                    <div>
                        <div className="flex items-center gap-3">
                            <div className="flex size-11 items-center justify-center rounded-2xl bg-white/15 backdrop-blur">
                                <Icons.Rocket size={24} />
                            </div>
                            <div className="text-2xl font-bold tracking-tight">DataPitcher</div>
                        </div>
                        <p className="mt-8 text-lg leading-relaxed text-white/90">
                            Move an exact subset of rows between databases with the smallest referentially complete
                            dependency set, a sealed reviewable plan, and live transfer progress.
                        </p>
                    </div>
                    <ol className="space-y-3 text-sm text-white/85">
                        {[
                            'Connect source and target',
                            'Scan and explore the schema',
                            'Select root rows with SQL',
                            'Seal a transfer plan',
                            'Transfer and watch progress',
                        ].map((step, index) => (
                            <li className="flex items-center gap-3" key={step}>
                                <span className="flex size-6 items-center justify-center rounded-full bg-white/20 text-xs font-bold">
                                    {index + 1}
                                </span>
                                {step}
                            </li>
                        ))}
                    </ol>
                </aside>

                <form className="p-8 md:p-10" onSubmit={submit}>
                    <h1 className="text-2xl font-bold text-fg">Sign in</h1>
                    <p className="mt-1 text-sm text-fg-muted">
                        The API accepts development tokens signed with its local signing key.
                    </p>

                    <div className="mt-6 flex gap-1 rounded-xl bg-surface-2 p-1" role="tablist">
                        {(
                            [
                                ['development', 'Development token'],
                                ['token', 'Paste a token'],
                            ] as const
                        ).map(([value, label]) => (
                            <button
                                aria-selected={mode === value}
                                className={cx(
                                    'flex-1 rounded-lg px-3 py-1.5 text-sm font-medium transition-colors',
                                    mode === value ? 'bg-surface text-fg shadow-sm' : 'text-fg-muted hover:text-fg',
                                )}
                                key={value}
                                onClick={() => setMode(value)}
                                role="tab"
                                type="button"
                            >
                                {label}
                            </button>
                        ))}
                    </div>

                    {mode === 'development' ? (
                        <div className="mt-6 grid gap-4">
                            <Field
                                hint={
                                    <>
                                        Prefilled with the local default used by <Code>scripts/dev.sh</Code>. Change it
                                        only if the API was started with a different{' '}
                                        <Code>Authentication__Development__SigningKey</Code>.
                                    </>
                                }
                                label="Signing key"
                                required
                            >
                                <SecretInput
                                    autoComplete="off"
                                    onChange={(event) => setSettings({ ...settings, signingKey: event.target.value })}
                                    placeholder="Paste the development signing key"
                                    value={settings.signingKey}
                                />
                            </Field>
                            <div className="grid grid-cols-2 gap-4">
                                <Field label="Subject" required>
                                    <TextInput
                                        onChange={(event) => setSettings({ ...settings, subject: event.target.value })}
                                        value={settings.subject}
                                    />
                                </Field>
                                <Field label="Token lifetime">
                                    <Select
                                        onChange={(event) =>
                                            setSettings({ ...settings, lifetimeMinutes: Number(event.target.value) })
                                        }
                                        value={String(settings.lifetimeMinutes)}
                                    >
                                        <option value="60">1 hour</option>
                                        <option value="480">8 hours</option>
                                        <option value="1440">24 hours</option>
                                    </Select>
                                </Field>
                            </div>
                            <fieldset>
                                <legend className="mb-2 text-[13px] font-medium text-fg-muted">Roles</legend>
                                <div className="grid grid-cols-2 gap-2">
                                    {roleNames.map((role) => {
                                        const checked = settings.roles.includes(role);
                                        const id = `role-${role}`;
                                        return (
                                            <label
                                                aria-label={role}
                                                htmlFor={id}
                                                className={cx(
                                                    'flex cursor-pointer items-start gap-2.5 rounded-xl border p-3 transition-colors',
                                                    checked
                                                        ? 'border-accent bg-accent-soft/60'
                                                        : 'border-border hover:border-border-strong',
                                                )}
                                                key={role}
                                            >
                                                <input
                                                    checked={checked}
                                                    className="mt-0.5 accent-accent"
                                                    id={id}
                                                    onChange={() => toggleRole(role)}
                                                    type="checkbox"
                                                />
                                                <span>
                                                    <span className="block text-sm font-semibold text-fg">{role}</span>
                                                    <span className="block text-xs leading-snug text-fg-muted">
                                                        {roleDescriptions[role]}
                                                    </span>
                                                </span>
                                            </label>
                                        );
                                    })}
                                </div>
                            </fieldset>
                            <details className="text-[13px] text-fg-muted">
                                <summary className="cursor-pointer select-none">Advanced (issuer and audience)</summary>
                                <div className="mt-3 grid grid-cols-2 gap-3">
                                    <Field label="Issuer">
                                        <TextInput
                                            onChange={(event) =>
                                                setSettings({ ...settings, issuer: event.target.value })
                                            }
                                            value={settings.issuer}
                                        />
                                    </Field>
                                    <Field label="Audience">
                                        <TextInput
                                            onChange={(event) =>
                                                setSettings({ ...settings, audience: event.target.value })
                                            }
                                            value={settings.audience}
                                        />
                                    </Field>
                                </div>
                            </details>
                            <label className="flex items-center gap-2 text-sm text-fg-muted">
                                <input
                                    checked={remember}
                                    className="accent-accent"
                                    onChange={(event) => setRemember(event.target.checked)}
                                    type="checkbox"
                                />
                                Keep me signed in on this browser tab
                            </label>
                        </div>
                    ) : (
                        <div className="mt-6 grid gap-4">
                            <Field
                                hint="A bearer token issued by a configured identity provider. It stays in memory only."
                                label="Access token"
                                required
                            >
                                <TextArea
                                    className="font-mono text-xs"
                                    onChange={(event) => setPastedToken(event.target.value)}
                                    rows={6}
                                    spellCheck={false}
                                    value={pastedToken}
                                />
                            </Field>
                        </div>
                    )}

                    {error ? (
                        <Alert className="mt-4" tone="danger">
                            {error}
                        </Alert>
                    ) : null}

                    <Button
                        block
                        className="mt-6"
                        icon={<Icons.Lock size={16} />}
                        loading={busy}
                        size="lg"
                        type="submit"
                        variant="primary"
                    >
                        Sign in
                    </Button>
                    <p className="mt-4 text-center text-xs text-fg-faint">
                        Start everything with <Code>./scripts/dev.sh</Code> from the repository root.
                    </p>
                </form>
            </div>
        </div>
    );
}
