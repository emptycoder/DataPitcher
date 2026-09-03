export type SqlServerAuth =
    | 'sql-login'
    | 'integrated'
    | 'entra-password'
    | 'entra-integrated'
    | 'entra-interactive'
    | 'entra-managed-identity'
    | 'entra-service-principal'
    | 'entra-default'
    | 'entra-device-code'
    | 'entra-workload-identity';
export type PostgresAuth = 'password';
export type AuthMethod = SqlServerAuth | PostgresAuth;

export type ConnectionDetails = Readonly<{
    providerId: string;
    host: string;
    port: string;
    database: string;
    auth: AuthMethod;
    /** User name, user principal name, or client ID depending on the login method. */
    username: string;
    /** Password or client secret depending on the login method. */
    password: string;
    encrypt: boolean;
    trustServerCertificate: boolean;
}>;

export type AuthOption = Readonly<{
    value: AuthMethod;
    label: string;
    group: 'Database' | 'Microsoft Entra ID';
    description: string;
    usernameLabel: string | null;
    usernameRequired: boolean;
    passwordLabel: string | null;
}>;

const sqlServerAuth: readonly AuthOption[] = [
    {
        value: 'sql-login',
        label: 'SQL Server login',
        group: 'Database',
        description: 'User name and password managed by SQL Server.',
        usernameLabel: 'User name',
        usernameRequired: true,
        passwordLabel: 'Password',
    },
    {
        value: 'integrated',
        label: 'Windows integrated',
        group: 'Database',
        description: 'The Windows identity of the API process (Kerberos or NTLM).',
        usernameLabel: null,
        usernameRequired: false,
        passwordLabel: null,
    },
    {
        value: 'entra-password',
        label: 'Entra password',
        group: 'Microsoft Entra ID',
        description: 'An Entra user principal name and password (no MFA).',
        usernameLabel: 'User principal name',
        usernameRequired: true,
        passwordLabel: 'Password',
    },
    {
        value: 'entra-integrated',
        label: 'Entra integrated',
        group: 'Microsoft Entra ID',
        description: 'The domain-joined Windows identity of the API process, federated with Entra.',
        usernameLabel: null,
        usernameRequired: false,
        passwordLabel: null,
    },
    {
        value: 'entra-interactive',
        label: 'Entra interactive (MFA)',
        group: 'Microsoft Entra ID',
        description:
            'Opens a browser sign-in with multi-factor authentication on the API host. Suitable for local runs only.',
        usernameLabel: 'User principal name (optional)',
        usernameRequired: false,
        passwordLabel: null,
    },
    {
        value: 'entra-device-code',
        label: 'Entra device code (MFA)',
        group: 'Microsoft Entra ID',
        description: 'Prints a device code on the API host to complete MFA from another device.',
        usernameLabel: 'User principal name (optional)',
        usernameRequired: false,
        passwordLabel: null,
    },
    {
        value: 'entra-managed-identity',
        label: 'Entra managed identity (MSI)',
        group: 'Microsoft Entra ID',
        description: 'The Azure managed identity of the API host. Enter a client ID to use a user-assigned identity.',
        usernameLabel: 'User-assigned client ID (optional)',
        usernameRequired: false,
        passwordLabel: null,
    },
    {
        value: 'entra-service-principal',
        label: 'Entra service principal',
        group: 'Microsoft Entra ID',
        description: 'An application registration authenticating with a client secret.',
        usernameLabel: 'Application (client) ID',
        usernameRequired: true,
        passwordLabel: 'Client secret',
    },
    {
        value: 'entra-workload-identity',
        label: 'Entra workload identity',
        group: 'Microsoft Entra ID',
        description: 'Federated identity of the API workload (for example AKS).',
        usernameLabel: 'Client ID (optional)',
        usernameRequired: false,
        passwordLabel: null,
    },
    {
        value: 'entra-default',
        label: 'Entra default credential',
        group: 'Microsoft Entra ID',
        description:
            'Tries environment, workload and managed identities, then Azure CLI and Visual Studio credentials.',
        usernameLabel: 'Client ID (optional)',
        usernameRequired: false,
        passwordLabel: null,
    },
];
const postgresAuth: readonly AuthOption[] = [
    {
        value: 'password',
        label: 'User name and password',
        group: 'Database',
        description: 'Standard PostgreSQL password authentication.',
        usernameLabel: 'User name',
        usernameRequired: true,
        passwordLabel: 'Password',
    },
];

const entraKeywords: Readonly<Record<Exclude<SqlServerAuth, 'sql-login' | 'integrated'>, string>> = {
    'entra-password': 'Active Directory Password',
    'entra-integrated': 'Active Directory Integrated',
    'entra-interactive': 'Active Directory Interactive',
    'entra-device-code': 'Active Directory Device Code Flow',
    'entra-managed-identity': 'Active Directory Managed Identity',
    'entra-service-principal': 'Active Directory Service Principal',
    'entra-workload-identity': 'Active Directory Workload Identity',
    'entra-default': 'Active Directory Default',
};

export function authOptionsFor(providerId: string): readonly AuthOption[] {
    return providerId === 'postgresql' ? postgresAuth : sqlServerAuth;
}

export function authOption(details: ConnectionDetails): AuthOption {
    return (
        authOptionsFor(details.providerId).find((option) => option.value === details.auth) ??
        authOptionsFor(details.providerId)[0]!
    );
}

export function defaultConnectionDetails(providerId: string): ConnectionDetails {
    const postgres = providerId === 'postgresql';
    return {
        providerId,
        host: 'localhost',
        port: postgres ? '5432' : '1433',
        database: '',
        auth: postgres ? 'password' : 'sql-login',
        username: '',
        password: '',
        encrypt: true,
        trustServerCertificate: true,
    };
}

/** Switches provider while keeping whatever the operator already typed that still applies. */
export function withProvider(details: ConnectionDetails, providerId: string): ConnectionDetails {
    const defaults = defaultConnectionDetails(providerId);
    const portIsDefault = details.port === defaultConnectionDetails(details.providerId).port;
    const authStillValid = authOptionsFor(providerId).some((option) => option.value === details.auth);
    return {
        ...details,
        providerId,
        port: portIsDefault ? defaults.port : details.port,
        auth: authStillValid ? details.auth : defaults.auth,
    };
}

function quote(value: string): string {
    return /[;'"=\s]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

export function validateConnectionDetails(details: ConnectionDetails): string | null {
    if (!details.host.trim()) return 'Enter the server host.';
    if (details.port.trim() && !/^\d{1,5}$/.test(details.port.trim())) return 'Port must be a number.';
    if (!details.database.trim()) return 'Enter the database name.';
    const option = authOption(details);
    if (option.usernameRequired && !details.username.trim()) return `Enter the ${option.usernameLabel!.toLowerCase()}.`;
    if (option.passwordLabel && !details.password) return `Enter the ${option.passwordLabel.toLowerCase()}.`;
    return null;
}

export function buildConnectionString(
    details: ConnectionDetails,
    options: Readonly<{ maskPassword?: boolean }> = {},
): string {
    const password = options.maskPassword ? '••••••••' : details.password;
    const host = details.host.trim();
    const port = details.port.trim();
    const user = details.username.trim();
    const parts: string[] = [];
    if (details.providerId === 'postgresql') {
        parts.push(`Host=${quote(host)}`);
        if (port) parts.push(`Port=${port}`);
        parts.push(`Database=${quote(details.database.trim())}`);
        parts.push(`Username=${quote(user)}`);
        parts.push(`Password=${quote(password)}`);
        parts.push(`SSL Mode=${details.encrypt ? 'Require' : 'Prefer'}`);
        if (details.encrypt && details.trustServerCertificate) parts.push('Trust Server Certificate=true');
        return parts.join(';');
    }
    parts.push(`Server=${quote(port ? `${host},${port}` : host)}`);
    parts.push(`Database=${quote(details.database.trim())}`);
    const option = authOption(details);
    if (details.auth === 'sql-login') {
        parts.push(`User Id=${quote(user)}`, `Password=${quote(password)}`);
    } else if (details.auth === 'integrated') {
        parts.push('Integrated Security=True');
    } else {
        parts.push(`Authentication=${entraKeywords[details.auth as keyof typeof entraKeywords]}`);
        if (user && option.usernameLabel) parts.push(`User Id=${quote(user)}`);
        if (option.passwordLabel) parts.push(`Password=${quote(password)}`);
    }
    parts.push(`Encrypt=${details.encrypt ? 'True' : 'False'}`);
    parts.push(`TrustServerCertificate=${details.trustServerCertificate ? 'True' : 'False'}`);
    return parts.join(';');
}
