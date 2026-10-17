import test from 'node:test';
import assert from 'node:assert/strict';
import { argumentsFor, cleanEnvironment, parseAnswer, summarizeCliFailure } from './bridge.mjs';
test('all providers use noninteractive modes and reject unknown providers', () => {
  assert.ok(argumentsFor('gemini').includes('stream-json'));
  assert.ok(argumentsFor('gemini').includes('plan'));
  assert.ok(argumentsFor('gemini').includes('--sandbox'));
  assert.ok(argumentsFor('claude').includes('--print'));
  assert.ok(argumentsFor('codex').includes('read-only'));
  assert.throws(() => argumentsFor('invalid'));
});
test('API credentials removed without changing parent environment', () => {
  const source = { PATH: 'keep', OPENAI_API_KEY: 'secret', OPENAI_BASE_URL: 'https://example.invalid', ANTHROPIC_API_KEY: 'secret', ANTHROPIC_BASE_URL: 'https://example.invalid', GEMINI_API_KEY: 'secret', GOOGLE_CLOUD_PROJECT_ID: 'billable-project', GOOGLE_GENAI_USE_VERTEXAI: 'true' };
  assert.deepEqual(cleanEnvironment(source), { PATH: 'keep' });
  assert.equal(source.OPENAI_API_KEY, 'secret');
});
test('extract provider answers and ignore codex tool events', () => {
  const antigravity = [
    {event:'init',conversation_id:'test'},
    {event:'step_update',step_update:{text_delta:'Grüße 🌍'}},
    {event:'result',result:{status:'SUCCESS',response:'Grüße 🌍'}}
  ];
  assert.equal(parseAnswer('gemini', antigravity.map(e => JSON.stringify(e)).join('\n')), 'Grüße 🌍');
  assert.equal(parseAnswer('claude', '{"result":"Grüße 🌍","is_error":false}'), 'Grüße 🌍');
  const events = [{type:'thread.started'}, {type:'item.completed',item:{type:'command_execution',text:'ignore'}}, {type:'item.completed',item:{type:'agent_message',text:'Hallo'}}];
  assert.equal(parseAnswer('codex', events.map(e => JSON.stringify(e)).join('\n')), 'Hallo');
});
test('provider errors do not become successful answers', () => {
  assert.throws(() => parseAnswer('gemini', '{"event":"result","result":{"status":"ERROR","error":"Login fehlt"}}'), /Login fehlt/);
  assert.throws(() => parseAnswer('claude', '{"is_error":true,"result":"Login fehlt"}'), /Login fehlt/);
  assert.throws(() => parseAnswer('codex', '{"type":"turn.failed","error":{"message":"Limit"}}'), /Limit/);
  const geminiError = "Warning: TERM=dumb\nreasonMessage: 'Dieses Konto ist nicht berechtigt'\n    at internal";
  assert.equal(summarizeCliFailure(geminiError, '', 1), 'Dieses Konto ist nicht berechtigt');
});
