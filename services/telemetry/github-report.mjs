import { writeFile } from 'node:fs/promises';

const url = new URL(process.env.TELEMETRY_SUMMARY_URL);
if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash || url.pathname !== '/v1/summary') throw new Error('Invalid summary URL');
if ((process.env.TELEMETRY_REPORT_TOKEN?.length ?? 0) < 32) throw new Error('Missing report token');
const response = await fetch(url, { headers: { authorization: `Bearer ${process.env.TELEMETRY_REPORT_TOKEN}` }, redirect: 'error', signal: AbortSignal.timeout(15000) });
if (!response.ok) throw new Error(`Summary returned HTTP ${response.status}`);
const source = await response.text();
if (source.length > 100000) throw new Error('Summary too large');
const data = JSON.parse(source);
const count = value => { if (!Number.isSafeInteger(value) || value < 0) throw new Error('Invalid count'); return value; };
if (data.protocol !== 1 || !Array.isArray(data.versions) || Number.isNaN(Date.parse(data.generatedAt))) throw new Error('Invalid summary');
// Only aggregate fields can reach GitHub, even if the server response changes.
const summary = {
  generatedAt: new Date(data.generatedAt).toISOString(),
  totalInstallations: count(data.totalInstallations),
  active24Hours: count(data.active24Hours),
  active7Days: count(data.active7Days),
  active30Days: count(data.active30Days),
  versions: data.versions.map(row => {
    if (typeof row.version !== 'string' || !/^\d{1,5}(\.\d{1,5}){1,3}(-[A-Za-z0-9.-]{1,32})?$/.test(row.version)) throw new Error('Invalid version');
    return { version: row.version, installations: count(row.installations) };
  })
};
const report = `# DevWorkspace kurulum istatistikleri

Güncelleme (UTC): ${summary.generatedAt}

| Bilgi | Sayı |
| --- | ---: |
| Bildirim gönderen toplam kurulum | ${summary.totalInstallations} |
| Son 24 saatte aktif | ${summary.active24Hours} |
| Son 7 günde aktif | ${summary.active7Days} |
| Son 30 günde aktif | ${summary.active30Days} |

## Son bildirilen sürümler

| Sürüm | Kurulum |
| --- | ---: |
${summary.versions.map(row => `| ${row.version} | ${row.installations} |`).join('\n')}

Bu sayılar istatistik paylaşımına izin veren kurulumları kapsar. Kurulum kimliği Windows kullanıcı profiline bağlıdır; tekrar kurulum/sıfırlama yeni kayıt oluşturabilir. Kaldırılmış uygulamalar toplamda kalır. Bildirimler taklit edilebildiği için bunlar lisans veya kesin kullanıcı sayıları değildir.

Kurulum kimlikleri bu repoya aktarılmaz. Günlük raporlar Git geçmişinden karşılaştırılabilir.
`;
await writeFile('README.md', report);
await writeFile('summary.json', JSON.stringify(summary, null, 2) + '\n');
