import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const sandbox = {};
sandbox.globalThis = sandbox;

const helperSource = await readFile(new URL('../wwwroot/js/job-workflow-state.js', import.meta.url), 'utf8');
vm.runInNewContext(helperSource, sandbox, { filename: 'job-workflow-state.js' });

const { printerQueuePrefillFromJob, timelineEventTarget } = sandbox.EpataJobWorkflowState;
const plain = (value) => JSON.parse(JSON.stringify(value));

assert.deepEqual(
  plain(timelineEventTarget({ routePage: 'customerJobs', recordId: 42 })),
  { kind: 'modal', page: 'customerJobs', configKey: 'customerJobs', rowId: 42 },
  'Customer Job timeline events should open the exact Job modal.',
);
assert.deepEqual(
  plain(timelineEventTarget({ routePage: 'printerQueue', recordId: 43 })),
  { kind: 'modal', page: 'printerQueue', configKey: 'printerQueue', rowId: 43 },
  'Printer Queue timeline events should open the exact Queue modal.',
);
assert.deepEqual(
  plain(timelineEventTarget({ routePage: 'communications', recordId: 44 })),
  { kind: 'modal', page: 'communications', configKey: 'communications', rowId: 44 },
);
assert.deepEqual(
  plain(timelineEventTarget({ routePage: 'invoiceRecords', recordId: 45 })),
  { kind: 'page', page: 'invoiceRecords', configKey: '', rowId: 0 },
  'Invoice Records timeline events should fall back to page navigation.',
);
assert.deepEqual(plain(timelineEventTarget({})), { kind: 'none', page: '', configKey: '', rowId: 0 });

const job = {
  id: 99,
  customerName: 'Round 53 Customer',
  jobName: 'Round 53 Console Bracket',
  relatedOrderNumber: 'ORDER-R53',
  relatedInvoiceNumber: 'INV-R53',
  productName: 'Console Bracket',
  material: 'PETG',
  color: 'Black',
  description: 'Use 4 walls and 40% gyroid.',
  status: 'Open',
  invoiceAmount: 120,
};

const prefill = plain(printerQueuePrefillFromJob(job));
assert.deepEqual(prefill, {
  customerJobId: 99,
  customerName: 'Round 53 Customer',
  jobName: 'Round 53 Console Bracket',
  relatedOrderNumber: 'ORDER-R53',
  relatedInvoiceNumber: 'INV-R53',
  productName: 'Console Bracket',
  material: 'PETG',
  color: 'Black',
  notes: 'Use 4 walls and 40% gyroid.',
});
assert.equal(job.status, 'Open', 'Queue prefill should not mutate the source Customer Job.');

const appSource = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const indexSource = await readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8');
const relationshipSource = await readFile(new URL('../wwwroot/js/relationship-directory.js', import.meta.url), 'utf8');

assert.ok(indexSource.includes('/js/job-workflow-state.js?v=1'), 'Main shell should load the Job Workflow helper before app.js.');
assert.ok(appSource.includes('EpataJobWorkflowState?.timelineEventTarget'), 'Timeline events should use shared open-target planning.');
assert.ok(appSource.includes('data-timeline-route="${escapeAttr(event.routePage || \'\')}"'), 'Timeline buttons should carry route metadata.');
assert.ok(appSource.includes('openTimelineEvent'), 'Timeline event clicks should call the exact-row opener.');
assert.ok(appSource.includes('EpataJobWorkflowState?.printerQueuePrefillFromJob(job)'), 'Queue selected job should use shared queue prefill planning.');
assert.ok(appSource.includes("quickOpen('printerQueue', 'queue', prefill)"), 'Queue selected job should open an unsaved Queue modal.');
assert.ok(appSource.includes("api('/api/printer-queue-items?includeArchived=true')"), 'Printer Queue should count archived rows and reveal them only when requested.');
assert.ok(appSource.includes("restoreLedgerEntityRecord('printerQueue'"), 'Archived queue items should expose Restore.');
assert.ok(appSource.includes("archiveLedgerEntityRecord('printerQueue'"), 'Active queue items should expose Archive.');
assert.ok(appSource.includes("row.status === 'Needs Attention' && !row.isArchived"), 'Archived queue items should not retain active attention styling.');
assert.ok(relationshipSource.includes("relationshipLinkedRowTarget(configKey, row = {})"), 'Customer detail linked rows should use relationship row targets.');

console.log(JSON.stringify({
  JobWorkflowBehavior: 'pass',
  JobTimelineOpenRound53: 'pass',
  JobQueuePrefillRound53: 'pass',
}));
