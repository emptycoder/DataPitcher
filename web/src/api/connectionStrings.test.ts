import { describe, expect, it } from 'vitest';
import {
    authOptionsFor,
    buildConnectionString,
    defaultConnectionDetails,
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
