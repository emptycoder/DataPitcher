import { z } from 'zod';

export async function parseJson<T>(response: Response, schema: z.ZodType<T>): Promise<T> {
  if (!response.ok) throw new Error(`Request failed: ${response.status}`);
  return schema.parse(await response.json());
}
