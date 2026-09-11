import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';

const readProjectFile = path => readFile(new URL(`../${path}`, import.meta.url), 'utf8');

async function readTree(dir, predicate) {
  const root = new URL(`../${dir}/`, import.meta.url);
  const results = [];
  async function walk(relative) {
    const entries = await readdir(new URL(relative, root), { withFileTypes: true });
    for (const entry of entries) {
      const child = `${relative}${entry.name}`;
      if (entry.isDirectory()) {
        await walk(`${child}/`);
      } else if (predicate(child)) {
        results.push([`${dir}/${child}`, await readFile(new URL(child, root), 'utf8')]);
      }
    }
  }
  await walk('');
  return results;
}

function mustInclude(source, text, label) {
  assert.ok(source.includes(text), label);
}

function mustNotMatch(source, pattern, label) {
  assert.doesNotMatch(source, pattern, label);
}

function mustMatch(source, pattern, label) {
  assert.match(source, pattern, label);
}

const [
  program,
  localAiService,
  aiEstimateService,
  aiOperationsService,
  invoiceImportService,
  runPs1,
  runBat,
  localSmoke,
  fullAcceptance,
  browserTestServer,
  playwrightServer,
  launchRehearsal,
  finalReleaseRehearsal,
  largeDataStress,
  sqliteLockSmoke,
  uploadPerformance,
  repeatedStartStop,
  slowMachineSimulation,
] = await Promise.all([
  readProjectFile('Program.cs'),
  readProjectFile('Services/LocalAiService.cs'),
  readProjectFile('Services/AiEstimateService.cs'),
  readProjectFile('Services/AiOperationsService.cs'),
  readProjectFile('Services/InvoiceAppImportService.cs'),
  readProjectFile('launch-epata.ps1'),
  readProjectFile('run.bat'),
  readProjectFile('tools/local-smoke.ps1'),
  readProjectFile('tools/full-acceptance.ps1'),
  readProjectFile('tools/browser-test-server.ps1'),
  readProjectFile('tools/playwright-server.ps1'),
  readProjectFile('tools/launch-rehearsal.ps1'),
  readProjectFile('tools/final-release-rehearsal.ps1'),
  readProjectFile('tools/large-data-stress.ps1'),
  readProjectFile('tools/sqlite-lock-smoke.ps1'),
  readProjectFile('tools/upload-performance.ps1'),
  readProjectFile('tools/repeated-start-stop.ps1'),
  readProjectFile('tools/slow-machine-simulation.ps1'),
]);

const browserSources = [
  ...await readTree('wwwroot/js', file => file.endsWith('.js')),
  ...await readTree('wwwroot/invoice-builder/js', file => file.endsWith('.js')),
  ...await readTree('wwwroot', file => file.endsWith('.html')),
];

for (const [path, source] of browserSources) {
  mustNotMatch(source, /fetch\(\s*['"]https?:\/\//i, `${path} should not hardcode remote fetch dependencies.`);
  mustNotMatch(source, /navigator\.sendBeacon\(\s*['"]https?:\/\//i, `${path} should not beacon to a remote dependency.`);
  mustNotMatch(source, /new\s+WebSocket\(\s*['"]wss?:\/\//i, `${path} should not open a remote websocket dependency.`);
  mustNotMatch(source, /window\.open\(\s*['"]https?:\/\//i, `${path} should not auto-open a remote URL.`);
}

for (const [path, source] of browserSources.filter(([path]) => path.endsWith('.html'))) {
  mustNotMatch(source, /<(script|link|img|iframe|source)\b[^>]+(?:src|href)=["']https?:\/\//i, `${path} should not hotlink required static assets.`);
}

mustInclude(program, 'var appUrl = builder.Configuration["App:Url"] ?? "http://127.0.0.1:5062";', 'Default app URL should be loopback.');
mustInclude(program, 'var openBrowser = bool.TryParse(app.Configuration["App:OpenBrowserOnStart"], out var shouldOpen) && shouldOpen;', 'Browser launch should be configuration-gated.');
mustInclude(program, 'Process.Start(new ProcessStartInfo(appUrl) { UseShellExecute = true });', 'Only the configured app URL should be opened when auto-open is enabled.');
mustInclude(program, 'Directory.CreateDirectory(GetSqliteDataDirectory(app.Configuration, app.Environment.ContentRootPath));', 'SQLite data directory should be resolved under local configuration.');
mustInclude(program, 'Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "Backups"));', 'Backups should be created under the app content root.');
mustInclude(program, 'Path.Combine(env.ContentRootPath, "UploadedDocs")', 'Uploaded proof files should be rooted under UploadedDocs.');
mustInclude(program, 'Path.Combine(env.ContentRootPath, "Backups")', 'Database backups should be rooted under Backups.');
mustNotMatch(program, /(?:@"[A-Za-z]:\\|"[A-Za-z]:\\\\|'[A-Za-z]:\\\\)/, 'Program.cs should not hardcode quoted absolute Windows write paths.');
mustNotMatch(program, /Environment\.GetFolderPath/, 'Program.cs should not write ledger data to profile folders.');

mustInclude(localAiService, 'Local AI server URL must be a loopback HTTP origin', 'Local AI settings should reject non-loopback origins.');
mustInclude(localAiService, 'uri.Host.Equals("localhost"', 'Local AI loopback validation should allow localhost only by rule.');
mustInclude(invoiceImportService, 'http://localhost:5057/', 'Legacy invoice import should target only the old local app by default.');
mustInclude(aiEstimateService, 'return addresses.Length > 0 && addresses.All(IsPublicAddress);', 'AI Estimate URL intake should block local/private hosts.');
mustInclude(aiOperationsService, 'Private or local-network URLs are blocked.', 'AI Operations URL intake should block local/private hosts.');
mustInclude(program, 'ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });', 'External AI URL fetchers should not silently follow redirects before validation.');

mustInclude(runPs1, '[object]$OpenBrowserOnStart = $true', 'The canonical launcher should keep browser auto-open explicit.');
mustInclude(runPs1, "@('Data', 'Models', 'Services', 'Properties')", 'Launcher freshness checks should include all backend source directories.');
mustInclude(runPs1, 'Get-ChildItem -LiteralPath $webRoot -Recurse -File', 'Launcher freshness checks should include every packaged web asset.');
mustInclude(runPs1, '$env:App__OpenBrowserOnStart = "false"', 'The launcher should prevent the child process from racing its own browser open.');
mustInclude(runPs1, 'Start-Process -FilePath $effectiveUrl', 'The launcher should open the browser only after the health check succeeds.');
mustInclude(runBat, 'powershell -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*', 'run.bat should forward all launcher arguments to run.ps1.');

for (const [name, source] of [
  ['local-smoke.ps1', localSmoke],
  ['full-acceptance.ps1', fullAcceptance],
  ['browser-test-server.ps1', browserTestServer],
  ['playwright-server.ps1', playwrightServer],
  ['launch-rehearsal.ps1', launchRehearsal],
  ['final-release-rehearsal.ps1', finalReleaseRehearsal],
  ['large-data-stress.ps1', largeDataStress],
  ['sqlite-lock-smoke.ps1', sqliteLockSmoke],
  ['upload-performance.ps1', uploadPerformance],
  ['repeated-start-stop.ps1', repeatedStartStop],
  ['slow-machine-simulation.ps1', slowMachineSimulation],
]) {
  mustInclude(source, 'App:OpenBrowserOnStart=false', `${name} should disable browser auto-open.`);
}

for (const [name, source] of [
  ['local-smoke.ps1', localSmoke],
  ['full-acceptance.ps1', fullAcceptance],
  ['playwright-server.ps1', playwrightServer],
  ['large-data-stress.ps1', largeDataStress],
  ['sqlite-lock-smoke.ps1', sqliteLockSmoke],
  ['upload-performance.ps1', uploadPerformance],
  ['repeated-start-stop.ps1', repeatedStartStop],
  ['slow-machine-simulation.ps1', slowMachineSimulation],
]) {
  mustMatch(source, /Data\\|Data\//, `${name} should use an explicit local Data database or probe path.`);
}

console.log(JSON.stringify({
  OfflineLocalBoundaryBehavior: 'pass',
  OfflineLocalBoundaryRound104: 'pass',
  CheckedAreas: [
    'browser-remote-fetches',
    'static-asset-hotlinks',
    'browser-auto-open-gates',
    'local-write-roots',
    'loopback-local-ai',
    'public-url-ai-intake-guards',
    'disposable-test-runners'
  ]
}));
