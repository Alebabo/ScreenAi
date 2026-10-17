import { spawn } from 'node:child_process';
import { existsSync, readFileSync, mkdirSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const SYSTEM_PROMPT = `Antworte im Caveman-Ultra-Stil und in der Sprache der Nutzerfrage. Standard: 1–2 kurze Sätze oder Fragmente, höchstens 35 Wörter. Überschreite das nur, wenn der Nutzer ausdrücklich Details verlangt oder Korrektheit beziehungsweise Sicherheit mehr Text erfordert. Gib ausschließlich die direkte Antwort, notwendige Fakten, Befehle oder Code aus. Keine Begrüßung, Einleitung, Höflichkeitsfloskeln, Beispiele ohne Nachfrage, Wiederholung, Zusammenfassung oder Abschlussformel. Bewahre Zahlen, Bedingungen und Verneinungen exakt. Verändere keine Dateien und führe keine Befehle aus.`;

export function argumentsFor(provider) {
  switch (provider) {
    case 'gemini': return ['--input-format', 'stream-json', '--output-format', 'stream-json', '--mode', 'plan', '--sandbox', '--print-timeout', '180s'];
    case 'claude': return ['--print', '--output-format', 'json', '--append-system-prompt', SYSTEM_PROMPT, '--tools', '', '--disallowedTools', 'mcp__*', '--strict-mcp-config', '--setting-sources', '', '--no-session-persistence', '--no-chrome', '--permission-prompts', 'none'];
    case 'codex': return ['exec', '--ignore-user-config', '--skip-git-repo-check', '--sandbox', 'read-only', '--ephemeral', '--json', '-'];
    default: throw new Error('Unbekannter Anbieter.');
  }
}
export function cleanEnvironment(source) {
  const env = { ...source };
  for (const key of Object.keys(env)) {
    if (/^(OPENAI_API_KEY|CODEX_API_KEY|OPENAI_BASE_URL|ANTHROPIC_API_KEY|ANTHROPIC_AUTH_TOKEN|ANTHROPIC_BASE_URL|ANTHROPIC_CUSTOM_HEADERS|GOOGLE_API_KEY|GEMINI_API_KEY|GOOGLE_APPLICATION_CREDENTIALS|GOOGLE_CLOUD_PROJECT|GOOGLE_CLOUD_PROJECT_ID|GOOGLE_CLOUD_LOCATION|GOOGLE_GENAI_USE_VERTEXAI|GOOGLE_GENAI_USE_GCA|CLAUDE_CODE_USE_BEDROCK|CLAUDE_CODE_USE_VERTEX|CLAUDE_CODE_USE_FOUNDRY)$/i.test(key)) delete env[key];
  }
  return env;
}
export function parseAnswer(provider, stdout) {
  if (provider === 'gemini') {
    const events = stdout.split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
    const result = events.filter(event => event.event === 'result').at(-1)?.result;
    if (!result) throw new Error('Antigravity hat kein Ergebnis geliefert.');
    if (result.status !== 'SUCCESS') throw new Error(result.error || `Antigravity-Anfrage beendet: ${result.status || 'unbekannt'}.`);
    return result.response;
  }
  if (provider === 'codex') {
    const events = stdout.split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
    const error = events.find(e => e.type === 'error' || e.type === 'turn.failed');
    if (error) throw new Error(error.message || error.error?.message || 'Codex-Anfrage fehlgeschlagen.');
    return events.filter(e => e.type === 'item.completed' && e.item?.type === 'agent_message').map(e => e.item.text).join('\n');
  }
  const obj = JSON.parse(stdout);
  if (obj.is_error || obj.error) throw new Error(obj.error?.message || obj.result || 'Anfrage fehlgeschlagen.');
  return obj.result;
}
export function summarizeCliFailure(stderr, stdout, code) {
  const combined = `${stderr}\n${stdout}`;
  const reason = combined.match(/reasonMessage:\s*'([^']+)'/);
  if (reason) return reason[1];
  const lines = combined.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
  const preferred = lines.find(line => /^(Error authenticating:|Error:|Failed to |Not logged in)/i.test(line));
  return (preferred || lines.find(line => !/^Warning:/i.test(line)) || `CLI beendet (${code}). Bitte Anmeldung im Terminal prüfen.`)
    .replace(/^Error authenticating:\s*/i, '');
}
export function buildPrompt(question, history = []) {
  return `SYSTEMANWEISUNG (höchste Priorität):\n${SYSTEM_PROMPT}\n\n` +
    history.slice(-12).map(m => `${m.role}: ${m.text}`).join('\n') +
    `\nuser: ${question}`;
}
function executable(provider) {
  const names = { claude: '@anthropic-ai/claude-code', codex: '@openai/codex' };
  const roots = new Set([
    path.join(process.env.APPDATA || '', 'npm', 'node_modules'),
    path.join(path.dirname(process.execPath), 'node_modules'),
    process.env.NPM_CONFIG_PREFIX ? path.join(process.env.NPM_CONFIG_PREFIX, 'node_modules') : ''
  ]);
  for (const item of (process.env.PATH || '').split(path.delimiter)) {
    if (item) roots.add(path.join(item, 'node_modules'));
  }
  for (const root of roots) {
    if (!root) continue;
    if (!names[provider]) continue;
    const dir = path.join(root, names[provider]);
    if (!existsSync(path.join(dir, 'package.json'))) continue;
    const pkg = JSON.parse(readFileSync(path.join(dir, 'package.json'), 'utf8'));
    const bin = typeof pkg.bin === 'string' ? pkg.bin : pkg.bin?.[provider];
    if (!bin) continue;
    const file = path.resolve(dir, bin);
    if (existsSync(file)) return file.endsWith('.exe') ? [file, []] : [process.execPath, [file]];
  }

  const executableName = provider === 'gemini' ? 'agy' : provider;
  const native = [];
  for (const item of (process.env.PATH || '').split(path.delimiter)) {
    if (item) native.push(path.join(item, `${executableName}.exe`));
  }
  native.push(path.join(process.env.USERPROFILE || '', '.local', 'bin', `${executableName}.exe`));
  if (provider === 'gemini') {
    native.push(path.join(process.env.LOCALAPPDATA || '', 'agy', 'bin', 'agy.exe'));
    native.push(path.join(process.env.LOCALAPPDATA || '', 'Microsoft', 'WinGet', 'Links', 'agy.exe'));
  }
  if (provider === 'codex') {
    const codexRoot = path.join(process.env.LOCALAPPDATA || '', 'OpenAI', 'Codex', 'bin');
    if (existsSync(codexRoot)) {
      for (const item of readdirSync(codexRoot, { withFileTypes: true })) {
        if (item.isDirectory()) native.push(path.join(codexRoot, item.name, 'codex.exe'));
      }
    }
  }
  const file = native.find(existsSync);
  if (file) return [file, []];
  throw new Error(`${provider === 'gemini' ? 'Antigravity CLI (agy)' : provider} nicht gefunden. Bitte gemäß README installieren und einmal anmelden.`);
}

function terminateProcessTree(child) {
  if (!child?.pid || child.killed) return;
  if (process.platform === 'win32') {
    const taskkill = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'taskkill.exe');
    const killer = spawn(taskkill, ['/pid', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
    killer.on('error', () => { try { child.kill(); } catch {} });
  } else {
    try { child.kill('SIGKILL'); } catch {}
  }
}

async function capture(command, args, options, timeoutMs = 10000) {
  return await new Promise((resolve, reject) => {
    const child = spawn(command, args, { ...options, windowsHide: true, shell: false, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '', stderr = '', timedOut = false;
    child.stdout.setEncoding('utf8'); child.stderr.setEncoding('utf8');
    child.stdout.on('data', data => { stdout = (stdout + data).slice(-50000); });
    child.stderr.on('data', data => { stderr = (stderr + data).slice(-50000); });
    const timer = setTimeout(() => { timedOut = true; terminateProcessTree(child); }, timeoutMs);
    child.on('error', error => { clearTimeout(timer); reject(error); });
    child.on('close', code => {
      clearTimeout(timer);
      if (timedOut) return reject(new Error('Anmeldestatus konnte nicht rechtzeitig geprüft werden.'));
      resolve({ code, stdout, stderr });
    });
  });
}

async function assertSubscriptionAuth(provider, command, prefix, cwd, env) {
  if (provider === 'gemini') return;
  const args = provider === 'claude' ? ['auth', 'status'] : ['login', 'status'];
  const status = await capture(command, [...prefix, ...args], { cwd, env });
  if (status.code !== 0) throw new Error(`${provider}: Anmeldung fehlt oder konnte nicht geprüft werden.`);
  if (provider === 'claude') {
    let auth;
    try { auth = JSON.parse(status.stdout); } catch { throw new Error('Claude-Anmeldestatus ist nicht lesbar.'); }
    if (!auth.loggedIn || auth.authMethod !== 'claude.ai' || auth.apiProvider !== 'firstParty') {
      throw new Error('Claude nutzt keine Claude-Abo-Anmeldung. Bitte mit claude auth login anmelden.');
    }
  } else if (!/Logged in using ChatGPT/i.test(status.stdout + status.stderr)) {
    throw new Error('Codex nutzt keine ChatGPT-Anmeldung. Bitte mit codex login anmelden.');
  }
}
export async function run(request) {
  const args = argumentsFor(request.provider);
  if (typeof request.prompt !== 'string' || !request.prompt.trim()) throw new Error('Frage fehlt.');
  const history = Array.isArray(request.history) ? request.history : [];
  const prompt = buildPrompt(request.prompt, history);
  if (prompt.length > 100000) throw new Error('Gespräch zu lang. Bitte ein neues Fenster öffnen.');
  const [command, prefix] = executable(request.provider);
  const cwd = path.join(process.env.LOCALAPPDATA || process.cwd(), 'AIHub', 'Workspace');
  mkdirSync(cwd, { recursive: true });
  const env = cleanEnvironment(process.env);
  await assertSubscriptionAuth(request.provider, command, prefix, cwd, env);
  return await new Promise((resolve, reject) => {
    const child = spawn(command, [...prefix, ...args], { cwd, env, windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'pipe'] });
    let stdout = '', stderr = '', tooLarge = false, timedOut = false;
    child.stdout.setEncoding('utf8'); child.stderr.setEncoding('utf8');
    const timer = setTimeout(() => { timedOut = true; terminateProcessTree(child); }, 180000);
    child.stdout.on('data', data => { stdout += data; if (stdout.length > 4e6) { tooLarge = true; terminateProcessTree(child); } });
    child.stderr.on('data', data => { stderr = (stderr + data).slice(-8000); });
    child.on('error', err => { clearTimeout(timer); reject(err); });
    child.stdin.on('error', () => {});
    child.on('close', code => {
      clearTimeout(timer);
      if (timedOut) return reject(new Error('Zeitlimit nach 180 Sekunden erreicht.'));
      if (tooLarge) return reject(new Error('Antwort überschreitet das Größenlimit.'));
      if (code !== 0) {
        if (stdout.trim()) {
          try { parseAnswer(request.provider, stdout); }
          catch (error) { if (!(error instanceof SyntaxError)) return reject(error); }
        }
        return reject(new Error(summarizeCliFailure(stderr, stdout, code)));
      }
      try { const answer = parseAnswer(request.provider, stdout); if (!answer) throw new Error('Keine Textantwort erhalten.'); resolve(answer); } catch (e) { reject(e); }
    });
    child.stdin.end(request.provider === 'gemini'
      ? JSON.stringify({ event: 'user', message: { content: prompt } }) + '\n'
      : prompt);
  });
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  let input = ''; for await (const chunk of process.stdin) input += chunk;
  try { console.log(JSON.stringify({ type: 'answer', text: await run(JSON.parse(input)) })); }
  catch (error) { console.log(JSON.stringify({ type: 'error', text: error.message })); process.exitCode = 1; }
}
