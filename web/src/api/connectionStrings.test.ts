import { describe, expect, it } from 'vitest';
import {
    authOptionsFor,
    buildConnectionString,
    defaultConnectionDetails,
    needsStoredPassword,
    parseConnectionString,
    tokenizeConnectionString,
    validateConnectionDetails,
    withProvider,
} from './connectionStrings';

describe('connection strings', () => {
    const sql = { ...defaultConnectionDetails('sqlserver'), database: 'app' };

    it('builds a SQL Server login string with encryption flags', () => {
        const details = { ...sql, username: 'sa', password: 'p;w' };
        expect(buildConnectionString(details)).toBe(
            'Server=localhost,1433;Database=app;User Id=sa;Password="p;w";Encrypt=True;TrustServerCertificate=True',
        );
        expect(buildConnectionString(details, { maskPassword: true })).toContain('Password=••••••••');
    });

    it('covers every Entra login option', () => {
        expect(buildConnectionString({ ...sql, auth: 'integrated' })).toContain('Integrated Security=True');
        expect(buildConnectionString({ ...sql, auth: 'entra-password', username: 'a@b.com', password: 'x' })).toContain(
            'Authentication=Active Directory Password;User Id=a@b.com;Password=x',
        );
        expect(buildConnectionString({ ...sql, auth: 'entra-integrated' })).toContain(
            'Authentication=Active Directory Integrated',
        );
        expect(buildConnectionString({ ...sql, auth: 'entra-interactive', username: 'a@b.com' })).toContain(
            'Authentication=Active Directory Interactive;User Id=a@b.com',
        );
        expect(buildConnectionString({ ...sql, auth: 'entra-device-code' })).toContain(
            'Authentication=Active Directory Device Code Flow',
        );
        expect(buildConnectionString({ ...sql, auth: 'entra-managed-identity' })).toContain(
            'Authentication=Active Directory Managed Identity;Encrypt',
        );
        expect(buildConnectionString({ ...sql, auth: 'entra-managed-identity', username: 'client-id' })).toContain(
            'Authentication=Active Directory Managed Identity;User Id=client-id',
        );
        expect(
            buildConnectionString({ ...sql, auth: 'entra-service-principal', username: 'app-id', password: 'secret' }),
        ).toContain('Authentication=Active Directory Service Principal;User Id=app-id;Password=secret');
        expect(buildConnectionString({ ...sql, auth: 'entra-workload-identity' })).toContain(
            'Authentication=Active Directory Workload Identity',
        );
        expect(buildConnectionString({ ...sql, auth: 'entra-default' })).toContain(
            'Authentication=Active Directory Default',
        );
        expect(authOptionsFor('sqlserver')).toHaveLength(10);
    });

    it('validates required fields per login method', () => {
        expect(validateConnectionDetails({ ...sql, auth: 'integrated' })).toBeNull();
        expect(validateConnectionDetails(sql)).toBe('Enter the user name.');
        expect(validateConnectionDetails({ ...sql, auth: 'entra-service-principal', username: 'id' })).toBe(
            'Enter the client secret.',
        );
        expect(validateConnectionDetails({ ...sql, auth: 'entra-managed-identity' })).toBeNull();
    });

    it('builds a PostgreSQL string and switches providers sensibly', () => {
        const details = {
            ...defaultConnectionDetails('postgresql'),
            database: 'app',
            username: 'app',
            password: 'secret',
        };
        expect(buildConnectionString(details)).toBe(
            'Host=localhost;Port=5432;Database=app;Username=app;Password=secret;SSL Mode=Require;Trust Server Certificate=true',
        );
        const switched = withProvider(details, 'sqlserver');
        expect(switched.port).toBe('1433');
        expect(switched.auth).toBe('sql-login');
        expect(withProvider({ ...details, port: '6000' }, 'sqlserver').port).toBe('6000');
    });
});

describe('connection string parsing', () => {
    it('tokenizes quoted values and doubled quotes', () => {
        expect(
            tokenizeConnectionString('Server=a;Password="p;""w";Encrypt=True; Trust Server Certificate = yes;;'),
        ).toEqual([
            ['Server', 'a'],
            ['Password', 'p;"w'],
            ['Encrypt', 'True'],
            ['Trust Server Certificate', 'yes'],
        ]);
        expect(tokenizeConnectionString("Host=x;Password='it''s'")).toEqual([
            ['Host', 'x'],
            ['Password', "it's"],
        ]);
    });

    it('reads a SQL Server login string back into the form without the password', () => {
        const { details, unsupportedKeys } = parseConnectionString(
            'sqlserver',
            'Server=tcp:db.internal,1433;Database=app;User Id=sa;Password=secret;Encrypt=True;TrustServerCertificate=False',
        );
        expect(details).toEqual({
            providerId: 'sqlserver',
            host: 'db.internal',
            port: '1433',
            database: 'app',
            auth: 'sql-login',
            username: 'sa',
            password: '',
            encrypt: true,
            trustServerCertificate: false,
        });
        expect(unsupportedKeys).toEqual([]);
    });

    it('round-trips every login method and keeps instance names', () => {
        const base = { ...defaultConnectionDetails('sqlserver'), database: 'app', password: '' };
        for (const option of authOptionsFor('sqlserver')) {
            const details = { ...base, auth: option.value, username: option.usernameLabel ? 'someone' : '' };
            expect(parseConnectionString('sqlserver', buildConnectionString(details)).details).toEqual(details);
        }
        const localDb = parseConnectionString(
            'sqlserver',
            String.raw`Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=Shop;Integrated Security=SSPI`,
        );
        expect(localDb.details.host).toBe(String.raw`(localdb)\MSSQLLocalDB`);
        expect(localDb.details.port).toBe('');
        expect(localDb.details.database).toBe('Shop');
        expect(localDb.details.auth).toBe('integrated');
    });

    it('reports keys the form cannot show', () => {
        const parsed = parseConnectionString(
            'sqlserver',
            'Server=db;Database=app;User Id=sa;Application Name=DataPitcher;MultipleActiveResultSets=True',
        );
        expect(parsed.unsupportedKeys).toEqual(['Application Name', 'MultipleActiveResultSets']);
    });

    it('reads a PostgreSQL string and its SSL mode', () => {
        const { details, unsupportedKeys } = parseConnectionString(
            'postgresql',
            'host=pg.internal;port=6432;database=app;username=app;pwd=x;SSL Mode=Prefer;Timeout=5',
        );
        expect(details).toMatchObject({
            providerId: 'postgresql',
            host: 'pg.internal',
            port: '6432',
            database: 'app',
            auth: 'password',
            username: 'app',
            password: '',
            encrypt: false,
        });
        expect(unsupportedKeys).toEqual(['Timeout']);
        const pg = { ...defaultConnectionDetails('postgresql'), database: 'app', username: 'app' };
        expect(parseConnectionString('postgresql', buildConnectionString(pg)).details).toEqual(pg);
    });

    it('omits an empty password so the API can append the stored one', () => {
        const sql = { ...defaultConnectionDetails('sqlserver'), database: 'app', username: 'sa' };
        expect(buildConnectionString(sql)).toBe(
            'Server=localhost,1433;Database=app;User Id=sa;Encrypt=True;TrustServerCertificate=True',
        );
        expect(buildConnectionString(sql, { maskPassword: true })).toContain('Password=••••••••');
        expect(needsStoredPassword(sql)).toBe(true);
        expect(needsStoredPassword({ ...sql, auth: 'integrated' })).toBe(false);
        expect(validateConnectionDetails(sql)).toBe('Enter the password.');
        expect(validateConnectionDetails(sql, { passwordOptional: true })).toBeNull();
    });
});
