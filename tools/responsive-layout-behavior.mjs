import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const mainCss = await readFile(new URL('../wwwroot/css/site.css', import.meta.url), 'utf8');
const mainApp = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const builderCss = await readFile(new URL('../wwwroot/invoice-builder/css/app.css', import.meta.url), 'utf8');
const builderHtml = await readFile(new URL('../wwwroot/invoice-builder/index.html', import.meta.url), 'utf8');

function includes(source, needle, message) {
  assert.ok(source.includes(needle), message);
}

function matches(source, pattern, message) {
  assert.match(source, pattern, message);
}

includes(mainCss, '@media (max-width: 1120px)', 'Main shell should have a tablet breakpoint.');
includes(mainCss, '@media (max-width: 760px)', 'Main shell should have a mobile breakpoint.');
includes(builderCss, '@media (max-width: 1500px)', 'Invoice builder should collapse the builder/preview grid on laptop widths.');
includes(builderCss, '@media (max-width: 820px)', 'Invoice builder should have a tablet breakpoint.');
includes(builderCss, '@media (max-width: 520px)', 'Invoice builder should have a narrow mobile breakpoint.');

matches(
  mainCss,
  /\.kpi-grid,\s*\.grid\.four,\s*\.quick-grid,\s*\.insight-grid,\s*\.entity-strip,\s*\.chart-grid\s*\{\s*grid-template-columns:\s*repeat\(2,\s*minmax\(0,\s*1fr\)\);/s,
  'Dashboard and common grid cards should reduce to two columns at tablet width.',
);
matches(
  mainCss,
  /\.form-grid,[\s\S]*?\.marketplace-review-form\s*\{\s*grid-template-columns:\s*1fr;\s*\}/,
  'Main shell form/dashboard/AI/tax grids should collapse to one column on mobile.',
);
matches(
  mainCss,
  /\.content\s*\{\s*margin-left:\s*0;\s*padding:\s*\.75rem;\s*\}/,
  'Mobile content should not retain a sidebar offset.',
);
matches(
  mainCss,
  /\.page-head,\s*\.table-tools\s*\{\s*flex-direction:\s*column;\s*align-items:\s*stretch;\s*\}/,
  'Page heads and table toolbars should stack on mobile instead of clipping actions.',
);
matches(
  mainCss,
  /\.header-actions\s*\{[\s\S]*?width:\s*100%;[\s\S]*?\}/,
  'Header actions should get a full mobile row.',
);
matches(
  mainCss,
  /\.header-actions\s*>\s*\*\s*\{\s*flex:\s*1 1 auto;\s*\}/,
  'Header action buttons should flex instead of clipping at mobile widths.',
);
matches(
  mainCss,
  /\.ai-source-actions\s+\.primary-button\s*\{\s*width:\s*100%;\s*margin-left:\s*0;\s*\}/,
  'Mobile AI action buttons should use full-width controls.',
);

matches(
  mainCss,
  /\.table-wrap\s*\{[\s\S]*?overflow:\s*auto;[\s\S]*?\}/,
  'Main shell table wrapper should intentionally scroll when narrow.',
);
matches(
  mainCss,
  /\.table-wrap td\s*\{[\s\S]*?overflow-wrap:\s*anywhere;[\s\S]*?word-break:\s*break-word;[\s\S]*?\}/,
  'Main shell table cells should wrap long text instead of overflowing.',
);
matches(
  mainCss,
  /\.modal\s*\{[\s\S]*?width:\s*min\(980px,\s*calc\(100vw - 2rem\)\);[\s\S]*?max-height:\s*calc\(100vh - 2rem\);[\s\S]*?overflow:\s*auto;[\s\S]*?\}/,
  'Main shell modals should stay inside the viewport with internal scrolling.',
);

matches(
  mainCss,
  /\.printer-board\s*\{[\s\S]*?grid-template-columns:\s*repeat\(5,\s*minmax\(245px,\s*1fr\)\);[\s\S]*?overflow-x:\s*auto;[\s\S]*?\}/,
  'Printer board should preserve five lanes with intentional horizontal scroll when needed.',
);
matches(
  mainCss,
  /\.printer-board-column\s*\{[\s\S]*?min-width:\s*245px;[\s\S]*?\}/,
  'Printer board columns should keep usable lane widths with many cards.',
);
matches(
  mainCss,
  /\.printer-queue-card h4\s*\{[\s\S]*?overflow-wrap:\s*anywhere;[\s\S]*?\}/,
  'Printer cards should wrap long job names.',
);
matches(
  mainCss,
  /\.printer-queue-card p,\s*\.printer-queue-card small\s*\{[\s\S]*?overflow-wrap:\s*anywhere;[\s\S]*?\}/,
  'Printer card metadata should wrap long customers, printers, material names, and notes.',
);
includes(mainApp, "['Completed', rows.filter(row => row.status === 'Completed').slice(0, 12)]", 'Printer board should cap the completed lane so many old cards do not dominate the board.');
matches(
  mainApp,
  /items\.length \? items\.map\(renderPrinterQueueCard\)\.join\(''\) : `<div class="empty-state compact">/,
  'Printer board should render any number of active cards while preserving explicit empty lanes.',
);

matches(
  builderCss,
  /\.table-wrap\s*\{[\s\S]*?overflow-x:\s*auto;[\s\S]*?\}/,
  'Standalone invoice records table should intentionally scroll on narrow screens.',
);
matches(
  builderCss,
  /\.lineitems-wrap\s*\{[\s\S]*?overflow-x:\s*auto;[\s\S]*?overflow-y:\s*hidden;[\s\S]*?\}/,
  'Invoice line item table should use horizontal scroll instead of squeezing inputs.',
);
matches(
  builderCss,
  /\.data-table td\s*\{[\s\S]*?max-width:\s*320px;[\s\S]*?overflow-wrap:\s*anywhere;[\s\S]*?word-break:\s*break-word;[\s\S]*?\}/,
  'Standalone records table cells should wrap long values.',
);
matches(
  builderCss,
  /\.builder-grid\s*\{[\s\S]*?grid-template-columns:\s*1fr;[\s\S]*?\}/,
  'Standalone builder should stack editor and preview on narrower screens.',
);
matches(
  builderCss,
  /\.view-actions\s*\{[\s\S]*?width:\s*100%;[\s\S]*?overflow-x:\s*auto;[\s\S]*?\}/,
  'Standalone builder action bars should scroll instead of clipping buttons.',
);
matches(
  builderCss,
  /\.invoice-preview-stage\s*\{[\s\S]*?max-width:\s*100%;[\s\S]*?\}/,
  'Standalone preview stage should stay within mobile viewport width.',
);
includes(builderCss, '.invoice-preview-frame { min-width: 640px; }', 'Tablet invoice preview should keep a usable minimum width inside a scroll container.');
includes(builderCss, '.invoice-preview-frame { min-width: 560px; }', 'Narrow mobile invoice preview should still render at a stable scrollable width.');
includes(builderCss, 'table.li-table { min-width: 720px; }', 'Invoice line item table should keep stable editable columns on tablet/mobile.');
includes(builderHtml, '<iframe id="invoicePreviewFrame" class="invoice-preview-frame" title="Invoice PDF preview"></iframe>', 'Invoice preview iframe should be present with an accessible title.');

console.log(JSON.stringify({
  ResponsiveLayoutBehavior: 'pass',
  ResponsiveLayoutRound108: 'pass',
  NoClipSourceRound108: 'pass',
  DashboardResizeRound108: 'pass',
  PrinterBoardManyCardsRound108: 'pass',
  MainBreakpoints: [1120, 760],
  InvoiceBuilderBreakpoints: [1500, 820, 520],
}));
