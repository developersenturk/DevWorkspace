// Web-standard request handler: no Cloudflare imports or runtime bindings.
const headers = { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' };
const json = (data, status = 200) => new Response(JSON.stringify(data), { status, headers });
export const maxBodyBytes = 1024;

async function authorized(request, secret) {
  if (!secret || secret.length < 32) return false;
  const hash = async value => new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value)));
  const a = await hash(request.headers.get('authorization') ?? '');
  const b = await hash(`Bearer ${secret}`);
  let difference = 0;
  for (let i = 0; i < a.length; i++) difference |= a[i] ^ b[i];
  return difference === 0;
}

async function readBody(request) {
  if (!request.body) throw new Error('empty');
  const reader = request.body.getReader();
  const chunks = [];
  let length = 0;
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      length += value.length;
      if (length > maxBodyBytes) { await reader.cancel(); throw new Error('large'); }
      chunks.push(value);
    }
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
}

export async function handle(request, { store, reportToken, now = () => new Date() }) {
  const url = new URL(request.url);
  if (url.pathname === '/health' && request.method === 'GET') return json({ status: 'ok', protocol: 1 });
  if (url.pathname === '/v1/heartbeat' && request.method === 'POST') {
    if (request.headers.get('content-type')?.split(';')[0].trim().toLowerCase() !== 'application/json') return json({ error: 'JSON required' }, 415);
    let body;
    try { body = await readBody(request); }
    catch (e) { return json({ error: 'Invalid body' }, e.message === 'large' ? 413 : 400); }
    if (!body || typeof body !== 'object' || Array.isArray(body) ||
        Object.keys(body).some(key => !['protocol', 'installationId', 'version'].includes(key)) ||
        body.protocol !== 1 || typeof body.installationId !== 'string' ||
        !/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(body.installationId) ||
        typeof body.version !== 'string' || !/^\d{1,5}(\.\d{1,5}){1,3}(-[A-Za-z0-9.-]{1,32})?$/.test(body.version)) return json({ error: 'Invalid payload' }, 400);
    // Timestamps come from the server. There is one row per installation, no event history.
    await store.record(body.installationId.toLowerCase(), body.version, now().toISOString());
    return new Response(null, { status: 204, headers });
  }
  if (url.pathname === '/v1/summary' && request.method === 'GET') {
    if (!await authorized(request, reportToken)) return json({ error: 'Unauthorized' }, 401);
    const date = now();
    return json({ protocol: 1, generatedAt: date.toISOString(), ...await store.summary(date) });
  }
  return json({ error: 'Not found' }, 404);
}
