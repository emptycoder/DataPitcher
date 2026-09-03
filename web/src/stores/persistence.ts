import type { StateStorage } from 'zustand/middleware';

/** Storage is injected from the composition root so stores never touch browser storage directly. */
let injected: StateStorage | null = null;

const memory = new Map<string, string>();
const memoryStorage: StateStorage = {
  getItem: (name) => memory.get(name) ?? null,
  setItem: (name, value) => {
    memory.set(name, value);
  },
  removeItem: (name) => {
    memory.delete(name);
  },
};

export function injectStorage(storage: StateStorage | null) {
  injected = storage;
}

export function resolveStorage(): StateStorage {
  return injected ?? memoryStorage;
}
