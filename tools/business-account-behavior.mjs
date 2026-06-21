import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const appSource = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');

assert.ok(
  appSource.includes("columns: ['name','accountType','institution','last4','activeStatus','openingBalance','currentBalance']"),
  'Business Accounts table should expose a clear active/inactive status column.',
);
assert.ok(
  appSource.includes("if (column === 'activeStatus') return row.isActive === false ? 1 : 0;"),
  'Business Accounts active status should sort predictably.',
);
assert.ok(
  appSource.includes("if (key === 'activeStatus') return badgeFor(row.isActive === false ? 'Inactive' : 'Active');"),
  'Business Accounts active status should render as Active/Inactive.',
);
assert.ok(
  appSource.includes("f('isActive','Active','checkbox','Turn off for closed accounts.')"),
  'Business Accounts modal should still edit the stored isActive flag.',
);

console.log(JSON.stringify({
  BusinessAccountBehavior: 'pass',
  BusinessAccountStatusRound52: 'pass',
}));
