import { handle } from './core.mjs';
import { createStore } from './sql-store.mjs';

export default {
  async fetch(request, env) {
    try {
      if (new URL(request.url).pathname === '/v1/heartbeat') {
        // Platform protection is isolated here; IP addresses are never persisted.
        if (!env.INGEST_LIMITER) return new Response('Unavailable', { status: 503 });
        const { success } = await env.INGEST_LIMITER.limit({ key: request.headers.get('CF-Connecting-IP') ?? 'unknown' });
        if (!success) return new Response('Rate limited', { status: 429, headers: { 'retry-after': '60' } });
      }
      const store = createStore({
        run: (sql, ...args) => env.DB.prepare(sql).bind(...args).run(),
        all: async (sql, ...args) => (await env.DB.prepare(sql).bind(...args).all()).results
      });
      return await handle(request, { store, reportToken: env.REPORT_TOKEN });
    } catch { return new Response('Unavailable', { status: 503, headers: { 'cache-control': 'no-store' } }); }
  }
};
