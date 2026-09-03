import { describe, expect, it } from 'vitest';
import { coerceParameterValue, parameterNamesIn, validateParameterValue } from './selections';

describe('selection parameters', () => {
  it('finds @parameters in first-seen order without duplicates', () => {
    expect(parameterNamesIn('SELECT id FROM app.orders WHERE a = @since AND b = @customer OR c = @since')).toEqual(['since', 'customer']);
    expect(parameterNamesIn('SELECT 1')).toEqual([]);
  });

  it('coerces values by kind', () => {
    expect(coerceParameterValue('int', '42')).toBe(42);
    expect(coerceParameterValue('decimal', '1.5')).toBe(1.5);
    expect(coerceParameterValue('boolean', 'true')).toBe(true);
    expect(coerceParameterValue('guid', 'abc')).toBe('abc');
  });

  it('validates values by kind', () => {
    expect(validateParameterValue('int', '4x')).toBe('Enter a whole number.');
    expect(validateParameterValue('int', '4')).toBeNull();
    expect(validateParameterValue('boolean', 'maybe')).toBe('Enter true or false.');
    expect(validateParameterValue('date', '2026-01-01')).toBeNull();
    expect(validateParameterValue('guid', '00000000-0000-0000-0000-000000000000')).toBeNull();
    expect(validateParameterValue('string', '')).toBe('Enter a value.');
  });
});
