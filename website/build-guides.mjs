// Regenerate the static guides after editing their repository Markdown sources.
import { readFile, writeFile, copyFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
const directory = path.dirname(fileURLToPath(import.meta.url));
const root = path.dirname(directory);
const escape = text => text.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
const inline = text => escape(text).replace(/\[([^\]]+)\]\(([^)]+)\)/g, (_, label, target) => {
  const local = { LICENSE: 'license.txt', 'THIRD-PARTY-NOTICES.md': 'third-party-notices.txt' };
  const href = local[target] || (/^https:\/\//.test(target) ? target : 'https://github.com/developersenturk/DevWorkspace/blob/master/' + target);
  return `<a href="${href}">${label}</a>`;
}).replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>').replace(/`([^`]+)`/g, '<code>$1</code>');
function markdown(source) {
  let html = '', code = false, list = false;
  for (const line of source.split(/\r?\n/)) {
    if (line.startsWith('```')) {
      if (list) { html += '</ul>'; list = false; }
      html += code ? '</code></pre>' : '<pre><code>'; code = !code; continue;
    }
    if (code) { html += escape(line) + '\n'; continue; }
    if (line.startsWith('- ')) { if (!list) html += '<ul>'; list = true; html += '<li>' + inline(line.slice(2)) + '</li>'; continue; }
    if (list) { html += '</ul>'; list = false; }
    if (!line.trim()) continue;
    const heading = line.match(/^(#{1,3}) (.+)$/);
    html += heading ? `<h${heading[1].length}>${inline(heading[2])}</h${heading[1].length}>` : '<p>' + inline(line) + '</p>';
  }
  return html + (list ? '</ul>' : '');
}
for (const [file, source, title] of [['privacy', 'docs/privacy.md', 'Privacy and sharing'], ['contributing', 'CONTRIBUTING.md', 'Contributing to DevWorkspace']]) {
  const body = markdown(await readFile(path.join(root, source), 'utf8'));
  await writeFile(path.join(directory, 'dist', file + '.html'), `<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${title} — DevWorkspace</title><link rel="stylesheet" href="style.css"><style>main{max-width:850px!important;padding-block:55px}h1{font-size:40px;letter-spacing:-1px;line-height:1.2;margin-bottom:30px}h2{font-size:26px;letter-spacing:-.5px;margin-top:40px}main p,main li{color:var(--muted)}main a{color:var(--accent);text-decoration:underline;overflow-wrap:anywhere}pre{padding:20px;background:var(--surface);border:1px solid var(--line);overflow:auto}code{font-family:Consolas,monospace;font-size:14px}</style><header class="wrap nav"><a class="brand" href="index.html">DevWorkspace</a><a href="index.html">Home / Ana sayfa</a></header><main class="wrap">${body}</main></html>`);
}
await copyFile(path.join(root, 'LICENSE'), path.join(directory, 'dist/license.txt'));
await copyFile(path.join(root, 'THIRD-PARTY-NOTICES.md'), path.join(directory, 'dist/third-party-notices.txt'));
console.log('Static guides regenerated.');
