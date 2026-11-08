/** "0.1.0+ef84d6fe3ef4…" (version plus full git SHA) as "0.1.0+ef84d6f". */
export const shortVersion = (version: string) => version.replace(/\+([0-9a-f]{7})[0-9a-f]+$/, "+$1");
