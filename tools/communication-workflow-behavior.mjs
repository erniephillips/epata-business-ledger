import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const sandbox = {};
sandbox.globalThis = sandbox;

const helperSource = await readFile(new URL('../wwwroot/js/communication-state.js', import.meta.url), 'utf8');
vm.runInNewContext(helperSource, sandbox, { filename: 'communication-state.js' });

const { communicationCardPlan } = sandbox.EpataCommunicationState;
const plain = (value) => JSON.parse(JSON.stringify(value));
const now = new Date('2026-06-19T12:00:00');

const incoming = plain(communicationCardPlan({
  direction: 'Incoming',
  channel: 'Email',
  customerName: 'Round 54 Customer',
  subject: 'Incoming approval',
  summary: 'Customer approved the proof.',
  followUpStatus: 'Open',
  followUpDate: '2026-06-18',
  relatedJobNumber: 'JOB-R54',
  relatedOrderNumber: 'ORDER-R54',
  relatedInvoiceNumber: 'INV-R54'
}, now));

assert.equal(incoming.direction, 'Incoming');
assert.equal(incoming.followUpOpen, true);
assert.equal(incoming.overdue, true);
assert.equal(incoming.followUpBadge, 'Follow-up overdue');
assert.equal(incoming.reference, 'JOB-R54 · ORDER-R54 · INV-R54');

const outgoing = plain(communicationCardPlan({
  direction: 'Outgoing',
  channel: 'Text Message',
  customerName: 'Round 54 Customer',
  summary: 'Sent a shipping update.',
  followUpStatus: 'Done',
  followUpDate: '2026-06-20'
}, now));

assert.equal(outgoing.direction, 'Outgoing');
assert.equal(outgoing.subject, 'Text Message with Round 54 Customer');
assert.equal(outgoing.followUpOpen, false);
assert.equal(outgoing.overdue, false);

const internal = plain(communicationCardPlan({
  direction: 'Internal',
  customerName: 'Round 54 Customer',
  summary: 'Private note for next production pass.',
  followUpStatus: 'None'
}, now));

assert.equal(internal.direction, 'Internal');
assert.equal(internal.subject, 'Communication with Round 54 Customer');

const longWord = 'x'.repeat(120);
const longSummary = plain(communicationCardPlan({
  direction: 'Incoming',
  customerName: 'Round 54 Long Summary',
  summary: `Customer pasted slicer settings ${longWord} with multiple requirements.`,
  followUpStatus: 'Open'
}, now));

assert.equal(longSummary.hasLongSummary, true);
assert.ok(longSummary.summary.includes(longWord), 'Long communication summaries should be preserved for wrapping.');
assert.equal(longSummary.overdue, false, 'Open follow-up without a date is not visually overdue in the card.');

const indexSource = await readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8');
const appSource = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const cssSource = await readFile(new URL('../wwwroot/css/site.css', import.meta.url), 'utf8');

assert.ok(indexSource.includes('/js/communication-state.js?v=1'), 'Main shell should load the Communication state helper before app.js.');
assert.ok(appSource.includes('EpataCommunicationState?.communicationCardPlan'), 'Communication cards should use shared card planning.');
assert.ok(appSource.includes('communication-summary ${plan.hasLongSummary ?'), 'Long summaries should receive the long-summary class.');
assert.ok(cssSource.includes('.communication-summary') && cssSource.includes('overflow-wrap: anywhere'), 'Communication summaries should wrap long pasted text.');
assert.ok(cssSource.includes('.communication-reference') && cssSource.includes('word-break: break-word'), 'Communication references should wrap long linked record text.');

console.log(JSON.stringify({
  CommunicationWorkflowBehavior: 'pass',
  CommunicationDisplayRound54: 'pass',
  CommunicationFollowUpRound54: 'pass',
  CommunicationLongSummaryRound54: 'pass'
}));
