import { createServer } from 'node:http';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';
import { handle, maxBodyBytes } from './core.mjs';
import { createStore } from './sql-store.mjs';

if ((process.env.REPORT_TOKEN?.length ?? 0) < 32) throw new Error('REPORT_TOKEN must have at least 32 characters');
const database = new DatabaseSync(process.env.DATABASE_PATH ?? 'telemetry.sqlite');
database.exec(readFileSync(new URL('./migrations/0001_installations.sql', import.meta.url), 'utf8'));
const store = createStore({
  run: (sql, ...args) => database.prepare(sql).run(...args),
  all: (sql, ...args) => database.prepare(sql).all(...args)
});
// Bind to loopback by default; put TLS and a rate-limiting reverse proxy in front in production.
createServer(async (incoming, outgoing) => {
  try {
    let length = 0;
    const chunks = [];
    for await (const chunk of incoming) {
      length += chunk.length;
      if (length > maxBodyBytes) { outgoing.writeHead(413).end(); return; }
      chunks.push(chunk);
    }
    const method = incoming.method ?? 'GET';
    const request = new Request(`http://localhost${incoming.url}`, { method, headers: incoming.headers,
      ...(!['GET', 'HEAD'].includes(method) ? { body: Buffer.concat(chunks) } : {}) });
    const response = await handle(request, { store, reportToken: process.env.REPORT_TOKEN });
    outgoing.writeHead(response.status, Object.fromEntries(response.headers));
    outgoing.end(Buffer.from(await response.arrayBuffer()));
  } catch { outgoing.writeHead(503).end(); }
}).listen(Number(process.env.PORT ?? 8787), process.env.HOST ?? '127.0.0.1');
