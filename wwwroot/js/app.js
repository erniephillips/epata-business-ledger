const appState = {
  currentPage: 'dashboard',
  modal: { config: null, row: null, mode: 'create' },
  globalSearchTimer: null,
  invoicePdfModule: null,
  invoiceToolSnapshot: null
};

const moneyFields = new Set(['itemSales','shippingCharged','salesTaxCollected','customerPaid','platformFees','shippingLabelCost','refunds','estimatedCogs','subtotal','discount','rushFee','salesTax','invoiceTotal','amountPaid','balanceDue','amount','total','openingBalance','currentBalance','cost','giftCardAmount','quoteAmount','invoiceAmount','targetPrice','grams','materialCostPerGram','printHours','machineRatePerHour','packagingCost','designMinutes','rewardValue','pointsValue','notYetExpensed','netBeforeCogs','estNetAfterCogs']);
const dateFields = new Set(['saleDate','shipByDate','invoiceDate','dueDate','billDate','paymentDate','expenseDate','purchaseDate','warrantyEndDate','rewardDate','documentDate','jobDate']);

const commonOptions = {
  platform: ['Direct','Etsy','MakerWorld','Local','Other'],
  yesNo: ['Yes','No'],
  partyType: ['Customer','Vendor','Both'],
  saleStatus: ['Draft','Paid','Fulfilled','Refunded','Needs Review'],
  invoiceStatus: ['Draft','Sent','Partial','Paid','Void','Overdue'],
  billStatus: ['Unpaid','Partial','Paid','Void'],
  jobStatus: ['Lead','Quoted','Open','In Progress','Invoiced','Completed','Paid','Cancelled'],
  priority: ['Low','Normal','High'],
  actionStatus: ['Open','Waiting','Done'],
  categories: ['Filament / Material','Shipping / Postage','Packaging','Marketplace Fees','Software','Tools','Equipment','Advertising','Office Supplies','General Business','Tax / Government','Other'],
  accountType: ['Cash','Checking','Credit Card','Etsy','Gift Card','Other'],
  documentType: ['Receipt','Invoice','Etsy Order','Tax','Bank','Photo Proof','Customer Message','Other']
};

const help = {
  includeInDashboard: 'Turn this on only when the money should count in your dashboard. Use No for unpaid quotes, drafts, and estimates.',
  itemSales: 'The product or service amount before shipping and sales tax. On Etsy, this is usually Item total.',
  shippingCharged: 'What the customer paid you for shipping. This is income, but it is different from what you paid USPS.',
  salesTaxCollected: 'Sales tax shown on the order or invoice. Etsy usually collects/remits marketplace tax; keep this as a memo amount.',
  customerPaid: 'The full amount paid by the customer, usually item + shipping + tax.',
  platformFees: 'Etsy/payment/marketplace fees. Pull this from Etsy payment statements, not the customer receipt.',
  shippingLabelCost: 'What you actually paid USPS or another carrier for the label.',
  estimatedCogs: 'Estimated cost of goods sold: filament, hardware, packaging, and other direct cost to make the item.',
  sourceProof: 'Filename, receipt, invoice PDF, Etsy order PDF, screenshot, or folder path proving this row.',
  needsReview: 'Use this when a row is incomplete or you still need to verify something.',
  bill: 'Use Bills for money you owe but have not paid yet. Once paid, keep it here or add a matching Expense for cash reporting.',
  expense: 'Use Expenses for purchases already paid: filament, shipping supplies, labels, ads, software, tools, etc.',
  receivable: 'Use Receivable Invoices for direct customer invoices and open money owed to you.',
  job: 'Use Customer Jobs to track the work/project itself, even before there is a sale or invoice.',
  audit: 'Use Audit Docs as your proof index. This is how you know which PDF, receipt, or screenshot backs up a transaction.',
  product: 'Use Products/Costing to estimate pricing and cost for repeat items or custom print types.',
  makerworld: 'Use MakerWorld for points, gift cards, redemptions, or reward credits so they do not disappear from your records.'
};

const proofFieldNames = new Set(['sourceProof', 'receiptProof', 'filePathOrUrl']);

const configs = {
  customerJobs: {
    route: 'customer-jobs',
    title: 'Customer Jobs',
    nav: 'Customer Jobs',
    purpose: 'Track the work itself: leads, Etsy orders, direct custom jobs, design work, prints in progress, and completed jobs. This is your customer/project history.',
    columns: ['jobDate','customerName','platform','jobName','status','relatedOrderNumber','relatedInvoiceNumber','invoiceAmount','amountPaid','needsReview'],
    fields: [
      f('jobDate','Job Date','date','Date the customer job started or order came in.'),
      f('customerName','Customer Name','text','Who the job is for.'),
      f('platform','Platform','select','Where the job came from.', commonOptions.platform),
      f('jobNumber','Job #','text','Optional internal job number.'),
      f('relatedOrderNumber','Related Order #','text','Etsy order number or marketplace order ID.'),
      f('relatedInvoiceNumber','Related Invoice #','text','Your direct invoice number if this job has one.'),
      f('jobName','Job Name','text','Plain-English name for the work.'),
      f('jobType','Job Type','select','Print, design, repair, or other.', ['Print','Print + Design','Design','Repair','Estimate','Other']),
      f('status','Status','select','Where the job stands right now.', commonOptions.jobStatus),
      f('productName','Product / Part','text','What you are making or selling.'),
      f('material','Material','text','PLA, PETG, ABS, ASA, TPU, etc.'),
      f('color','Color','text','Color requested or used.'),
      f('quoteAmount','Quote Amount','number','Amount quoted to the customer, if any.'),
      f('invoiceAmount','Invoice Amount','number','Amount billed or order total.'),
      f('amountPaid','Amount Paid','number','How much the customer has actually paid.'),
      f('dueDate','Due Date','date','Promise date or invoice due date.'),
      f('shipByDate','Ship By Date','date','Marketplace ship-by or planned shipping date.'),
      f('sourceProof','Source / Proof','text',help.sourceProof),
      f('needsReview','Needs Review','checkbox',help.needsReview),
      f('description','Description','textarea','Customer requirements, sizing, design assumptions, or build details.', null, 'full'),
      f('notes','Notes','textarea','Internal notes, reminders, and issues.', null, 'full')
    ]
  },
  sales: {
    route: 'sales',
    title: 'Sales / Income',
    nav: 'Sales',
    purpose: 'Use this for actual sales and paid customer orders. Etsy orders and paid direct invoices go here. Draft quotes do not belong here until paid.',
    columns: ['saleDate','platform','orderNumber','customerName','productName','customerPaid','platformFees','estimatedCogs','netBeforeCogs','estNetAfterCogs','status','needsReview'],
    fields: [
      f('saleDate','Sale Date','date','Order date or paid date.'),
      f('platform','Platform','select','Where the sale happened.', commonOptions.platform),
      f('orderNumber','Order #','text','Etsy order number or external order ID.'),
      f('invoiceNumber','Invoice #','text','Your direct invoice number if applicable.'),
      f('customerName','Customer Name','text','Customer who paid.'),
      f('productName','Product / Service','text','What was sold.'),
      f('sku','SKU','text','Your SKU or product code.'),
      f('variation','Variation','text','Set of 4, Set of 5, size, option, etc.'),
      f('color','Color','text','Item color.'),
      f('quantity','Qty','number','Number of units or sets.'),
      f('itemSales','Item Sales','number',help.itemSales),
      f('shippingCharged','Shipping Charged','number',help.shippingCharged),
      f('salesTaxCollected','Sales Tax Memo','number',help.salesTaxCollected),
      f('customerPaid','Customer Paid','number',help.customerPaid),
      f('platformFees','Platform Fees','number',help.platformFees),
      f('shippingLabelCost','Shipping Label Cost','number',help.shippingLabelCost),
      f('refunds','Refunds','number','Any refund, discount after sale, or adjustment.'),
      f('estimatedCogs','Est. COGS','number',help.estimatedCogs),
      f('status','Status','select','Current payment/order status.', commonOptions.saleStatus),
      f('trackingNumber','Tracking #','text','USPS/UPS/FedEx tracking number.'),
      f('shipByDate','Ship By','date','Ship-by date from Etsy or your promise date.'),
      f('sourceProof','Source / Proof','text',help.sourceProof),
      f('includeInDashboard','Include in Dashboard','checkbox',help.includeInDashboard),
      f('needsReview','Needs Review','checkbox',help.needsReview),
      f('notes','Notes','textarea','Internal notes for this sale.', null, 'full')
    ]
  },
  receivables: {
    route: 'receivable-invoices',
    title: 'Accounts Receivable / Invoice Register',
    nav: 'AR Invoices',
    purpose: 'Use this for direct invoices and money customers owe you. Paid invoices can also be mirrored into Sales once paid.',
    columns: ['invoiceNumber','invoiceDate','dueDate','customerName','projectName','status','invoiceTotal','amountPaid','balanceDue','needsReview'],
    fields: [
      f('invoiceNumber','Invoice #','text','Your invoice number. Keep this unique and sequential.'),
      f('originalInvoiceNumber','Original PDF Invoice #','text','Use this only if a PDF had the wrong/duplicate number.'),
      f('invoiceDate','Invoice Date','date','Date shown on invoice.'),
      f('dueDate','Due Date','date','Date payment is due.'),
      f('customerName','Customer Name','text','Customer being invoiced.'),
      f('projectName','Project Name','text','Job/project name on the invoice.'),
      f('status','Status','select','Invoice state.', commonOptions.invoiceStatus),
      f('subtotal','Subtotal','number','Invoice amount before tax/discount/rush fee.'),
      f('discount','Discount','number','Discount amount, if any.'),
      f('rushFee','Rush Fee','number','Rush/expedite charge, if any.'),
      f('taxRatePercent','Tax Rate %','number','Tax percent, for example 6.625.'),
      f('salesTax','Sales Tax','number','Tax dollars charged.'),
      f('invoiceTotal','Invoice Total','number','Final invoice total.'),
      f('amountPaid','Amount Paid','number','How much has been paid so far.'),
      f('sourceProof','Source / Proof','text',help.sourceProof),
      f('externalInvoiceAppUrl','Invoice Source Link','text','Optional reference to an outside invoice source. New estimates and invoices should be created from Estimates or Invoices.'),
      f('includeInCashReports','Include in Cash Reports','checkbox','Turn on once actually paid. Keep off for sent/unpaid invoices.'),
      f('needsReview','Needs Review','checkbox',help.needsReview),
      f('notes','Notes','textarea','Internal invoice notes.', null, 'full')
    ]
  },
  bills: {
    route: 'bills',
    title: 'Accounts Payable / Bills',
    nav: 'AP Bills',
    purpose: 'Use Bills for money the business owes but has not fully paid yet: vendor invoices, software renewals, shipping invoices, etc.',
    columns: ['dueDate','vendorName','category','description','status','total','amountPaid','balanceDue','needsReview'],
    fields: [
      f('vendorName','Vendor Name','text','Who you owe.'),
      f('billNumber','Bill #','text','Vendor invoice/bill number, if any.'),
      f('billDate','Bill Date','date','Date on the bill.'),
      f('dueDate','Due Date','date','When payment is due.'),
      f('category','Category','select','Expense category.', commonOptions.categories),
      f('description','Description','text','What the bill is for.'),
      f('amount','Amount Before Tax','number','Pre-tax amount.'),
      f('salesTax','Sales Tax','number','Tax paid to vendor.'),
      f('total','Total','number','Total bill amount.'),
      f('amountPaid','Amount Paid','number','Amount already paid.'),
      f('paymentDate','Payment Date','date','Date you paid, if paid.'),
      f('status','Status','select','Bill payment status.', commonOptions.billStatus),
      f('paymentAccount','Payment Account','text','Cash, bank, credit card, Etsy balance, etc.'),
      f('sourceProof','Source / Proof','text',help.sourceProof),
      f('taxDeductible','Tax Deductible','checkbox','Most business expenses are deductible, but keep it off for personal/non-business items.'),
      f('needsReview','Needs Review','checkbox',help.needsReview),
      f('notes','Notes','textarea','Internal AP notes.', null, 'full')
    ]
  },
  expenses: {
    route: 'expenses',
    title: 'Expenses / Paid Purchases',
    nav: 'Expenses',
    purpose: 'Use this for purchases already paid: filament, labels, packaging, tools, software, marketplace fees, ads, and office supplies.',
    columns: ['expenseDate','vendorName','category','description','total','taxBucket','deductibleStatus','needsReview'],
    fields: [
      f('expenseDate','Expense Date','date','Date you paid or receipt date.'),
      f('vendorName','Vendor Name','text','Store/vendor name.'),
      f('category','Category','select','Expense category.', commonOptions.categories),
      f('description','Description','text','What you bought.'),
      f('paymentAccount','Payment Account','text','Cash, card, Etsy balance, gift card, etc.'),
      f('amount','Amount Before Tax','number','Pre-tax amount.'),
      f('salesTax','Sales Tax','number','Tax paid.'),
      f('total','Total','number','Full paid amount.'),
      f('taxBucket','Tax Bucket','select','How this expense is classified for taxes.', ['Operating Expense','COGS/Materials','Asset','Memo Only','Review']),
      f('deductibleStatus','Deductible','select','Is this expense tax-deductible?', ['Yes','No','Review']),
      f('businessUsePercent','Business Use %','number','Percent used for business. 100 for fully business, lower if mixed personal/business.'),
      f('countedExpense','Count as Expense','checkbox','Turn off to exclude from totals (e.g. personal or duplicate).'),
      f('receiptProof','Receipt / Proof','text','Receipt file, screenshot, or order number.'),
      f('taxDeductible','Tax Deductible','checkbox','Turn off if not a business expense.'),
      f('needsReview','Needs Review','checkbox',help.needsReview),
      f('notes','Notes','textarea','Internal notes.', null, 'full')
    ]
  },
  parties: {
    route: 'parties',
    title: 'Customers & Vendors',
    nav: 'People / Vendors',
    purpose: 'Store customer and vendor details so you are not hunting through PDFs later.',
    columns: ['name','partyType','email','phone','city','state','etsyUsername','defaultPlatform'],
    fields: [
      f('name','Name','text','Customer, vendor, or company name.'),
      f('partyType','Type','select','Customer, vendor, or both.', commonOptions.partyType),
      f('email','Email','email','Email address.'),
      f('phone','Phone','text','Phone number.'),
      f('address1','Address 1','text','Street address.'),
      f('address2','Address 2','text','Apartment/suite/etc.'),
      f('city','City','text','City.'),
      f('state','State','text','State.'),
      f('postalCode','ZIP','text','Postal code.'),
      f('country','Country','text','Country.'),
      f('etsyUsername','Etsy Username','text','Buyer username if from Etsy.'),
      f('defaultPlatform','Default Platform','select','Where this customer/vendor usually appears.', ['Direct','Etsy','Vendor','MakerWorld','Other']),
      f('notes','Notes','textarea','Relationship notes, preferences, compatibility info, etc.', null, 'full')
    ]
  },
  products: {
    route: 'products',
    title: 'Products & Costing',
    nav: 'Products / Costing',
    purpose: 'Estimate what items cost to make. This helps you price repeat items without doing math every time.',
    columns: ['name','sku','material','grams','printHours','targetPrice','estimatedCost','needsReview'],
    fields: [
      f('name','Product Name','text','Product or common custom-job type.'),
      f('sku','SKU','text','Your product code.'),
      f('category','Category','text','Product category.'),
      f('material','Material','text','PLA, PETG, ABS, ASA, TPU, etc.'),
      f('color','Color','text','Default color.'),
      f('grams','Grams','number','Estimated filament used.'),
      f('materialCostPerGram','Cost / Gram','number','Filament cost per gram. Example: 0.05.'),
      f('printHours','Print Hours','number','Machine time estimate.'),
      f('machineRatePerHour','Machine Rate / Hr','number','Your chosen machine hourly rate.'),
      f('packagingCost','Packaging Cost','number','Box, bag, label, tape, inserts, etc.'),
      f('designMinutes','Design Minutes','number','Design/modeling time estimate.'),
      f('targetPrice','Target Price','number','What you expect to charge.'),
      f('needsReview','Needs Review','checkbox',help.needsReview),
      f('notes','Notes','textarea','Print settings, material risk, fitment notes, etc.', null, 'full')
    ]
  },
  assets: {
    route: 'assets',
    title: 'Assets / Equipment',
    nav: 'Assets',
    purpose: 'Track bigger business property: printers, AMS, tools, computers, high-value equipment, and warranty info.',
    columns: ['purchaseDate','name','vendorName','cost','taxTreatment','countedExpenseThisYear','notYetExpensed','needsReview'],
    fields: [
      f('name','Asset Name','text','Printer, tool, laptop, AMS, etc.'),
      f('purchaseDate','Purchase Date','date','Date purchased.'),
      f('inServiceDate','In-Service Date','date','Date the asset was placed in business service. Required for Section 179 and depreciation.'),
      f('vendorName','Vendor','text','Where you bought it.'),
      f('category','Category','text','Equipment, tool, computer, etc.'),
      f('cost','Cost','number','Purchase cost.'),
      f('serialNumber','Serial #','text','Serial number.'),
      f('warrantyEndDate','Warranty End','date','Warranty expiration date.'),
      f('businessUsePercent','Business Use %','number','Usually 100 for business-only equipment.'),
      f('taxTreatment','Tax Treatment','select','How this asset is handled on taxes.', ['Section 179','De Minimis Expense','Depreciation','Review','Not Deductible']),
      f('countedExpenseThisYear','Expensed This Year','checkbox','Check when this asset has been fully expensed this tax year (Section 179 or De Minimis).'),
      f('notYetExpensed','Not Yet Expensed','number','Remaining cost not yet deducted. Use for partial-year or multi-year depreciation tracking.'),
      f('sourceProof','Source / Proof','text',help.sourceProof),
      f('needsReview','Needs Review','checkbox',help.needsReview),
      f('notes','Notes','textarea','Warranty, repair, depreciation, or setup notes.', null, 'full')
    ]
  },
  makerworld: {
    route: 'makerworld-rewards',
    title: 'MakerWorld Rewards',
    nav: 'MakerWorld',
    purpose: help.makerworld,
    columns: ['rewardDate','rewardType','pointsChange','giftCardAmount','status','incomeStatus','needsReview'],
    fields: [
      f('rewardDate','Date','date','Date points/gift card/reward changed.'),
      f('rewardType','Reward Type','select','Kind of reward.', ['Points','Gift Card','Redemption','Other']),
      f('pointsChange','Points Change','number','Positive for earned points, negative for used points.'),
      f('giftCardAmount','Gift Card Amount','number','Dollar value if a gift card was issued or used.'),
      f('codeLast4','Code Last 4','text','Last four characters only. Do not store full gift card codes here.'),
      f('status','Status','select','Reward status.', ['Available','Redeemed','Expired','Pending']),
      f('incomeStatus','Income Status','select','Should this count as taxable income?', ['Yes - Count as income','No','Memo only','Review']),
      f('sourceProof','Source / Proof','text',help.sourceProof),
      f('needsReview','Needs Review','checkbox',help.needsReview),
      f('notes','Notes','textarea','What it was used for or where to find the proof.', null, 'full')
    ]
  },
  auditDocs: {
    route: 'audit-documents',
    title: 'Audit Docs / Proof Index',
    nav: 'Audit Docs',
    purpose: help.audit,
    columns: ['documentDate','documentType','relatedRecordType','relatedRecordNumber','fileName','needsReview'],
    fields: [
      f('documentDate','Document Date','date','Receipt/order/invoice date.'),
      f('documentType','Document Type','select','What kind of proof this is.', commonOptions.documentType),
      f('relatedRecordType','Related Type','text','Sale, Invoice, Bill, Expense, Asset, etc.'),
      f('relatedRecordNumber','Related #','text','Invoice #, order #, receipt #, etc.'),
      f('fileName','File Name','text','Exact saved file name.'),
      f('filePathOrUrl','File Path / URL','text','Optional folder path, OneDrive path, Etsy URL, etc.'),
      f('needsReview','Needs Review','checkbox',help.needsReview),
      f('notes','Notes','textarea','What this proves or where to file it.', null, 'full')
    ]
  },
  accounts: {
    route: 'business-accounts',
    title: 'Business Accounts',
    nav: 'Accounts',
    purpose: 'Track where business money moves: Etsy balance, cash, bank account, credit card, gift cards. This is not bank sync, just a clear register reference.',
    columns: ['name','accountType','institution','last4','openingBalance','currentBalance','isActive'],
    fields: [
      f('name','Account Name','text','Friendly name, like Etsy Payments or Business Checking.'),
      f('accountType','Account Type','select','Kind of account.', commonOptions.accountType),
      f('institution','Institution','text','Bank/vendor/platform name.'),
      f('last4','Last 4','text','Last four digits only, if useful.'),
      f('openingBalance','Opening Balance','number','Starting balance when you begin tracking.'),
      f('currentBalance','Current Balance','number','Current manual balance if you want to track it.'),
      f('isActive','Active','checkbox','Turn off for closed accounts.'),
      f('notes','Notes','textarea','Any account notes.', null, 'full')
    ]
  },
  actions: {
    route: 'action-items',
    title: 'Action Items',
    nav: 'Actions',
    purpose: 'Your to-do list for missing costs, open invoices, receipt cleanup, and bookkeeping fixes.',
    columns: ['priority','dueDate','area','title','status','relatedRecord'],
    fields: [
      f('title','Title','text','What needs to be done.'),
      f('area','Area','select','Which area this belongs to.', ['General','Sales','Invoice','AP','Expense','Product','Tax','Audit']),
      f('priority','Priority','select','How important this is.', commonOptions.priority),
      f('dueDate','Due Date','date','Optional due date.'),
      f('status','Status','select','Current status.', commonOptions.actionStatus),
      f('relatedRecord','Related Record','text','Order #, invoice #, customer name, etc.'),
      f('notes','Notes','textarea','Details and next step.', null, 'full')
    ]
  }
};

const navGroups = [
  {
    label: 'Command',
    items: [
      ['dashboard','Dashboard','◆'],
      ['quickAdd','Quick Add','＋'],
      ['estimates','Estimates','🧾'],
      ['invoices','Invoices','📄'],
      ['pricingCalculator','Calculator','🧮'],
      ['invoiceRecords','Invoice Records','🗂'],
      ['documentIntake','Document Intake','⇪']
    ]
  },
  {
    label: 'Books',
    items: [
      ['sales',configs.sales.nav,'$'],
      ['receivables',configs.receivables.nav,'AR'],
      ['bills',configs.bills.nav,'AP'],
      ['expenses',configs.expenses.nav,'−'],
      ['accounts',configs.accounts.nav,'▣']
    ]
  },
  {
    label: 'Operations',
    items: [
      ['customerJobs',configs.customerJobs.nav,'◱'],
      ['parties',configs.parties.nav,'◎'],
      ['products',configs.products.nav,'▦'],
      ['assets',configs.assets.nav,'▤'],
      ['makerworld',configs.makerworld.nav,'MW']
    ]
  },
  {
    label: 'Control',
    items: [
      ['auditDocs',configs.auditDocs.nav,'↗'],
      ['actions',configs.actions.nav,'!'],
      ['taxPrep','Tax Prep','%'],
      ['importExport','Import / Export','⇄'],
      ['admin','Admin / Data','⚙'],
      ['workflowGuide','Workflow','▶'],
      ['help','Help / Glossary','?']
    ]
  }
];

function f(name, label, type, tip, options = null, span = null) {
  return { name, label, type, tip, options, span };
}

function qs(sel) { return document.querySelector(sel); }
function qsa(sel) { return [...document.querySelectorAll(sel)]; }

async function api(path, options = {}) {
  const response = await fetch(path, {
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
    ...options
  });
  if (!response.ok) {
    const msg = await response.text();
    throw new Error(msg || `Request failed: ${response.status}`);
  }
  const contentType = response.headers.get('content-type') || '';
  if (contentType.includes('application/json')) return response.json();
  return response;
}

function formatMoney(value) {
  const n = Number(value || 0);
  return n.toLocaleString(undefined, { style: 'currency', currency: 'USD' });
}
function formatValue(value, key) {
  if (value === null || value === undefined || value === '') return '';
  if (key && moneyFields.has(key)) return formatMoney(value);
  if (key && dateFields.has(key)) return String(value).substring(0,10);
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  return value;
}
function badgeFor(value, needsReview = false) {
  const v = (value || '').toString().toLowerCase();
  let cls = '';
  if (needsReview || v.includes('review') || v.includes('unpaid') || v.includes('open') || v.includes('sent')) cls = 'warn';
  if (v.includes('paid') || v.includes('fulfilled') || v.includes('done') || v.includes('completed')) cls = 'good';
  if (v.includes('void') || v.includes('cancel') || v.includes('refunded') || v.includes('overdue')) cls = 'bad';
  return `<span class="badge ${cls}">${escapeHtml(value ?? '')}</span>`;
}
function escapeHtml(v) {
  return String(v ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
}
function toast(message) {
  const el = qs('#toast');
  el.textContent = message;
  el.classList.remove('hidden');
  setTimeout(() => el.classList.add('hidden'), 3200);
}

function bindTooltips() {
  const tooltip = qs('#tooltip');
  document.addEventListener('mouseover', e => {
    const tip = e.target.closest('.tip');
    if (!tip || !tip.dataset.tip) return;
    tooltip.textContent = tip.dataset.tip;
    tooltip.classList.remove('hidden');
    positionTooltip(tip, tooltip);
  });
  document.addEventListener('focusin', e => {
    const tip = e.target.closest('.tip');
    if (!tip || !tip.dataset.tip) return;
    tooltip.textContent = tip.dataset.tip;
    tooltip.classList.remove('hidden');
    positionTooltip(tip, tooltip);
  });
  document.addEventListener('mouseout', e => {
    if (e.target.closest('.tip')) tooltip.classList.add('hidden');
  });
  document.addEventListener('focusout', e => {
    if (e.target.closest('.tip')) tooltip.classList.add('hidden');
  });
  window.addEventListener('scroll', () => tooltip.classList.add('hidden'), true);
  window.addEventListener('resize', () => tooltip.classList.add('hidden'));
}

function positionTooltip(anchor, tooltip) {
  const rect = anchor.getBoundingClientRect();
  const gap = 10;
  const tipRect = tooltip.getBoundingClientRect();
  let left = rect.left + rect.width / 2 - tipRect.width / 2;
  let top = rect.bottom + gap;
  left = Math.max(12, Math.min(left, window.innerWidth - tipRect.width - 12));
  if (top + tipRect.height > window.innerHeight - 12) {
    top = rect.top - tipRect.height - gap;
  }
  tooltip.style.left = `${left}px`;
  tooltip.style.top = `${Math.max(12, top)}px`;
}

function renderNav() {
  const nav = qs('#nav');
  nav.innerHTML = navGroups.map(group => `
    <div class="nav-group">
      <div class="nav-label">${group.label}</div>
      ${group.items.map(([id,label,icon]) => `<button class="nav-button" data-page="${id}"><span class="nav-icon">${icon}</span><span>${label}</span></button>`).join('')}
    </div>`).join('');
  nav.addEventListener('click', e => {
    const btn = e.target.closest('button[data-page]');
    if (btn) showPage(btn.dataset.page);
  });
}

async function showPage(page) {
  const invoicePages = ['invoiceCenter','estimates','invoices','pricingCalculator','invoiceRecords'];
  const movingWithinInvoiceTool = invoicePages.includes(appState.currentPage) && invoicePages.includes(page);
  if (movingWithinInvoiceTool && window._invoiceToolSnapshot) {
    appState.invoiceToolSnapshot = window._invoiceToolSnapshot() || appState.invoiceToolSnapshot;
  } else if (!invoicePages.includes(page)) {
    appState.invoiceToolSnapshot = null;
  }

  appState.currentPage = page;
  toggleMergedInvoiceStyles(invoicePages.includes(page));
  if (page !== 'globalSearch') qs('#globalSearch').value = '';
  qsa('.nav-button').forEach(b => b.classList.toggle('active', b.dataset.page === page));
  const el = qs('#app');
  el.innerHTML = `<div class="card"><p>Loading...</p></div>`;
  try {
    if (page === 'dashboard') await renderDashboard(el);
    else if (page === 'globalSearch') await renderGlobalSearch(el);
    else if (page === 'quickAdd') renderQuickAdd(el);
    else if (page === 'invoiceCenter') await renderMergedInvoiceTool(el, 'dashboard', '', appState.invoiceToolSnapshot);
    else if (page === 'estimates') await renderMergedInvoiceTool(el, 'builder', 'ESTIMATE', appState.invoiceToolSnapshot);
    else if (page === 'invoices') await renderMergedInvoiceTool(el, 'builder', 'INVOICE', appState.invoiceToolSnapshot);
    else if (page === 'pricingCalculator') await renderMergedInvoiceTool(el, 'calculator', '', appState.invoiceToolSnapshot);
    else if (page === 'invoiceRecords') await renderMergedInvoiceTool(el, 'records', '', appState.invoiceToolSnapshot);
    else if (page === 'workflowGuide') renderWorkflowGuide(el);
    else if (page === 'documentIntake') renderDocumentIntake(el);
    else if (page === 'taxPrep') await renderTaxPrep(el);
    else if (page === 'importExport') renderImportExport(el);
    else if (page === 'admin') await renderAdmin(el);
    else if (page === 'help') renderHelp(el);
    else await renderEntity(el, configs[page]);
  } catch (err) {
    el.innerHTML = `<div class="card"><h2>Something broke</h2><p>${escapeHtml(err.message)}</p></div>`;
  }
}

function milestonePulse(net) {
  const milestones = [100, 250, 500, 1000, 2500, 5000];
  const n = Number(net || 0);
  const next = milestones.find(m => m > n) || milestones[milestones.length - 1];
  const prev = milestones[milestones.indexOf(next) - 1] || 0;
  const pct = Math.min(100, Math.max(0, ((n - prev) / (next - prev)) * 100)).toFixed(1);
  const label = n >= milestones[milestones.length - 1] ? `🎉 Past $${milestones[milestones.length-1].toLocaleString()}!` : `Next milestone: $${next.toLocaleString()} net`;
  return `<div class="milestone-pulse">
    <div class="milestone-label"><span>${label}</span><span>${formatMoney(n)} / $${next.toLocaleString()}</span></div>
    <div class="milestone-bar"><div class="milestone-fill" style="width:${pct}%"></div></div>
  </div>`;
}

async function renderDashboard(el) {
  const [data, appInfo] = await Promise.all([api('/api/dashboard'), api('/api/app-info').catch(() => null)]);
  const k = data.kpis;
  const monthlyMax = Math.max(...(data.monthly || []).map(x => Number(x.grossReceipts || 0)), 1);
  el.innerHTML = `
    <section class="workspace-hero">
      <div>
        <span class="eyebrow">EPATA Ledger Command</span>
        <h2>Business control, without spreadsheet fog.</h2>
        <p>Track cash, open invoices, vendor obligations, costs, proof files, and the cleanup queue from one local workspace.</p>
      </div>
      <div class="hero-actions">
        <button class="primary-button" onclick="showPage('quickAdd')">Add Transaction</button>
        <button class="ghost-button dark" onclick="showPage('documentIntake')">Upload Proof</button>
      </div>
    </section>
    <div class="page-head">
      <div>
        <h2>Dashboard</h2>
        <p>Your live money view: revenue, estimated net, open AR/AP, tax memo, proof discipline, and cleanup pressure.</p>
      </div>
      <div class="actions">
        <button class="primary-button" onclick="showPage('quickAdd')">Quick Add</button>
        <button class="ghost-button" onclick="showPage('actions')">Review Actions</button>
      </div>
    </div>
    <section class="kpi-grid">
      ${kpi('Gross Receipts', k.grossReceipts, 'Item sales + shipping charged, before platform fees and COGS.', 'good')}
      ${kpi('Estimated Net', k.estimatedNet, 'Gross receipts minus entered fees, label costs, COGS, and expenses.', k.estimatedNet >= 0 ? 'good' : 'bad')}
      ${kpi('Open AR', k.openReceivables, 'Customer invoice balances still owed to you.', k.openReceivables > 0 ? 'warn' : 'good')}
      ${kpi('Open AP', k.openPayables, 'Bills you still owe vendors.', k.openPayables > 0 ? 'warn' : 'good')}
      ${kpi('Customer Paid', k.customerPaid, 'Total customer-paid amounts including tax memo.', '')}
      ${kpi('Sales Tax Memo', k.salesTaxMemo, 'Tax shown on orders/invoices. Etsy usually handles marketplace tax, but keep the memo.', 'warn')}
      ${kpi('Known Costs', k.sellingCosts + k.directExpenses, 'Platform fees, shipping labels, COGS, and expenses you entered.', '')}
      ${kpi('Needs Review', k.needsReviewCount, 'Rows missing costs, proof, or confirmation.', k.needsReviewCount > 0 ? 'warn' : 'good', false)}
    </section>
    <section class="insight-grid">
      ${insight('Cash Focus', k.openReceivables > 0 ? `${formatMoney(k.openReceivables)} still needs collected from customers.` : 'No open customer invoice balance in the current ledger.', 'Open AR is the fastest place to improve cash flow.')}
      ${insight('Cleanup Queue', k.needsReviewCount > 0 ? `${k.needsReviewCount} rows are marked for review.` : 'Nothing is currently marked for review.', 'Use this as your bookkeeping punch list before tax time.')}
      ${insight('Proof Discipline', 'Upload receipts, PDFs, screenshots, and order files into Document Intake.', 'Each upload creates an Audit Doc so records point back to proof.')}
    </section>
    ${milestonePulse(k.estimatedNet)}
    <section class="chart-grid">
      <div class="chart-card wide">
        <div class="card-header-lite">
          <h3>Revenue vs Net Trend</h3>
          <span class="badge">Monthly</span>
        </div>
        ${lineChart(data.monthly || [], 'month', [
          { key: 'grossReceipts', label: 'Gross', color: '#60a5fa' },
          { key: 'estimatedNet', label: 'Net', color: '#34d399' }
        ])}
      </div>
      <div class="chart-card">
        <div class="card-header-lite">
          <h3>Money Mix</h3>
          <span class="badge">Current</span>
        </div>
        ${donutChart([
          { label: 'Net', value: Math.max(0, k.estimatedNet), color: '#059669' },
          { label: 'Known Costs', value: Math.max(0, k.sellingCosts + k.directExpenses), color: '#d97706' },
          { label: 'Tax Memo', value: Math.max(0, k.salesTaxMemo), color: '#7c3aed' },
          { label: 'Open AR', value: Math.max(0, k.openReceivables), color: '#2563eb' }
        ])}
      </div>
      <div class="chart-card">
        <div class="card-header-lite">
          <h3>Open Obligations</h3>
          <span class="badge warn">AR / AP</span>
        </div>
        ${horizontalChart([
          { label: 'Receivable', value: k.openReceivables, color: '#2563eb' },
          { label: 'Payable', value: k.openPayables, color: '#dc2626' }
        ])}
      </div>
    </section>
    <div class="grid two">
      <div class="card">
        <div class="card-header-lite">
          <h3>Revenue Pulse</h3>
          <span class="badge">${(data.monthly || []).length} months</span>
        </div>
        <div class="bar-list">
          ${(data.monthly || []).map(m => metricBar(m.month, formatMoney(m.grossReceipts), Number(m.grossReceipts || 0) / monthlyMax * 100, m.estimatedNet >= 0 ? 'good' : 'bad')).join('') || emptyState('No monthly data yet.', 'Add sales to build a revenue trend.')}
        </div>
      </div>
      <div class="card">
        <div class="card-header-lite">
          <h3>Today’s Control Stack</h3>
          <span class="badge warn">${k.needsReviewCount} review</span>
        </div>
        <div class="control-stack">
          ${controlItem('Collect', k.openReceivables > 0 ? formatMoney(k.openReceivables) : 'Clear', 'Customer balances waiting on payment.', k.openReceivables > 0 ? 'warn' : 'good')}
          ${controlItem('Pay', k.openPayables > 0 ? formatMoney(k.openPayables) : 'Clear', 'Vendor bills still open.', k.openPayables > 0 ? 'warn' : 'good')}
          ${controlItem('Verify', `${k.needsReviewCount} rows`, 'Rows that need proof, cost, or status review.', k.needsReviewCount > 0 ? 'warn' : 'good')}
          ${controlItem('Back Up', 'SQLite DB', 'Use the backup button before big edits.', '')}
        </div>
      </div>
    </div>
    <div class="grid two">
      <div class="card">
        <h3>Open Receivables</h3>
        ${smallTable(data.openInvoices, ['invoiceNumber','customerName','projectName','dueDate','balanceDue','status'])}
      </div>
      <div class="card">
        <h3>Open Payables</h3>
        ${smallTable(data.openBills, ['vendorName','description','dueDate','balanceDue','status'])}
      </div>
    </div>
    <div class="grid two">
      <div class="card">
        <h3>Monthly Summary</h3>
        ${smallTable(data.monthly, ['month','orders','grossReceipts','salesTaxMemo','estimatedCosts','estimatedNet'])}
      </div>
      <div class="card">
        <h3>Action Items</h3>
        ${smallTable(data.actions, ['priority','area','title','status','relatedRecord'])}
      </div>
    </div>`;
}

function kpi(label, value, tip, cls = '', money = true) {
  return `<div class="kpi ${cls}"><span>${label} <span class="tip" data-tip="${escapeHtml(tip)}">?</span></span><strong>${money ? formatMoney(value) : escapeHtml(value)}</strong></div>`;
}
function insight(title, value, detail) {
  return `<div class="insight"><h3>${escapeHtml(title)}</h3><strong>${escapeHtml(value)}</strong><p>${escapeHtml(detail)}</p></div>`;
}
function metricBar(label, value, pct, cls = '') {
  return `<div class="metric-bar ${cls}"><div><strong>${escapeHtml(label)}</strong><span>${escapeHtml(value)}</span></div><div class="bar-track"><i style="width:${Math.max(4, Math.min(100, pct))}%"></i></div></div>`;
}
function controlItem(label, value, detail, cls = '') {
  return `<div class="control-item ${cls}"><div><span>${escapeHtml(label)}</span><strong>${escapeHtml(value)}</strong></div><p>${escapeHtml(detail)}</p></div>`;
}
function emptyState(title, detail) {
  return `<div class="empty-state"><div class="empty-icon">◇</div><div class="empty-title">${escapeHtml(title)}</div><div class="empty-desc">${escapeHtml(detail)}</div></div>`;
}
function lineChart(rows, labelKey, series) {
  if (!rows.length) return emptyState('No chart data yet.', 'Add sales to build chart history.');
  const width = 720, height = 260, pad = 34;
  const values = rows.flatMap(row => series.map(s => Number(row[s.key] || 0)));
  const max = Math.max(...values, 1);
  const min = Math.min(...values, 0);
  const span = max - min || 1;
  const x = i => rows.length === 1 ? width / 2 : pad + i * ((width - pad * 2) / (rows.length - 1));
  const y = v => height - pad - ((Number(v || 0) - min) / span) * (height - pad * 2);
  const paths = series.map(s => {
    const d = rows.map((row, i) => `${i === 0 ? 'M' : 'L'} ${x(i).toFixed(1)} ${y(row[s.key]).toFixed(1)}`).join(' ');
    const points = rows.map((row, i) => `<circle cx="${x(i).toFixed(1)}" cy="${y(row[s.key]).toFixed(1)}" r="4" fill="${s.color}"><title>${escapeHtml(s.label)} ${escapeHtml(row[labelKey])}: ${formatMoney(row[s.key])}</title></circle>`).join('');
    return `<path d="${d}" fill="none" stroke="${s.color}" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>${points}`;
  }).join('');
  const labels = rows.map((row, i) => `<text x="${x(i).toFixed(1)}" y="${height - 8}" text-anchor="middle">${escapeHtml(String(row[labelKey]).replace('2026-', ''))}</text>`).join('');
  return `<div class="chart-wrap">
    <svg viewBox="0 0 ${width} ${height}" role="img" aria-label="Revenue and net trend chart">
      <g class="chart-gridlines">
        <line x1="${pad}" x2="${width - pad}" y1="${pad}" y2="${pad}"></line>
        <line x1="${pad}" x2="${width - pad}" y1="${height / 2}" y2="${height / 2}"></line>
        <line x1="${pad}" x2="${width - pad}" y1="${height - pad}" y2="${height - pad}"></line>
      </g>
      <g class="chart-lines">${paths}</g>
      <g class="chart-labels">${labels}</g>
    </svg>
    <div class="chart-legend">${series.map(s => `<span><i style="background:${s.color}"></i>${escapeHtml(s.label)}</span>`).join('')}</div>
  </div>`;
}
function donutChart(items) {
  const total = items.reduce((sum, item) => sum + Number(item.value || 0), 0);
  if (total <= 0) return emptyState('No money mix yet.', 'Add sales, costs, or AR to fill this chart.');
  let offset = 25;
  const radius = 36;
  const circumference = 2 * Math.PI * radius;
  const slices = items.map(item => {
    const pct = Number(item.value || 0) / total;
    const dash = pct * circumference;
    const slice = `<circle r="${radius}" cx="50" cy="50" fill="none" stroke="${item.color}" stroke-width="16" stroke-dasharray="${dash} ${circumference - dash}" stroke-dashoffset="${offset}"><title>${escapeHtml(item.label)}: ${formatMoney(item.value)}</title></circle>`;
    offset -= dash;
    return slice;
  }).join('');
  return `<div class="donut-layout">
    <svg class="donut" viewBox="0 0 100 100" role="img" aria-label="Money mix donut chart">
      <circle r="${radius}" cx="50" cy="50" fill="none" stroke="#e2e8f0" stroke-width="16"></circle>
      <g transform="rotate(-90 50 50)">${slices}</g>
      <text x="50" y="47" text-anchor="middle" class="donut-total">${formatCompact(total)}</text>
      <text x="50" y="60" text-anchor="middle" class="donut-caption">tracked</text>
    </svg>
    <div class="donut-legend">${items.map(item => `<div><span><i style="background:${item.color}"></i>${escapeHtml(item.label)}</span><strong>${formatMoney(item.value)}</strong></div>`).join('')}</div>
  </div>`;
}
function horizontalChart(items) {
  const max = Math.max(...items.map(item => Number(item.value || 0)), 1);
  return `<div class="horizontal-chart">${items.map(item => {
    const pct = Math.max(3, Number(item.value || 0) / max * 100);
    return `<div class="hbar-row"><div><strong>${escapeHtml(item.label)}</strong><span>${formatMoney(item.value)}</span></div><div class="hbar-track"><i style="width:${pct}%; background:${item.color}"></i></div></div>`;
  }).join('')}</div>`;
}
function formatCompact(value) {
  return Number(value || 0).toLocaleString(undefined, { notation: 'compact', maximumFractionDigits: 1 });
}
function smallTable(rows, cols) {
  if (!rows || rows.length === 0) return emptyState('Nothing here yet.', 'This area will fill in as records are added.');
  return `<div class="table-wrap"><table><thead><tr>${cols.map(c=>`<th>${labelize(c)}</th>`).join('')}</tr></thead><tbody>${rows.map(r=>`<tr>${cols.map(c=>`<td>${c.toLowerCase().includes('status') || c === 'priority' ? badgeFor(r[c], r.needsReview) : formatValue(r[c], c)}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`;
}

async function renderEntity(el, config) {
  const rows = await api(`/api/${config.route}`);
  const reviewCount = rows.filter(x => x.needsReview === true).length;
  const moneyTotal = summarizeMoney(rows, config);
  el.innerHTML = `
    <div class="page-head">
      <div>
        <h2>${config.title}</h2>
        <p>${config.purpose}</p>
      </div>
      <div class="actions">
        <button class="primary-button" id="addRowBtn">Add New</button>
        <button class="ghost-button" id="reviewQueueBtn">Review Queue</button>
        <a class="ghost-button" href="/api/export/${config.route}" title="Export this table to a CSV file you can open in Excel.">Export CSV</a>
      </div>
    </div>
    <section class="entity-strip">
      <div><span>Active Rows</span><strong>${rows.length}</strong></div>
      <div><span>Review Queue</span><strong>${reviewCount}</strong></div>
      <div><span>Visible Money</span><strong>${formatMoney(moneyTotal)}</strong></div>
      <div><span>Export Ready</span><strong>CSV</strong></div>
    </section>
    <div class="card">
      <div class="table-tools">
        <input class="search" id="searchBox" placeholder="Search this table..." title="Filters this page only. It does not delete or change anything.">
        <select id="statusFilter" class="search compact" title="Filter by status/priority/review state.">
          <option value="">All records</option>
          <option value="needs-review">Needs review</option>
          <option value="open">Open / sent / unpaid</option>
          <option value="paid">Paid / done / completed</option>
        </select>
        <span class="badge">${rows.length} active rows</span>
      </div>
      <div id="tableArea"></div>
    </div>`;
  qs('#addRowBtn').onclick = () => openModal(config, null);
  qs('#reviewQueueBtn').onclick = () => {
    qs('#searchBox').value = '"needsReview":true';
    renderEntityTable(config, rows, '"needsReview":true');
  };
  qs('#searchBox').oninput = e => renderEntityTable(config, rows, e.target.value);
  qs('#statusFilter').onchange = () => renderEntityTable(config, rows, qs('#searchBox').value);
  renderEntityTable(config, rows, '');
}

const tablePageState = {};

function renderEntityTable(config, rows, filter) {
  const f = (filter || '').toLowerCase();
  const statusFilter = qs('#statusFilter')?.value || '';
  const filtered = rows.filter(r => {
    const blob = JSON.stringify(r).toLowerCase();
    if (f && !blob.includes(f)) return false;
    if (statusFilter === 'needs-review') return r.needsReview === true;
    if (statusFilter === 'open') return /open|sent|unpaid|partial|waiting|lead|quoted|progress/.test(blob);
    if (statusFilter === 'paid') return /paid|done|completed|fulfilled/.test(blob);
    return true;
  });
  const cols = config.columns;
  const tableArea = qs('#tableArea');
  if (filtered.length === 0) {
    tableArea.innerHTML = emptyState('No matching rows.', 'Clear the search or change the filter.');
    return;
  }

  const key = config.route;
  if (!tablePageState[key]) tablePageState[key] = { page: 0, pageSize: 25 };
  const state = tablePageState[key];
  // Reset to page 0 when filter changes
  if (filter !== state._lastFilter || statusFilter !== state._lastStatus) {
    state.page = 0;
    state._lastFilter = filter;
    state._lastStatus = statusFilter;
  }
  const pageSize = state.pageSize;
  const totalPages = Math.ceil(filtered.length / pageSize);
  if (state.page >= totalPages) state.page = totalPages - 1;
  const pageRows = filtered.slice(state.page * pageSize, (state.page + 1) * pageSize);

  const pagerHtml = filtered.length > 25 ? `
    <div class="table-pager">
      <span class="pager-info">${filtered.length} rows &nbsp;|&nbsp; Page ${state.page + 1} of ${totalPages}</span>
      <span class="pager-controls">
        <button class="ghost-button pager-btn" id="pgFirst" ${state.page === 0 ? 'disabled' : ''}>«</button>
        <button class="ghost-button pager-btn" id="pgPrev" ${state.page === 0 ? 'disabled' : ''}>‹ Prev</button>
        <select id="pgSize" class="pager-size">
          ${[25,50,100].map(n => `<option value="${n}"${n === pageSize ? ' selected' : ''}>${n} / page</option>`).join('')}
        </select>
        <button class="ghost-button pager-btn" id="pgNext" ${state.page >= totalPages - 1 ? 'disabled' : ''}>Next ›</button>
        <button class="ghost-button pager-btn" id="pgLast" ${state.page >= totalPages - 1 ? 'disabled' : ''}>»</button>
      </span>
    </div>` : '';

  tableArea.innerHTML = `<div class="table-wrap"><table><thead><tr>${cols.map(c=>`<th>${labelize(c)}</th>`).join('')}<th>Actions</th></tr></thead><tbody>${pageRows.map(row => `
    <tr>
      ${cols.map(c => `<td>${cell(row, c)}</td>`).join('')}
      <td class="row-actions">
        <button class="ghost-button" data-edit="${row.id}">Edit</button>
        ${row.needsReview === true ? `<button class="ghost-button" data-reviewed="${row.id}">Reviewed</button>` : ''}
        <button class="danger-button" data-delete="${row.id}">Archive</button>
      </td>
    </tr>`).join('')}</tbody></table></div>${pagerHtml}`;

  qsa('[data-edit]').forEach(btn => btn.onclick = () => openModal(config, filtered.find(r => r.id == btn.dataset.edit)));
  qsa('[data-reviewed]').forEach(btn => btn.onclick = () => markReviewed(config, filtered.find(r => r.id == btn.dataset.reviewed)));
  qsa('[data-delete]').forEach(btn => btn.onclick = () => archiveRow(config, btn.dataset.delete));

  if (filtered.length > 25) {
    qs('#pgFirst')?.addEventListener('click', () => { state.page = 0; renderEntityTable(config, rows, filter); });
    qs('#pgPrev')?.addEventListener('click', () => { state.page--; renderEntityTable(config, rows, filter); });
    qs('#pgNext')?.addEventListener('click', () => { state.page++; renderEntityTable(config, rows, filter); });
    qs('#pgLast')?.addEventListener('click', () => { state.page = totalPages - 1; renderEntityTable(config, rows, filter); });
    qs('#pgSize')?.addEventListener('change', e => { state.pageSize = Number(e.target.value); state.page = 0; renderEntityTable(config, rows, filter); });
  }
}

function summarizeMoney(rows, config) {
  const preferred = ['customerPaid','invoiceTotal','total','currentBalance','amount','targetPrice','giftCardAmount'];
  const fields = preferred.filter(name => config.columns.includes(name) || config.fields.some(f => f.name === name));
  const field = fields[0];
  if (!field) return 0;
  return rows.reduce((sum, row) => sum + Number(row[field] || 0), 0);
}

function cell(row, key) {
  if (key === 'needsReview') return row.needsReview ? badgeFor('Needs Review', true) : badgeFor('OK');
  if (key.toLowerCase().includes('status') || key === 'priority') return badgeFor(row[key], row.needsReview);
  if (key === 'estimatedCost') {
    const cost = (Number(row.grams || 0) * Number(row.materialCostPerGram || 0))
               + (Number(row.printHours || 0) * Number(row.machineRatePerHour || 0))
               + Number(row.packagingCost || 0);
    const price = Number(row.targetPrice || 0);
    const label = formatMoney(cost);
    if (cost > 0 && price > 0 && price < cost) return `<span style="color:#dc2626;font-weight:700" title="Target price is below estimated cost">${label} ⚠</span>`;
    return escapeHtml(label);
  }
  if (key === 'netBeforeCogs') {
    const net = Number(row.customerPaid || 0) - Number(row.platformFees || 0) - Number(row.shippingLabelCost || 0) - Number(row.refunds || 0);
    return escapeHtml(formatMoney(net));
  }
  if (key === 'estNetAfterCogs') {
    const net = Number(row.customerPaid || 0) - Number(row.platformFees || 0) - Number(row.shippingLabelCost || 0) - Number(row.refunds || 0) - Number(row.estimatedCogs || 0);
    const label = formatMoney(net);
    if (net < 0) return `<span style="color:#dc2626;font-weight:700" title="Est. net is negative after COGS">${escapeHtml(label)} ⚠</span>`;
    return escapeHtml(label);
  }
  return escapeHtml(formatValue(row[key], key));
}

function labelize(name) {
  return name.replace(/([A-Z])/g, ' $1').replace(/^./, s => s.toUpperCase()).replace('Cogs','COGS').replace('Ar','AR').replace('Ap','AP');
}

function renderQuickAdd(el) {
  el.innerHTML = `
    <div class="page-head"><div><h2>Quick Add</h2><p>Use this for simple ledger events: paid sale, unpaid invoice entered outside the builder, paid expense, vendor bill, or manual quote record.</p></div><div class="actions"><button class="ghost-button" onclick="showPage('estimates')">New Estimate</button><button class="ghost-button" onclick="showPage('invoices')">New Invoice</button><button class="ghost-button" onclick="showPage('workflowGuide')">Workflow Guide</button></div></div>
    <div class="quick-grid">
      ${quickCard('Estimate Sent (External)','You sent a quote made outside this app and are waiting for approval. The built-in Estimates builder tracks these automatically.','customerJobs','estimateSent')}
      ${quickLinkCard('New Estimate PDF','Build a real customer estimate with calculator, line items, and PDF preview.','estimates')}
      ${quickLinkCard('New Invoice PDF','Build a real customer invoice with line items, AR sync, and PDF preview.','invoices')}
      ${quickCard('Etsy Sale','A paid Etsy order or marketplace sale.','sales','etsy')}
      ${quickCard('Direct Paid Sale','Customer already paid you directly.','sales','directPaid')}
      ${quickCard('Open Invoice / AR','You sent a direct invoice and are waiting for payment.','receivables','invoice')}
      ${quickCard('Bill / AP','You owe a vendor and have not paid yet.','bills','bill')}
      ${quickCard('Paid Expense','You already bought supplies, labels, software, etc.','expenses','expense')}
      ${quickCard('Equipment / Asset Purchase','A printer, AMS, durable tool, computer, or higher-value item that needs tax-treatment review.','assets','assetPurchase')}
    </div>
    <div class="grid two">
      <div class="card"><h3>Clean Books Rule</h3><p><b>Sales</b> are money coming in. <b>AR invoices</b> are money customers owe you. <b>AP bills</b> are money you owe. <b>Expenses</b> are things already paid. <b>Customer Jobs</b> track the work itself.</p></div>
      <div class="card"><h3>Proof Rule</h3><p>Every meaningful row should eventually point to a PDF, receipt, screenshot, order number, or uploaded document in Audit Docs.</p></div>
    </div>`;
  qsa('.quick-card[data-page]').forEach(card => card.onclick = () => showPage(card.dataset.page));
  qsa('.quick-card[data-config]').forEach(card => card.onclick = () => quickOpen(card.dataset.config, card.dataset.kind));
}

function quickLinkCard(title, desc, page) {
  return `<button class="quick-card" data-page="${page}"><strong>${escapeHtml(title)}</strong><span>${escapeHtml(desc)}</span></button>`;
}

function renderWorkflowGuide(el) {
  const flows = [
    ['Estimate needed', 'Estimates -> New Estimate', 'Use the builder when you need a customer-facing quote/PDF. Saving an estimate creates or updates a quoted Customer Job. It is not income and not AR.'],
    ['Estimate sent manually, waiting for approval', 'Quick Add -> Estimate Sent', 'Use this only when you are recording a quote made outside this app. This is not income and not AR yet.'],
    ['Etsy order came in', 'Quick Add → Etsy Sale', 'Creates the money record. Keep Needs Review on until Etsy fees, actual label cost, packaging, and COGS are entered. Add/attach proof from the sale form or Document Intake.'],
    ['Direct customer asks for work', 'Estimates or Customer Jobs', 'Create an estimate if they need a customer-facing quote. Use Customer Jobs for internal tracking when no PDF is needed.'],
    ['Invoice needed', 'Invoices -> New Invoice', 'Saving an invoice creates or updates AR. AR tracks money owed, not cash received.'],
    ['You sent an invoice manually', 'Quick Add → Open Invoice / AR', 'Use this only when recording an invoice made outside this app. Do not count it as cash until paid.'],
    ['Customer already paid direct', 'Quick Add → Direct Paid Sale', 'Use one sale row. Add the invoice number if there is one, and attach the invoice/proof file.'],
    ['You bought something and paid already', 'Quick Add → Paid Expense', 'One expense row is enough. Attach receipt proof.'],
    ['Vendor billed you, not paid yet', 'Quick Add → Bill / AP', 'Use AP Bills until paid. If you later want cash-basis expense reporting, add/confirm an Expense when paid.'],
    ['You only have a PDF/receipt first', 'Document Intake', 'Upload it to create an Audit Doc. Then use the next-step buttons or Attach File on a row; intake indexes proof but does not silently post accounting records.']
  ];
  el.innerHTML = `
    <section class="workspace-hero">
      <div>
        <span class="eyebrow">How To Use This Ledger</span>
        <h2>Pick the real-world event, enter it once, then attach proof.</h2>
        <p>Tabs are ledgers/views, not chores to fill one by one. Use the builder for customer PDFs, Quick Add for simple events, and Document Intake for proof files.</p>
      </div>
      <div class="hero-actions">
        <button class="ghost-button dark" onclick="showPage('estimates')">New Estimate</button>
        <button class="ghost-button dark" onclick="showPage('invoices')">New Invoice</button>
        <button class="primary-button" onclick="showPage('quickAdd')">Start Quick Add</button>
        <button class="ghost-button dark" onclick="showPage('documentIntake')">Upload Proof</button>
      </div>
    </section>
    ${workflowDiagram()}
    <div class="grid two">
      <div class="card">
        <h3>Where To Start</h3>
        <p><b>Use Estimates or Invoices</b> when you need a customer-facing PDF, calculator, line items, or live preview.</p>
        <p><b>Use Quick Add</b> for simple bookkeeping events that do not need the PDF builder.</p>
        <p><b>Use Document Intake</b> when the file comes first. It stores proof; you still approve what business record it belongs to.</p>
        <p>Use individual tabs when you are reviewing, editing, exporting, or doing a specific cleanup task.</p>
      </div>
      <div class="card">
        <h3>What The App Updates For You</h3>
        <p><b>Estimate saved:</b> creates/updates a quoted Customer Job. It does not count as income.</p>
        <p><b>Invoice saved:</b> creates/updates AR so you know who owes money.</p>
        <p><b>Invoice marked paid:</b> updates AR and creates/updates the Direct Sale for cash reporting.</p>
        <p><b>Proof uploaded:</b> creates an Audit Doc and stores the local file path.</p>
      </div>
    </div>
    <div class="card">
      <div class="card-header-lite"><h3>Where Do I Enter This?</h3><span class="badge">Step by step</span></div>
      <div class="workflow-list">
        ${flows.map(([event, place, rule]) => `<div class="workflow-row"><div><span>${escapeHtml(event)}</span><strong>${escapeHtml(place)}</strong></div><p>${escapeHtml(rule)}</p></div>`).join('')}
      </div>
    </div>
    <div class="grid two">
      <div class="card">
        <h3>Avoid Duplicate Entry</h3>
        <p>Do not create a Sale, AR Invoice, Job, and Audit Doc for every single thing by default. Use the extra records only when they mean something different:</p>
        <div class="help-list">
          <div class="help-item"><strong>Job</strong><p>The work/project history.</p></div>
          <div class="help-item"><strong>AR Invoice</strong><p>Money a direct customer owes you.</p></div>
          <div class="help-item"><strong>Sale</strong><p>Money that actually came in.</p></div>
          <div class="help-item"><strong>Audit Doc</strong><p>The proof file or reference.</p></div>
        </div>
      </div>
      <div class="card">
        <h3>Growing Past $1K</h3>
        <p>Once you cross $1K net profit these habits pay off fast. Start them now so they are automatic later.</p>
        <div class="help-list">
          <div class="help-item"><strong>Every sale has proof</strong><p>Etsy order PDF, direct invoice, or screenshot attached as an Audit Doc. The IRS wants receipts, not memory.</p></div>
          <div class="help-item"><strong>Separate bank account</strong><p>Business-only checking makes end-of-year reconciliation hours faster and keeps personal spending out of COGS.</p></div>
          <div class="help-item"><strong>Review weekly</strong><p>Five minutes once a week clearing Needs Review rows is easier than one marathon before taxes.</p></div>
          <div class="help-item"><strong>Track COGS per item</strong><p>Use Products / Costing to record filament cost, print hours, and packaging so you know which jobs actually make money.</p></div>
        </div>
      </div>
    </div>`;
}

function quickCard(title, text, config, kind) {
  return `<button class="quick-card" data-config="${config}" data-kind="${kind}"><strong>${title}</strong><span>${text}</span></button>`;
}
function quickOpen(configKey, kind) {
  const config = configs[configKey];
  const today = new Date().toISOString().substring(0,10);
  const presets = {
    estimateSent: { platform: 'Direct', status: 'Quoted', jobDate: today, jobType: 'Estimate', needsReview: false, notes: 'Estimate entered manually. Do not count as income or AR until approved/invoiced.' },
    etsy: { platform: 'Etsy', status: 'Paid', saleDate: today, quantity: 1, includeInDashboard: true, needsReview: true, notes: 'Remember to enter Etsy fees, actual label cost, packaging, and COGS.' },
    directPaid: { platform: 'Direct', status: 'Paid', saleDate: today, quantity: 1, includeInDashboard: true },
    invoice: { status: 'Sent', invoiceDate: today, includeInCashReports: false, needsReview: false },
    bill: { status: 'Unpaid', billDate: today, taxDeductible: true },
    expense: { expenseDate: today, taxDeductible: true, taxBucket: 'Review', deductibleStatus: 'Review', countedExpense: true, businessUsePercent: 100, needsReview: true, notes: 'Classify as COGS/Materials, Operating Expense, Asset, or Memo Only before filing.' },
    assetPurchase: { purchaseDate: today, inServiceDate: today, category: 'Equipment', businessUsePercent: 100, taxTreatment: 'Review', countedExpenseThisYear: false, needsReview: true, notes: 'Review with tax preparer before choosing Section 179, De Minimis Expense, or Depreciation.' }
  };
  openModal(config, presets[kind] || null, true);
}

function openModal(config, row, presetOnly = false) {
  appState.modal = { config, row: row || {}, mode: row && !presetOnly && row.id ? 'edit' : 'create' };
  qs('#modalTitle').textContent = `${appState.modal.mode === 'edit' ? 'Edit' : 'Add'} ${config.title}`;
  qs('#modalHelp').textContent = config.purpose;
  const form = qs('#modalForm');
  form.innerHTML = config.fields.map(field => renderField(field, row || {})).join('');
  qs('#modalBackdrop').classList.remove('hidden');
  qs('#modal').classList.remove('hidden');
  initProofPickers(config);
  initMoneyFormCalculators(config);
}

function renderField(field, row) {
  const value = row[field.name];
  const full = field.span === 'full' || field.type === 'textarea';
  const label = `<label for="field_${field.name}">${field.label} <span class="tip" data-tip="${escapeHtml(field.tip || '')}">?</span></label>`;
  if (field.type === 'checkbox') {
    return `<div class="form-field ${full ? 'full' : ''}"><label class="check-row"><input id="field_${field.name}" name="${field.name}" type="checkbox" ${value ? 'checked' : ''}> ${field.label} <span class="tip" data-tip="${escapeHtml(field.tip || '')}">?</span></label></div>`;
  }
  if (field.type === 'select') {
    const options = (field.options || []).map(o => `<option value="${escapeHtml(o)}" ${value === o ? 'selected' : ''}>${escapeHtml(o)}</option>`).join('');
    return `<div class="form-field ${full ? 'full' : ''}">${label}<select id="field_${field.name}" name="${field.name}"><option value=""></option>${options}</select></div>`;
  }
  if (field.type === 'textarea') {
    return `<div class="form-field full">${label}<textarea id="field_${field.name}" name="${field.name}">${escapeHtml(value ?? '')}</textarea></div>`;
  }
  const inputValue = dateFields.has(field.name) && value ? String(value).substring(0,10) : (value ?? '');
  if (proofFieldNames.has(field.name)) {
    return `<div class="form-field ${full ? 'full' : ''} proof-field">${label}<div class="proof-control"><input id="field_${field.name}" name="${field.name}" type="${field.type}" value="${escapeHtml(inputValue)}" placeholder="Upload a proof file or paste a OneDrive/local path"><button class="ghost-button" type="button" data-proof-upload="${field.name}">Attach File</button><input class="hidden" id="proof_file_${field.name}" type="file" accept=".pdf,.png,.jpg,.jpeg,.webp,.txt,.csv,.json,.md,application/pdf,image/*,text/*"></div><small class="proof-help">Saves a copy into UploadedDocs, creates an Audit Doc, then fills this field with the saved local path.</small></div>`;
  }
  return `<div class="form-field ${full ? 'full' : ''}">${label}<input id="field_${field.name}" name="${field.name}" type="${field.type}" step="0.01" value="${escapeHtml(inputValue)}"></div>`;
}

function initProofPickers(config) {
  qsa('[data-proof-upload]').forEach(button => {
    const fieldName = button.dataset.proofUpload;
    const fileInput = qs(`#proof_file_${fieldName}`);
    button.onclick = () => fileInput.click();
    fileInput.onchange = async () => {
      if (!fileInput.files || fileInput.files.length === 0) return;
      await uploadProofForField(config, fieldName, fileInput.files[0]);
      fileInput.value = '';
    };
  });
}

function initMoneyFormCalculators(config) {
  const watch = names => names.map(name => qs(`#field_${name}`)).filter(Boolean);
  const numberVal = name => Number(qs(`#field_${name}`)?.value || 0);
  const setMoney = (name, value) => {
    const input = qs(`#field_${name}`);
    if (input && !input.dataset.userEdited) input.value = value ? value.toFixed(2) : '';
  };

  const formulas = {
    bills: () => setMoney('total', numberVal('amount') + numberVal('salesTax')),
    expenses: () => setMoney('total', numberVal('amount') + numberVal('salesTax')),
    'receivable-invoices': () => setMoney('invoiceTotal', Math.max(0, numberVal('subtotal') - numberVal('discount') + numberVal('rushFee') + numberVal('salesTax')))
  };
  const update = formulas[config.route];
  if (!update) return;

  const totalField = qs('#field_total') || qs('#field_invoiceTotal');
  if (totalField) totalField.addEventListener('input', () => { totalField.dataset.userEdited = 'true'; });
  watch(['amount', 'salesTax', 'subtotal', 'discount', 'rushFee']).forEach(input => input.addEventListener('input', update));
  update();
}

async function uploadProofForField(config, fieldName, file) {
  const target = qs(`#field_${fieldName}`);
  const form = new FormData();
  form.append('files', file);
  form.append('relatedType', config.title);
  form.append('relatedNumber', proofRelatedNumber());

  target.disabled = true;
  try {
    const response = await fetch('/api/documents/upload', { method: 'POST', body: form });
    if (!response.ok) throw new Error(await response.text());
    const result = await response.json();
    const doc = result.documents && result.documents[0];
    target.value = doc?.filePathOrUrl || doc?.fileName || file.name;
    toast('Proof file attached and indexed.');
  } catch (err) {
    toast(`Proof upload failed: ${err.message}`);
  } finally {
    target.disabled = false;
  }
}

function proofRelatedNumber() {
  const candidates = ['invoiceNumber', 'orderNumber', 'relatedOrderNumber', 'relatedInvoiceNumber', 'billNumber', 'jobNumber', 'name', 'title'];
  for (const name of candidates) {
    const input = qs(`#field_${name}`);
    if (input && input.value) return input.value;
  }
  return '';
}

async function saveModal() {
  const { config, row, mode } = appState.modal;
  const payload = { ...(row || {}) };
  config.fields.forEach(field => {
    const input = qs(`#field_${field.name}`);
    if (!input) return;
    if (field.type === 'checkbox') payload[field.name] = input.checked;
    else if (field.type === 'number') payload[field.name] = input.value === '' ? null : Number(input.value);
    else if (field.type === 'date') payload[field.name] = input.value || null;
    else payload[field.name] = input.value || null;
  });
  try {
    if (mode === 'edit') {
      await api(`/api/${config.route}/${payload.id}`, { method: 'PUT', body: JSON.stringify(payload) });
    } else {
      delete payload.id;
      await api(`/api/${config.route}`, { method: 'POST', body: JSON.stringify(payload) });
    }
    closeModal();
    toast('Saved.');
    await showPage(appState.currentPage);
  } catch (err) {
    toast(`Save failed: ${err.message}`);
  }
}

async function archiveRow(config, id) {
  if (!confirm('Archive this row? It will be hidden but not permanently deleted.')) return;
  await api(`/api/${config.route}/${id}`, { method: 'DELETE' });
  toast('Archived.');
  await showPage(appState.currentPage);
}
async function markReviewed(config, row) {
  if (!row) return;
  await api(`/api/${config.route}/${row.id}`, { method: 'PUT', body: JSON.stringify({ ...row, needsReview: false }) });
  toast('Marked reviewed.');
  await showPage(appState.currentPage);
}
function closeModal() {
  qs('#modalBackdrop').classList.add('hidden');
  qs('#modal').classList.add('hidden');
}

function renderDocumentIntake(el) {
  el.innerHTML = `
    <div class="page-head">
      <div>
        <h2>Document Intake</h2>
        <p>Upload receipts, invoices, Etsy order PDFs, screenshots, CSVs, and notes. Files are saved locally and indexed as Audit Docs so every record can point back to proof.</p>
      </div>
      <div class="actions">
        <button class="ghost-button" onclick="showPage('auditDocs')">Open Audit Docs</button>
      </div>
    </div>
    <div class="grid two">
      <section class="upload-panel">
        <h3>Upload Proof</h3>
        <div class="upload-zone" id="uploadZone">
          <div class="upload-mark">⇪</div>
          <strong>Drop files here or choose them below</strong>
          <input id="docFiles" type="file" multiple accept=".pdf,.png,.jpg,.jpeg,.webp,.txt,.csv,.json,.md,application/pdf,image/*,text/*">
          <div class="form-grid" style="width:100%; padding:0;">
            <div class="form-field">
              <label for="relatedType">Related Area</label>
              <select id="relatedType">
                <option value=""></option>
                <option>Sale</option>
                <option>Invoice</option>
                <option>Bill</option>
                <option>Expense</option>
                <option>Customer Job</option>
                <option>Product</option>
                <option>Tax</option>
              </select>
            </div>
            <div class="form-field">
              <label for="relatedNumber">Order / Invoice / Record #</label>
              <input id="relatedNumber" type="text" placeholder="Example: INV-2026-0002 or Etsy order #">
            </div>
          </div>
          <button class="primary-button" id="uploadDocsBtn">Upload and Index</button>
        </div>
      </section>
      <section class="card">
        <h3>What Happens After Upload?</h3>
        <p><b>One upload creates one Audit Doc.</b> That Audit Doc is your proof file/index card. It does not automatically fill every tab.</p>
        <p>After upload, choose the next step: create the Sale/Expense/Invoice/Bill that the file proves, or edit the Audit Doc to add missing notes, related record number, and review status.</p>
        <p>Text, CSV, JSON, and Markdown files get a clean preview. PDFs get best-effort text preview when the PDF stores readable text. Scanned image-only PDFs still need OCR later.</p>
      </section>
    </div>
    <div class="card">
      <h3>After Upload</h3>
      <div id="uploadResult" class="upload-result empty-state">
        <div class="empty-icon">⇪</div>
        <div class="empty-title">No upload yet.</div>
        <div class="empty-desc">Uploaded files will appear here with next-step buttons.</div>
      </div>
    </div>`;
  qs('#uploadDocsBtn').onclick = uploadDocuments;
  const zone = qs('#uploadZone');
  zone.ondragover = e => { e.preventDefault(); zone.classList.add('dragging'); };
  zone.ondragleave = () => zone.classList.remove('dragging');
  zone.ondrop = e => {
    e.preventDefault();
    zone.classList.remove('dragging');
    qs('#docFiles').files = e.dataTransfer.files;
    toast(`${e.dataTransfer.files.length} file${e.dataTransfer.files.length === 1 ? '' : 's'} ready.`);
  };
}

async function renderGlobalSearch(el) {
  const query = (qs('#globalSearch').value || '').trim().toLowerCase();
  if (!query) {
    el.innerHTML = `<div class="page-head"><div><h2>Global Search</h2><p>Search customer names, invoice numbers, order numbers, vendors, notes, and proof references across the ledger.</p></div></div>${emptyState('Start typing above.', 'Results appear here as you search.')}`;
    return;
  }

  const entries = await Promise.all(Object.entries(configs).map(async ([key, config]) => {
    const rows = await api(`/api/${config.route}`);
    return rows
      .filter(row => JSON.stringify(row).toLowerCase().includes(query))
      .slice(0, 8)
      .map(row => ({ key, config, row }));
  }));
  const results = entries.flat();
  el.innerHTML = `
    <div class="page-head"><div><h2>Search Results</h2><p>${results.length} result${results.length === 1 ? '' : 's'} for "${escapeHtml(query)}".</p></div></div>
    <div class="search-results">
      ${results.length ? results.map(resultCard).join('') : emptyState('No results found.', 'Try a customer, invoice number, order number, vendor, or file name.')}
    </div>`;
}

function resultCard({ key, config, row }) {
  const title = row.customerName || row.vendorName || row.name || row.title || row.invoiceNumber || row.orderNumber || row.fileName || config.title;
  const subtitle = [row.invoiceNumber, row.orderNumber, row.relatedRecordNumber, row.status, row.platform].filter(Boolean).join(' · ');
  return `<button class="result-card" onclick="showPage('${key}')">
    <span class="badge">${escapeHtml(config.nav || config.title)}</span>
    <strong>${escapeHtml(title)}</strong>
    <small>${escapeHtml(subtitle || 'Open table')}</small>
  </button>`;
}

async function renderMergedInvoiceTool(el, initialView = 'dashboard', newType = '', restoreSnapshot = null) {
  ensureMergedInvoiceStyles();
  const html = await fetch('/invoice-builder/index.html').then(r => {
    if (!r.ok) throw new Error(`Invoice builder shell failed to load: ${r.status}`);
    return r.text();
  });
  const doc = new DOMParser().parseFromString(html, 'text/html');
  const main = doc.querySelector('#main');
  if (!main) throw new Error('Invoice builder markup is missing its main workspace.');

  el.innerHTML = `
    <section id="epataInvoiceMerged" class="invoice-tool-merged">
      <div class="merged-invoice-bar">
        <div>
          <span class="eyebrow">Merged Estimate & Invoice Workspace</span>
          <h2>Estimator, calculator, records, and PDF preview</h2>
          <p>This is the original invoice tool running inside the Business Ledger shell and saving to the unified SQLite database.</p>
        </div>
        <div class="merged-tabs">
          <button class="nav-item" data-view="dashboard">Dashboard</button>
          <button class="nav-item" data-view="calculator">Calculator</button>
          <button class="nav-item" data-view="builder">Builder + PDF</button>
          <button class="nav-item" data-view="records">Records</button>
          <button class="nav-item" data-view="ratecard">Rate Card</button>
          <button class="nav-item" data-view="settings">Settings</button>
        </div>
      </div>
      <main id="main">${main.innerHTML}</main>
    </section>`;

  qsa('#epataInvoiceMerged [onclick*="window.location.href"]').forEach(button => button.remove());

  // Wire difficulty preset cards directly — no module dependency
  qsa('#difficultyGrid .diff-card').forEach(btn => {
    btn.addEventListener('click', () => {
      qsa('#difficultyGrid .diff-card').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      const hidden = document.getElementById('difficulty');
      if (hidden) {
        hidden.value = btn.dataset.val;
        hidden.dispatchEvent(new Event('change'));
        hidden.dispatchEvent(new Event('input'));
      }
    });
  });

  const module = await import('/invoice-builder/js/app.js?v=3');
  await module.init({ initialView, restoreSnapshot });
  if (newType && !restoreSnapshot) {
    const buttonId = newType === 'INVOICE' ? 'btnNewInvoice' : 'btnNewEstimate';
    setTimeout(() => qs(`#${buttonId}`)?.click(), 50);
  }
}

function ensureMergedInvoiceStyles() {
  if (!document.getElementById('invoiceBuilderCss')) {
    const link = document.createElement('link');
    link.id = 'invoiceBuilderCss';
    link.rel = 'stylesheet';
    link.href = '/invoice-builder/css/app.css';
    document.head.appendChild(link);
  }
  document.getElementById('invoiceBuilderCss').disabled = false;
  if (!document.getElementById('invoiceBuilderRootOverrides')) {
    const style = document.createElement('style');
    style.id = 'invoiceBuilderRootOverrides';
    style.textContent = `
      body #app { display: block; min-height: 0; }
      body #main { margin-left: 0; }
      .invoice-tool-merged { display: block; min-height: calc(100vh - 112px); background: #f3f6fb; border: 1px solid #dbe3ef; border-radius: 14px; overflow: auto; }
      .invoice-tool-merged #main { margin-left: 0 !important; min-height: 0; }
      .invoice-tool-merged .view-header, .invoice-tool-merged .view-body, .invoice-tool-merged .active-record-bar { padding-left: 16px; padding-right: 16px; }
      .invoice-tool-merged .view-header { padding-top: 12px; padding-bottom: 0; }
      .invoice-tool-merged .view-body { padding-top: 12px; }
      .merged-invoice-bar { display: flex; justify-content: space-between; gap: 10px; align-items: center; padding: 6px 8px; background: #10244c; color: #fff; }
      .merged-invoice-bar h2 { margin: 0; font-size: .82rem; line-height: 1; white-space: nowrap; }
      .merged-invoice-bar .eyebrow { display: none; }
      .merged-invoice-bar p { display: none; }
      .merged-tabs { display: flex; flex-wrap: nowrap; gap: 4px; justify-content: flex-end; overflow-x: auto; }
      .merged-tabs .nav-item { min-height: 26px; border: 1px solid rgba(255,255,255,.18); background: rgba(255,255,255,.08); color: #fff; border-radius: 6px; padding: 4px 8px; font-weight: 800; cursor: pointer; font-size: .72rem; white-space: nowrap; }
      .merged-tabs .nav-item.active { background: #fff; color: #17468f; }
      .invoice-tool-merged .active-record-bar { min-height: 28px; padding-top: 5px; padding-bottom: 5px; }
      .invoice-tool-merged #view-builder .view-title p { display: none; }
      .invoice-tool-merged .view { min-height: 0; }
      .invoice-tool-merged .invoice-preview-frame { background: #fff; }
      @media (max-width: 760px) { .merged-invoice-bar { align-items: stretch; flex-direction: column; } .merged-tabs { justify-content: flex-start; } }
    `;
    document.head.appendChild(style);
  }
  document.getElementById('invoiceBuilderRootOverrides').disabled = false;
}

function toggleMergedInvoiceStyles(enabled) {
  const link = document.getElementById('invoiceBuilderCss');
  const style = document.getElementById('invoiceBuilderRootOverrides');
  if (link) link.disabled = !enabled;
  if (style) style.disabled = !enabled;
}

async function renderInvoiceCenter(el) {
  await renderInvoiceWorkspace(el, '');
}

async function renderInvoiceWorkspace(el, typeFocus = '') {
  const [docs, stats] = await Promise.all([
    api('/api/invoice-documents'),
    api('/api/invoice-documents/stats')
  ]);
  const estimates = docs.filter(d => (d.docType || '').toUpperCase() === 'ESTIMATE');
  const invoices = docs.filter(d => (d.docType || '').toUpperCase() === 'INVOICE');
  const focusLabel = typeFocus === 'ESTIMATE' ? 'Estimates' : typeFocus === 'INVOICE' ? 'Invoices' : 'Invoices & Estimates';
  const focusDocs = typeFocus ? docs.filter(d => d.docType === typeFocus) : docs;
  el.innerHTML = `
    <section class="workspace-hero">
      <div>
        <span class="eyebrow">Unified ${focusLabel}</span>
        <h2>Create the customer document here, then the ledger updates itself.</h2>
        <p>Estimates, invoices, calculator math, records, and PDF output all save into the same local SQLite database. No separate invoice app and no duplicate entry.</p>
      </div>
      <div class="hero-actions">
        <button class="ghost-button dark" onclick="showPage('pricingCalculator')">Open Calculator</button>
        <button class="ghost-button dark" id="legacyImportBtn">Import Old App Once</button>
        <button class="ghost-button dark" id="newEstimateBtn">New Estimate</button>
        <button class="primary-button" id="newInvoiceBtn">New Invoice</button>
      </div>
    </section>
    <section class="entity-strip">
      <div><span>Estimates</span><strong>${stats.totalEstimates ?? estimates.length}</strong></div>
      <div><span>Invoices</span><strong>${stats.totalInvoices ?? invoices.length}</strong></div>
      <div><span>Unpaid Balance</span><strong>${formatMoney(stats.unpaidBalance ?? invoices.reduce((s, d) => s + Number(d.balance || 0), 0))}</strong></div>
      <div><span>Paid Revenue</span><strong>${formatMoney(stats.paidRevenue || 0)}</strong></div>
    </section>
    <div class="grid three">
      <button class="action-tile" onclick="openInvoiceEditor(null, 'ESTIMATE')">
        <span>1</span><strong>New Estimate</strong><small>Quote work before approval. Creates a quoted Customer Job.</small>
      </button>
      <button class="action-tile" onclick="showPage('pricingCalculator')">
        <span>2</span><strong>Calculator</strong><small>Use grams, print hours, design time, fees, discount, and tax to build a line item.</small>
      </button>
      <button class="action-tile" onclick="openInvoiceEditor(null, 'INVOICE')">
        <span>3</span><strong>New Invoice</strong><small>Bill approved work. Creates AR and becomes a Sale when paid.</small>
      </button>
    </div>
    <div class="grid two">
      <div class="card">
        <div class="card-header-lite"><h3>What happens when you save</h3><span class="badge">One database</span></div>
        <div class="workflow-list">
          <div class="workflow-row"><div><span>Estimate</span><strong>Customer Job</strong></div><p>Saving an estimate creates/updates the quoted job. It is not income.</p></div>
          <div class="workflow-row"><div><span>Invoice</span><strong>AR Invoice</strong></div><p>Saving an invoice creates/updates AR. It is money owed until paid.</p></div>
          <div class="workflow-row"><div><span>Paid invoice</span><strong>Sale</strong></div><p>Enter Amount Paid and save. The app creates/updates the matching Direct Sale automatically.</p></div>
        </div>
      </div>
      <div class="card">
        <div class="card-header-lite"><h3>What To Do First</h3><span class="badge">Process</span></div>
        <div class="help-list">
          <div class="help-item"><strong>Quote first</strong><p>Create an estimate. Send/print it. The job appears in Customer Jobs as Quoted.</p></div>
          <div class="help-item"><strong>Approved</strong><p>Duplicate the estimate or switch it to Invoice, then save. The invoice appears in AR.</p></div>
          <div class="help-item"><strong>Paid</strong><p>Set Amount Paid and status Paid. The matching Direct Sale is created or updated automatically for cash reporting.</p></div>
        </div>
      </div>
    </div>
    <div class="card">
      <div class="table-tools">
        <input class="search" id="invoiceSearch" placeholder="Search estimates, invoices, customers, projects...">
        <select id="invoiceTypeFilter" class="search compact">
          <option value="">All documents</option>
          <option value="ESTIMATE" ${typeFocus === 'ESTIMATE' ? 'selected' : ''}>Estimates</option>
          <option value="INVOICE" ${typeFocus === 'INVOICE' ? 'selected' : ''}>Invoices</option>
        </select>
        <span class="badge">${focusDocs.length} shown</span>
      </div>
      <div id="invoiceDocsArea">${invoiceDocsTable(focusDocs)}</div>
    </div>`;
  qs('#newEstimateBtn').onclick = () => openInvoiceEditor(null, 'ESTIMATE');
  qs('#newInvoiceBtn').onclick = () => openInvoiceEditor(null, 'INVOICE');
  qs('#legacyImportBtn').onclick = importLegacyInvoicesOnce;
  qs('#invoiceSearch').oninput = () => filterInvoiceDocs(docs);
  qs('#invoiceTypeFilter').onchange = () => filterInvoiceDocs(docs);
  bindInvoiceDocButtons();
}

function invoiceDocsTable(docs) {
  if (!docs.length) return emptyState('No estimates or invoices yet.', 'Create one here, or use Import Old App Once to pull existing documents into this unified database.');
  return `<div class="table-wrap"><table><thead><tr><th>Number</th><th>Type</th><th>Status</th><th>Customer</th><th>Project</th><th>Total</th><th>Balance</th><th>Date</th><th></th></tr></thead><tbody>${docs.map(d => `
    <tr>
      <td><strong>${escapeHtml(d.docNumber)}</strong></td>
      <td>${badgeFor(d.docType)}</td>
      <td>${badgeFor(d.status)}</td>
      <td>${escapeHtml(d.customerName || '')}</td>
      <td>${escapeHtml(d.projectName || '')}</td>
      <td>${formatMoney(d.total)}</td>
      <td>${formatMoney(d.balance)}</td>
      <td>${escapeHtml(d.docDate || '')}</td>
      <td><button class="ghost-button compact-action" data-invoice-edit="${d.id}">Open</button></td>
    </tr>`).join('')}</tbody></table></div>`;
}

function filterInvoiceDocs(docs) {
  const term = (qs('#invoiceSearch')?.value || '').toLowerCase();
  const type = qs('#invoiceTypeFilter')?.value || '';
  const filtered = docs.filter(d => {
    const blob = JSON.stringify(d).toLowerCase();
    return (!type || d.docType === type) && (!term || blob.includes(term));
  });
  qs('#invoiceDocsArea').innerHTML = invoiceDocsTable(filtered);
  bindInvoiceDocButtons();
}

function bindInvoiceDocButtons() {
  qsa('[data-invoice-edit]').forEach(btn => btn.onclick = () => openInvoiceEditor(Number(btn.dataset.invoiceEdit)));
}

function renderPricingCalculator(el) {
  el.innerHTML = `
    <div class="page-head">
      <div>
        <h2>Estimate Calculator</h2>
        <p>Use this before or during an estimate. Push the result into a new estimate or an open invoice/estimate editor.</p>
      </div>
      <div class="actions">
        <button class="ghost-button" onclick="showPage('estimates')">Estimates</button>
        <button class="primary-button" id="calcNewEstimateBtn">New Estimate From Calculator</button>
      </div>
    </div>
    <div class="grid two">
      <form id="pricingCalcForm" class="card invoice-form">
        ${invoiceInput('calcDescription','Line Description','Custom 3D print service')}
        ${invoiceInput('calcDetails','Details','Material, color, size, or customer request')}
        ${invoiceInput('calcGrams','Filament Grams',0,'number')}
        ${invoiceInput('calcHours','Print Hours',0,'number')}
        ${invoiceInput('calcDesignHours','Design Hours',0,'number')}
        ${invoiceInput('calcSetupFee','Setup Fee',0,'number')}
        ${invoiceInput('calcPostFee','Post Processing',0,'number')}
        ${invoiceInput('calcGramRate','Rate / Gram',0.05,'number')}
        ${invoiceInput('calcHourRate','Machine Rate / Hr',3,'number')}
        ${invoiceInput('calcDesignRate','Design Rate / Hr',25,'number')}
        ${invoiceInput('calcDifficulty','Difficulty Multiplier',1,'number')}
        ${invoiceInput('calcRush','Rush %',0,'number')}
        ${invoiceInput('calcDiscount','Discount',0,'number')}
        ${invoiceInput('calcTaxRate','Tax %',0,'number')}
        ${invoiceInput('calcMinimum','Minimum Charge',15,'number')}
      </form>
      <div class="card calc-result-card">
        <div class="card-header-lite"><h3>Calculated Quote</h3><span class="badge">Ready for estimate</span></div>
        <div class="calc-total" id="pricingCalcTotal">$0.00</div>
        <div id="pricingCalcBreakdown" class="workflow-list"></div>
        <div class="actions">
          <button class="ghost-button" id="calcCopyLineBtn">Copy Line Text</button>
          <button class="primary-button" id="calcPushEstimateBtn">Create Estimate</button>
        </div>
      </div>
    </div>`;

  qs('#pricingCalcForm').oninput = updatePricingCalculator;
  qs('#calcNewEstimateBtn').onclick = () => createEstimateFromCalculator();
  qs('#calcPushEstimateBtn').onclick = () => createEstimateFromCalculator();
  qs('#calcCopyLineBtn').onclick = async () => {
    const calc = collectPricingCalculator();
    await navigator.clipboard?.writeText(`${calc.description} - ${formatMoney(calc.total)}`);
    toast('Calculator line copied.');
  };
  updatePricingCalculator();
}

function collectPricingCalculator() {
  const form = qs('#pricingCalcForm');
  const data = Object.fromEntries(new FormData(form).entries());
  const grams = nonNegative(data.calcGrams);
  const hours = nonNegative(data.calcHours);
  const designHours = nonNegative(data.calcDesignHours);
  const setupFee = nonNegative(data.calcSetupFee);
  const postFee = nonNegative(data.calcPostFee);
  const gramRate = nonNegative(data.calcGramRate, 0.05);
  const hourRate = nonNegative(data.calcHourRate, 3);
  const designRate = nonNegative(data.calcDesignRate, 25);
  const minimum = nonNegative(data.calcMinimum, 15);
  const difficulty = Math.max(1, nonNegative(data.calcDifficulty, 1));
  const rush = nonNegative(data.calcRush);
  const discount = nonNegative(data.calcDiscount);
  const taxRate = Math.min(30, nonNegative(data.calcTaxRate));
  const material = grams * gramRate;
  const machine = hours * hourRate;
  const design = designHours * designRate;
  const baseSubtotal = material + machine + design + setupFee + postFee;
  const difficultyFee = baseSubtotal * Math.max(0, difficulty - 1);
  const afterDifficulty = baseSubtotal + difficultyFee;
  const rushAmount = afterDifficulty * rush / 100;
  const taxable = Math.max(minimum, afterDifficulty + rushAmount - discount);
  const taxAmount = taxable * taxRate / 100;
  return {
    ...data,
    grams, hours, designHours, setupFee, postFee, gramRate, hourRate, designRate, minimum, difficulty, rush, discount, taxRate,
    material, machine, design, base: afterDifficulty, difficultyFee, rushAmount, subtotal: taxable, taxAmount, total: taxable + taxAmount,
    description: data.calcDescription || 'Custom 3D print service',
    details: data.calcDetails || ''
  };
}

function updatePricingCalculator() {
  const calc = collectPricingCalculator();
  qs('#pricingCalcTotal').textContent = formatMoney(calc.total);
  qs('#pricingCalcBreakdown').innerHTML = [
    ['Material', `${calc.grams}g x ${formatMoney(calc.gramRate)}`, calc.material],
    ['Machine time', `${calc.hours} hr x ${formatMoney(calc.hourRate)}`, calc.machine],
    ['Design time', `${calc.designHours} hr x ${formatMoney(calc.designRate)}`, calc.design],
    ['Base after minimum/difficulty', `Minimum ${formatMoney(calc.minimum)} · difficulty ${calc.difficulty}`, calc.base],
    ['Rush / discount / tax', `${calc.rush}% rush, ${formatMoney(calc.discount)} discount, ${calc.taxRate}% tax`, calc.rushAmount - calc.discount + calc.taxAmount]
  ].map(([label, note, value]) => `<div class="workflow-row"><div><span>${escapeHtml(label)}</span><strong>${formatMoney(value)}</strong></div><p>${escapeHtml(note)}</p></div>`).join('');
}

async function createEstimateFromCalculator() {
  const calc = collectPricingCalculator();
  const doc = await newInvoiceDocument('ESTIMATE');
  doc.calcGrams = calc.grams;
  doc.calcHours = calc.hours;
  doc.calcDesignHours = calc.designHours;
  doc.calcSetupFee = calc.setupFee;
  doc.calcPostFee = calc.postFee;
  doc.calcGramRate = calc.gramRate;
  doc.calcHourRate = calc.hourRate;
  doc.calcDesignRate = calc.designRate;
  doc.calcMinimum = calc.minimum;
  doc.calcDifficulty = calc.difficulty;
  doc.calcRush = calc.rush;
  doc.calcDiscount = calc.discount;
  doc.calcTaxRate = calc.taxRate;
  doc.discountAmount = calc.discount;
  doc.rushAmount = calc.rushAmount;
  doc.taxAmount = calc.taxAmount;
  doc.subtotal = calc.base;
  doc.total = calc.total;
  doc.amountPaid = 0;
  doc.balance = 0;
  doc.lineItems = [{
    sortOrder: 1,
    description: calc.description,
    details: calc.details,
    quantity: 1,
    rate: calc.base,
    amount: calc.base
  }];
  renderInvoiceEditor(doc);
}

async function importLegacyInvoicesOnce() {
  try {
    const result = await api('/api/invoice-documents/import-from-legacy', { method: 'POST', body: '{}' });
    toast(`Imported ${result.imported} old document(s).`);
    await showPage('invoiceCenter');
  } catch (err) {
    toast(`Import failed: ${err.message}`);
  }
}

async function openInvoiceEditor(id = null, requestedType = 'ESTIMATE') {
  const doc = id ? await api(`/api/invoice-documents/${id}`) : await newInvoiceDocument(requestedType);
  renderInvoiceEditor(doc);
}

async function newInvoiceDocument(type) {
  const next = await api(`/api/invoice-documents/next-number?type=${encodeURIComponent(type)}`);
  const today = new Date().toISOString().substring(0, 10);
  const due = new Date(Date.now() + (type === 'INVOICE' ? 7 : 14) * 86400000).toISOString().substring(0, 10);
  return {
    id: null, docNumber: next.number, docType: type, status: 'Draft',
    docDate: today, dueDate: due, pageSize: 'Letter',
    customerName: '', customerPhone: '', customerEmail: '', customerAddress: '',
    projectName: '', material: '', color: '', infill: '', projectDescription: '', projectNotes: '',
    subtotal: 0, discountAmount: 0, rushAmount: 0, taxAmount: 0, total: 0, amountPaid: 0, balance: 0,
    calcTaxRate: 0, termsNotes: type === 'INVOICE' ? 'Payment due by the due date shown above.' : 'Estimate is valid for 14 days unless otherwise noted.',
    pricingGuide: '', standardTurnaround: '', rushTurnaround: '',
    lineItems: [{ sortOrder: 1, description: '', details: '', quantity: 1, rate: 0, amount: 0 }]
  };
}

function renderInvoiceEditor(doc) {
  const el = qs('#app');
  el.innerHTML = `
    <div class="page-head">
      <div>
        <h2>${doc.id ? 'Edit' : 'New'} ${doc.docType === 'INVOICE' ? 'Invoice' : 'Estimate'}</h2>
        <p>Save here once. The ledger updates Jobs or AR automatically from this same database.</p>
      </div>
      <div class="actions">
        <button class="ghost-button" onclick="showPage('invoiceCenter')">Back</button>
        ${doc.id ? `<button class="ghost-button" id="duplicateInvoiceDocBtn">Duplicate</button>${doc.docType === 'ESTIMATE' ? `<button class="ghost-button" id="convertInvoiceDocBtn">Convert to Invoice</button>` : ''}<button class="ghost-button" id="deleteInvoiceDocBtn">Delete</button>` : ''}
        <button class="ghost-button" id="previewInvoiceDocBtn">Open PDF Preview</button>
        <button class="ghost-button" id="printInvoiceDocBtn">Print / Save PDF</button>
        <button class="primary-button" id="saveInvoiceDocBtn">Save</button>
      </div>
    </div>
    <div class="invoice-builder">
      <form id="invoiceDocForm" class="invoice-form card">
        ${invoiceInput('docNumber','Number',doc.docNumber)}
        ${invoiceSelect('docType','Type',doc.docType,['ESTIMATE','INVOICE'])}
        ${invoiceSelect('status','Status',doc.status, doc.docType === 'INVOICE' ? ['Draft','Sent','Partial','Paid','Void'] : ['Draft','Sent','Accepted','Void'])}
        ${invoiceInput('docDate','Date',doc.docDate,'date')}
        ${invoiceInput('dueDate','Due Date',doc.dueDate,'date')}
        ${invoiceInput('customerName','Customer',doc.customerName)}
        ${invoiceInput('customerEmail','Email',doc.customerEmail,'email')}
        ${invoiceInput('customerPhone','Phone',doc.customerPhone)}
        ${invoiceTextarea('customerAddress','Address',doc.customerAddress)}
        ${invoiceInput('projectName','Project',doc.projectName)}
        ${invoiceInput('material','Material',doc.material)}
        ${invoiceInput('color','Color',doc.color)}
        ${invoiceInput('infill','Infill',doc.infill)}
        ${invoiceTextarea('projectDescription','Description',doc.projectDescription)}
        ${invoiceTextarea('projectNotes','Internal Notes',doc.projectNotes)}
      </form>
      <div class="card">
        <div class="card-header-lite"><h3>Line Items</h3><button class="ghost-button compact-action" id="addInvoiceLineBtn">Add Line</button></div>
        <div id="invoiceLineItems"></div>
        <div class="invoice-totals">
          ${invoiceInput('discountAmount','Discount',doc.discountAmount,'number')}
          ${invoiceInput('rushAmount','Rush Fee',doc.rushAmount,'number')}
          ${invoiceInput('calcTaxRate','Tax %',doc.calcTaxRate,'number')}
          ${invoiceInput('amountPaid','Amount Paid',doc.amountPaid,'number')}
        </div>
        <section class="entity-strip invoice-total-strip">
          <div><span>Subtotal</span><strong id="invoiceSubtotal">$0.00</strong></div>
          <div><span>Tax</span><strong id="invoiceTax">$0.00</strong></div>
          <div><span>Total</span><strong id="invoiceTotal">$0.00</strong></div>
          <div><span>Balance</span><strong id="invoiceBalance">$0.00</strong></div>
        </section>
      </div>
      <div class="card">
        <div class="card-header-lite"><h3>Pricing Calculator</h3><span class="badge">Built in</span></div>
        <div class="invoice-form compact-calc">
          ${invoiceInput('calcGrams','Grams',doc.calcGrams,'number')}
          ${invoiceInput('calcHours','Print Hours',doc.calcHours,'number')}
          ${invoiceInput('calcDesignHours','Design Hours',doc.calcDesignHours,'number')}
          ${invoiceInput('calcSetupFee','Setup Fee',doc.calcSetupFee,'number')}
          ${invoiceInput('calcPostFee','Post Processing',doc.calcPostFee,'number')}
          ${invoiceInput('calcGramRate','Rate / Gram',doc.calcGramRate ?? 0.05,'number')}
          ${invoiceInput('calcHourRate','Machine Rate / Hr',doc.calcHourRate ?? 3,'number')}
          ${invoiceInput('calcDesignRate','Design Rate / Hr',doc.calcDesignRate ?? 25,'number')}
          ${invoiceInput('calcMinimum','Minimum',doc.calcMinimum ?? 15,'number')}
          ${invoiceInput('calcDifficulty','Difficulty',doc.calcDifficulty ?? 1,'number')}
        </div>
        <div class="actions">
          <strong id="inlineCalcTotal">$0.00</strong>
          <button class="primary-button" id="pushInlineCalcBtn">Push Calculator To Line</button>
        </div>
      </div>
      <div class="card invoice-preview-card">
        <div class="card-header-lite"><h3>PDF Preview</h3><span class="badge">${escapeHtml(doc.docNumber || '')}</span></div>
        <iframe id="invoicePreviewFrame" class="invoice-preview-frame" title="PDF preview"></iframe>
      </div>
    </div>`;

  qs('#invoiceDocForm').dataset.id = doc.id || '';
  qs('#invoiceDocForm')._lineItems = (doc.lineItems || []).map((line, i) => ({ ...line, sortOrder: line.sortOrder || i + 1 }));
  renderInvoiceLines();
  bindInvoiceEditor();
  updateInvoicePreview();
}

function invoiceInput(name, label, value = '', type = 'text') {
  return `<label><span>${label}</span><input name="${name}" type="${type}" value="${escapeHtml(value ?? '')}"></label>`;
}
function invoiceTextarea(name, label, value = '') {
  return `<label class="full"><span>${label}</span><textarea name="${name}" rows="3">${escapeHtml(value ?? '')}</textarea></label>`;
}
function invoiceSelect(name, label, value, options) {
  return `<label><span>${label}</span><select name="${name}">${options.map(o => `<option value="${o}" ${o === value ? 'selected' : ''}>${o}</option>`).join('')}</select></label>`;
}

function renderInvoiceLines() {
  const form = qs('#invoiceDocForm');
  const lines = form._lineItems?.length ? form._lineItems : [{ sortOrder: 1, quantity: 1, rate: 0, amount: 0 }];
  form._lineItems = lines;
  qs('#invoiceLineItems').innerHTML = lines.map((line, index) => `
    <div class="invoice-line" data-line="${index}">
      <input data-line-field="description" placeholder="Description" value="${escapeHtml(line.description || '')}">
      <input data-line-field="details" placeholder="Details" value="${escapeHtml(line.details || '')}">
      <input data-line-field="quantity" type="number" min="0" step="0.01" value="${Number(line.quantity ?? 1)}">
      <input data-line-field="rate" type="number" min="0" step="0.01" value="${Number(line.rate ?? 0)}">
      <strong>${formatMoney(line.amount)}</strong>
      <button class="icon-button" type="button" data-remove-line="${index}" title="Remove line">×</button>
    </div>`).join('');
  qsa('[data-line-field]').forEach(input => input.oninput = handleInvoiceLineChange);
  qsa('[data-remove-line]').forEach(btn => btn.onclick = () => {
    form._lineItems.splice(Number(btn.dataset.removeLine), 1);
    renderInvoiceLines();
    updateInvoicePreview();
  });
}

function handleInvoiceLineChange(event) {
  const row = event.target.closest('.invoice-line');
  const index = Number(row.dataset.line);
  const field = event.target.dataset.lineField;
  const form = qs('#invoiceDocForm');
  const line = form._lineItems[index];
  line[field] = ['quantity','rate'].includes(field) ? nonNegative(event.target.value) : event.target.value;
  line.amount = nonNegative(line.quantity) * nonNegative(line.rate);
  renderInvoiceLines();
  updateInvoicePreview();
}

function bindInvoiceEditor() {
  qs('#invoiceDocForm').oninput = updateInvoicePreview;
  qsa('.invoice-totals input').forEach(input => input.oninput = updateInvoicePreview);
  qs('#addInvoiceLineBtn').onclick = () => {
    const form = qs('#invoiceDocForm');
    form._lineItems.push({ sortOrder: form._lineItems.length + 1, description: '', details: '', quantity: 1, rate: 0, amount: 0 });
    renderInvoiceLines();
  };
  qs('#saveInvoiceDocBtn').onclick = saveInvoiceDocument;
  qs('#previewInvoiceDocBtn').onclick = () => printInvoiceDocument(true);
  qs('#printInvoiceDocBtn').onclick = () => printInvoiceDocument(false);
  qsa('.compact-calc input').forEach(input => input.oninput = updateInvoicePreview);
  qs('#pushInlineCalcBtn').onclick = pushInlineCalculatorToLine;
  const duplicate = qs('#duplicateInvoiceDocBtn');
  if (duplicate) duplicate.onclick = duplicateInvoiceDocument;
  const convert = qs('#convertInvoiceDocBtn');
  if (convert) convert.onclick = convertEstimateToInvoice;
  const del = qs('#deleteInvoiceDocBtn');
  if (del) del.onclick = deleteInvoiceDocument;
}

function collectInvoiceDocument() {
  const form = qs('#invoiceDocForm');
  const data = Object.fromEntries(new FormData(form).entries());
  qsa('.invoice-totals input').forEach(input => data[input.name] = input.value);
  qsa('.compact-calc input').forEach(input => data[input.name] = input.value);
  const lines = (form._lineItems || []).map((line, index) => ({
    id: line.id || null,
    sortOrder: index + 1,
    description: line.description || '',
    details: line.details || '',
    quantity: nonNegative(line.quantity),
    rate: nonNegative(line.rate),
    amount: nonNegative(line.quantity) * nonNegative(line.rate)
  }));
  const subtotal = lines.reduce((sum, line) => sum + line.amount, 0);
  const discountAmount = nonNegative(data.discountAmount);
  const rushAmount = nonNegative(data.rushAmount);
  const calcTaxRate = Math.min(30, nonNegative(data.calcTaxRate));
  const taxable = Math.max(0, subtotal - discountAmount + rushAmount);
  const taxAmount = taxable * calcTaxRate / 100;
  const total = taxable + taxAmount;
  const isInvoice = data.docType === 'INVOICE';
  const status = isInvoice && data.status === 'Paid' ? 'Paid' : data.status;
  const amountPaid = isInvoice ? Math.min(total, status === 'Paid' ? total : nonNegative(data.amountPaid)) : 0;
  return {
    ...data,
    status: !isInvoice && data.status === 'Paid' ? 'Accepted' : status,
    subtotal, discountAmount, rushAmount, calcTaxRate, taxAmount, total, amountPaid,
    balance: isInvoice ? Math.max(0, total - amountPaid) : 0,
    calcGrams: nonNegative(data.calcGrams), calcHours: nonNegative(data.calcHours),
    calcDesignHours: nonNegative(data.calcDesignHours), calcSetupFee: nonNegative(data.calcSetupFee),
    calcPostFee: nonNegative(data.calcPostFee), calcGramRate: nonNegative(data.calcGramRate, 0.05),
    calcHourRate: nonNegative(data.calcHourRate, 3), calcDesignRate: nonNegative(data.calcDesignRate, 25),
    calcMinimum: nonNegative(data.calcMinimum, 15), calcDifficulty: Math.max(1, nonNegative(data.calcDifficulty, 1)),
    calcRush: nonNegative(data.calcRush), calcDiscount: nonNegative(data.calcDiscount),
    pricingGuide: '', termsNotes: data.docType === 'INVOICE' ? 'Payment due by the due date shown above.' : 'Estimate is valid for 14 days unless otherwise noted.',
    standardTurnaround: '', rushTurnaround: '', pageSize: 'Letter',
    lineItems: lines,
    json: '{}'
  };
}

function updateInvoicePreview() {
  const doc = collectInvoiceDocument();
  qs('#invoiceSubtotal').textContent = formatMoney(doc.subtotal);
  qs('#invoiceTax').textContent = formatMoney(doc.taxAmount);
  qs('#invoiceTotal').textContent = formatMoney(doc.total);
  qs('#invoiceBalance').textContent = formatMoney(doc.balance);
  const inlineCalcTotal = qs('#inlineCalcTotal');
  if (inlineCalcTotal) inlineCalcTotal.textContent = `Calculator: ${formatMoney(calculateInlineInvoicePrice(doc).lineAmount)}`;
  refreshPdfPreview(doc);
}

async function loadInvoicePdfModule() {
  appState.invoicePdfModule ||= import('/invoice-builder/js/pdf.js');
  return appState.invoicePdfModule;
}

async function refreshPdfPreview(doc) {
  const frame = qs('#invoicePreviewFrame');
  if (!frame) return;
  try {
    const { renderInvoiceHtml } = await loadInvoicePdfModule();
    frame.srcdoc = renderInvoiceHtml(doc);
  } catch {
    frame.srcdoc = `<!doctype html><html><body>${invoicePreviewHtml(doc)}</body></html>`;
  }
}

function calculateInlineInvoicePrice(doc) {
  const material = Number(doc.calcGrams || 0) * Number(doc.calcGramRate || 0.05);
  const machine = Number(doc.calcHours || 0) * Number(doc.calcHourRate || 3);
  const design = Number(doc.calcDesignHours || 0) * Number(doc.calcDesignRate || 25);
  const fees = Number(doc.calcSetupFee || 0) + Number(doc.calcPostFee || 0);
  const difficulty = Math.max(0, Number(doc.calcDifficulty || 1));
  const minimum = Number(doc.calcMinimum || 15);
  const lineAmount = Math.max(minimum, (material + machine + design + fees) * difficulty);
  return { material, machine, design, fees, lineAmount };
}

function pushInlineCalculatorToLine() {
  const doc = collectInvoiceDocument();
  const calc = calculateInlineInvoicePrice(doc);
  const form = qs('#invoiceDocForm');
  const line = {
    sortOrder: (form._lineItems?.length || 0) + 1,
    description: doc.projectName || 'Custom 3D print service',
    details: `${doc.calcGrams || 0}g filament, ${doc.calcHours || 0} print hr, ${doc.calcDesignHours || 0} design hr`,
    quantity: 1,
    rate: calc.lineAmount,
    amount: calc.lineAmount
  };
  if (!form._lineItems?.length || !form._lineItems[0].description) form._lineItems = [line];
  else form._lineItems.push(line);
  renderInvoiceLines();
  updateInvoicePreview();
  toast('Calculator line added to this document.');
}

function invoicePreviewHtml(doc) {
  return `<article class="invoice-paper">
    <header><div><strong>EPATA 3D Prints</strong><span>Custom 3D printing, design, and small batch work</span></div><h2>${doc.docType === 'INVOICE' ? 'Invoice' : 'Estimate'}</h2></header>
    <div class="invoice-meta">
      <div><span>Number</span><strong>${escapeHtml(doc.docNumber)}</strong></div>
      <div><span>Date</span><strong>${escapeHtml(doc.docDate || '')}</strong></div>
      <div><span>Due</span><strong>${escapeHtml(doc.dueDate || '')}</strong></div>
      <div><span>Status</span><strong>${escapeHtml(doc.status || '')}</strong></div>
    </div>
    <section class="invoice-address"><div><span>Bill To</span><strong>${escapeHtml(doc.customerName || '')}</strong><p>${escapeHtml(doc.customerEmail || '')}<br>${escapeHtml(doc.customerPhone || '')}<br>${escapeHtml(doc.customerAddress || '')}</p></div><div><span>Project</span><strong>${escapeHtml(doc.projectName || '')}</strong><p>${escapeHtml(doc.projectDescription || '')}</p></div></section>
    <table><thead><tr><th>Description</th><th>Details</th><th>Qty</th><th>Rate</th><th>Amount</th></tr></thead><tbody>${doc.lineItems.map(line => `<tr><td>${escapeHtml(line.description)}</td><td>${escapeHtml(line.details)}</td><td>${line.quantity}</td><td>${formatMoney(line.rate)}</td><td>${formatMoney(line.amount)}</td></tr>`).join('')}</tbody></table>
    <section class="invoice-summary"><div></div><div><p><span>Subtotal</span><strong>${formatMoney(doc.subtotal)}</strong></p><p><span>Discount</span><strong>${formatMoney(doc.discountAmount)}</strong></p><p><span>Rush</span><strong>${formatMoney(doc.rushAmount)}</strong></p><p><span>Tax</span><strong>${formatMoney(doc.taxAmount)}</strong></p><p class="grand"><span>Total</span><strong>${formatMoney(doc.total)}</strong></p><p><span>Paid</span><strong>${formatMoney(doc.amountPaid)}</strong></p><p><span>Balance</span><strong>${formatMoney(doc.balance)}</strong></p></div></section>
    <footer>${escapeHtml(doc.termsNotes || '')}</footer>
  </article>`;
}

async function saveInvoiceDocument() {
  const form = qs('#invoiceDocForm');
  const id = form.dataset.id;
  const doc = collectInvoiceDocument();
  const saved = await api(id ? `/api/invoice-documents/${id}` : '/api/invoice-documents', {
    method: id ? 'PUT' : 'POST',
    body: JSON.stringify(doc)
  });
  toast(`${saved.docType === 'INVOICE' ? 'Invoice' : 'Estimate'} saved.`);
  renderInvoiceEditor(saved);
}

async function duplicateInvoiceDocument() {
  const id = qs('#invoiceDocForm').dataset.id;
  if (!id) return;
  const copy = await api(`/api/invoice-documents/${id}/duplicate`, { method: 'POST', body: '{}' });
  toast('Document duplicated.');
  renderInvoiceEditor(copy);
}

async function convertEstimateToInvoice() {
  const id = qs('#invoiceDocForm').dataset.id;
  if (!id) return;
  const invoice = await api(`/api/invoice-documents/${id}/convert-to-invoice`, { method: 'POST', body: '{}' });
  toast('Estimate converted to invoice.');
  renderInvoiceEditor(invoice);
}

async function deleteInvoiceDocument() {
  const id = qs('#invoiceDocForm').dataset.id;
  if (!id || !confirm('Delete this estimate/invoice?')) return;
  await api(`/api/invoice-documents/${id}`, { method: 'DELETE' });
  toast('Document deleted.');
  await showPage('invoiceCenter');
}

async function printInvoiceDocument(preview = false) {
  const doc = collectInvoiceDocument();
  try {
    const { generatePdf } = await loadInvoicePdfModule();
    await generatePdf(doc, preview);
  } catch {
    const win = window.open('', '_blank');
    win.document.write(`<!doctype html><html><head><title>${escapeHtml(doc.docNumber)}</title><style>${invoicePrintCss()}</style></head><body>${invoicePreviewHtml(doc)}${preview ? '' : '<script>window.onload=()=>window.print();</script>'}</body></html>`);
    win.document.close();
  }
}

function invoicePrintCss() {
  return `body{font-family:Segoe UI,Arial,sans-serif;background:#f4f6f8;margin:0;padding:28px;color:#111827}.invoice-paper{max-width:850px;margin:auto;background:#fff;padding:34px;border:1px solid #d1d5db}.invoice-paper header{display:flex;justify-content:space-between;border-bottom:3px solid #2563eb;padding-bottom:16px}.invoice-paper header strong{font-size:24px}.invoice-paper header span,.invoice-paper span{display:block;color:#64748b;font-size:12px;text-transform:uppercase;font-weight:700}.invoice-paper h2{text-transform:uppercase}.invoice-meta,.invoice-address{display:grid;grid-template-columns:repeat(4,1fr);gap:12px;margin:20px 0}.invoice-address{grid-template-columns:1fr 1fr}.invoice-paper table{width:100%;border-collapse:collapse;margin:20px 0}.invoice-paper th{background:#eff6ff;text-align:left}.invoice-paper th,.invoice-paper td{border-bottom:1px solid #e5e7eb;padding:9px}.invoice-summary{display:grid;grid-template-columns:1fr 300px}.invoice-summary p{display:flex;justify-content:space-between;margin:0;padding:7px 0;border-bottom:1px solid #e5e7eb}.grand{font-size:20px}.invoice-paper footer{margin-top:28px;color:#64748b}@media print{body{background:#fff;padding:0}.invoice-paper{border:0;max-width:none}}`;
}

async function renderTaxPrep(el) {
  const [dashboard, sales, expenses, bills, docs, assets, rewards, audit] = await Promise.all([
    api('/api/dashboard'),
    api('/api/sales'),
    api('/api/expenses'),
    api('/api/bills'),
    api('/api/audit-documents'),
    api('/api/assets'),
    api('/api/makerworld-rewards'),
    api('/api/tax-audit')
  ]);
  const k = dashboard.kpis;
  const deductibleExpenses = expenses.filter(isTaxCountedExpense);
  const nonDeductibleExpenses = expenses.filter(x => !isTaxCountedExpense(x));
  const reviewExpenses = expenses.filter(x => equalsText(x.deductibleStatus, 'Review') || equalsText(x.taxBucket, 'Review') || x.needsReview);
  const assetExpenses = expenses.filter(x => equalsText(x.taxBucket, 'Asset'));
  const expensedAssets = assets.filter(isFullyExpensedAsset);
  const makerWorldIncome = sum(rewards.filter(x => equalsText(x.incomeStatus, 'Yes - Count as income')), x => x.giftCardAmount);
  const deductibleTotal = sum(deductibleExpenses, taxExpenseAmount) + sum(expensedAssets, taxAssetAmount);
  const openProofGaps = [
    ...sales.filter(x => x.needsReview || !x.sourceProof),
    ...expenses.filter(x => x.needsReview || !x.receiptProof),
    ...bills.filter(x => x.needsReview || !x.sourceProof)
  ];
  const expenseGroups = groupMoney(deductibleExpenses, 'category', taxExpenseAmount);
  const billGroups = groupMoney(bills.filter(x => x.taxDeductible !== false), 'category', x => x.total ?? x.amount);
  el.innerHTML = `
    <section class="workspace-hero">
      <div>
        <span class="eyebrow">Tax Prep</span>
        <h2>Export clean records before filing.</h2>
        <p>This page is a filing-prep checklist and export hub. It is not tax advice, but it helps you collect the numbers and proof your accountant or tax software will ask for.</p>
      </div>
      <div class="hero-actions">
        <a class="primary-button" href="/api/export/sales">Export Sales</a>
        <a class="ghost-button dark" href="/api/export/expenses">Export Expenses</a>
      </div>
    </section>
    <section class="kpi-grid">
      ${kpi('Gross Receipts', k.grossReceipts, 'Gross receipts from included sales.', 'good')}
      ${kpi('Deductible Expenses', deductibleTotal, 'Counted expenses plus assets marked expensed this year, adjusted for business-use percent.', deductibleTotal > 0 ? 'warn' : '')}
      ${kpi('Sales Tax Memo', k.salesTaxMemo, 'Sales tax shown on orders/invoices. Marketplace tax may be handled by the marketplace.', 'warn')}
      ${kpi('MakerWorld Income Review', makerWorldIncome, 'Rewards marked Yes - Count as income. Review before filing.', makerWorldIncome > 0 ? 'warn' : '', true)}
      ${kpi('Proof Gaps', openProofGaps.length, 'Rows missing proof or marked Needs Review.', openProofGaps.length ? 'bad' : 'good', false)}
    </section>
    <div class="grid two">
      <div class="card">
        <div class="card-header-lite"><h3>Filing Checklist</h3><span class="badge">Do before filing</span></div>
        <div class="tax-checklist">
          ${taxCheck('Export Sales CSV', 'Use for gross receipts, platform, tax memo, fees, shipping charged, COGS, and proof references.', '/api/export/sales')}
          ${taxCheck('Export Expenses CSV', 'Use for deductible business purchases: filament, shipping labels, packaging, software, tools, ads.', '/api/export/expenses')}
          ${taxCheck('Export Bills/AP CSV', 'Use if you track unpaid vendor obligations or accrual-style records.', '/api/export/bills')}
          ${taxCheck('Export Audit Docs CSV', 'Use as proof index: receipt PDFs, invoice PDFs, screenshots, and file paths.', '/api/export/audit-documents')}
          ${taxCheck('Download DB Backup', 'Keep a full SQLite backup before filing and after filing.', null, 'backupDb()')}
        </div>
      </div>
      <div class="card">
        <div class="card-header-lite"><h3>Review Before Filing</h3><span class="badge warn">${openProofGaps.length} gaps</span></div>
        <div class="help-list">
          <div class="help-item"><strong>Needs Review</strong><p>${dashboard.kpis.needsReviewCount} rows are marked Needs Review. Clear these or document why they are estimates.</p></div>
          <div class="help-item"><strong>Missing Proof</strong><p>${openProofGaps.length} sales/expenses/bills are missing proof or need review.</p></div>
          <div class="help-item"><strong>Excluded / Non-Deductible</strong><p>${nonDeductibleExpenses.length} expenses are excluded because they are non-deductible, memo-only, assets, unchecked, or review-only.</p></div>
          <div class="help-item"><strong>Expense Review</strong><p>${reviewExpenses.length} expenses need category or deductibility review before filing.</p></div>
          <div class="help-item"><strong>Asset Review</strong><p>${assetExpenses.length} expenses are marked Asset, and ${assets.filter(x => x.needsReview || equalsText(x.taxTreatment, 'Review') || equalsText(x.taxTreatment, 'Depreciation')).length} asset rows need tax-treatment or depreciation review.</p></div>
          <div class="help-item"><strong>Total Filing Prep Income</strong><p>${formatMoney(Number(k.grossReceipts || 0) + makerWorldIncome)} combines gross receipts and MakerWorld rewards marked as income. Sales-tax memo stays separate.</p></div>
          <div class="help-item"><strong>Uploaded Proof</strong><p>${docs.length} audit documents are indexed.</p></div>
        </div>
      </div>
    </div>
    <div class="grid two">
      <div class="card">
        <div class="card-header-lite"><h3>Deductible Expenses By Category</h3><span class="badge">${deductibleExpenses.length} rows</span></div>
        ${moneyGroupTable(expenseGroups)}
      </div>
      <div class="card">
        <div class="card-header-lite"><h3>AP/Bills By Category</h3><span class="badge">${bills.length} rows</span></div>
        ${moneyGroupTable(billGroups)}
      </div>
    </div>
    <div class="card">
      <div class="card-header-lite"><h3>Calculation Audit</h3><span class="badge ${audit.summary.criticalIssues || audit.summary.highIssues ? 'bad' : audit.summary.mediumIssues ? 'warn' : 'good'}">${audit.issues.length} findings</span></div>
      <p>This checks for mismatched totals, paid invoices without sales, unpaid invoices with sales, tax-classification gaps, and asset/reward review items.</p>
      ${audit.issues.length ? smallTable(audit.issues.slice(0, 12), ['severity','title','record','detail']) : emptyState('No calculation audit findings.', 'The current entered records pass the built-in consistency checks.')}
    </div>
    <div class="card">
      <div class="card-header-lite"><h3>Sales Tax Memo</h3><span class="badge warn">${formatMoney(k.salesTaxMemo)}</span></div>
      <p>Use this as a memo/checking number, not as automatic tax filing advice. Etsy and other marketplaces may collect/remit marketplace sales tax. Direct invoices may be different. Verify with your accountant or tax software.</p>
      ${smallTable(dashboard.monthly, ['month','grossReceipts','salesTaxMemo','estimatedCosts','estimatedNet','orders'])}
    </div>`;
}

function nonNegative(value, fallback = 0) {
  const n = Number(value);
  return Number.isFinite(n) ? Math.max(0, n) : fallback;
}

function sum(rows, pick) {
  return rows.reduce((total, row) => total + Number(pick(row) || 0), 0);
}

function equalsText(value, expected) {
  return String(value || '').trim().toLowerCase() === String(expected || '').trim().toLowerCase();
}

function isTaxCountedExpense(x) {
  if (x.countedExpense === false || x.taxDeductible === false) return false;
  if (!equalsText(x.deductibleStatus, 'Yes')) return false;
  return equalsText(x.taxBucket, 'Operating Expense') || equalsText(x.taxBucket, 'COGS/Materials');
}

function taxExpenseAmount(x) {
  const businessUse = Math.min(100, Math.max(0, Number(x.businessUsePercent ?? 100))) / 100;
  return Number(x.total ?? x.amount ?? 0) * businessUse;
}

function taxAssetAmount(x) {
  const businessUse = Math.min(100, Math.max(0, Number(x.businessUsePercent ?? 100))) / 100;
  return Number(x.cost ?? 0) * businessUse;
}

function isFullyExpensedAsset(x) {
  if (x.countedExpenseThisYear !== true) return false;
  return equalsText(x.taxTreatment, 'Section 179') || equalsText(x.taxTreatment, 'De Minimis Expense');
}
function groupMoney(rows, key, pick) {
  const map = new Map();
  rows.forEach(row => {
    const label = row[key] || 'Uncategorized';
    map.set(label, (map.get(label) || 0) + Number(pick(row) || 0));
  });
  return [...map.entries()].map(([label, total]) => ({ label, total })).sort((a, b) => b.total - a.total);
}
function moneyGroupTable(groups) {
  if (!groups.length) return emptyState('No category totals yet.', 'Add expenses or bills to build this summary.');
  return `<div class="table-wrap"><table><thead><tr><th>Category</th><th>Total</th></tr></thead><tbody>${groups.map(g => `<tr><td>${escapeHtml(g.label)}</td><td>${formatMoney(g.total)}</td></tr>`).join('')}</tbody></table></div>`;
}
function taxCheck(title, detail, href, onclick) {
  const action = href ? `<a class="ghost-button" href="${href}">Export</a>` : `<button class="ghost-button" onclick="${onclick}">Backup</button>`;
  return `<div class="tax-check"><div><strong>${escapeHtml(title)}</strong><p>${escapeHtml(detail)}</p></div>${action}</div>`;
}

async function uploadDocuments() {
  const files = qs('#docFiles').files;
  const out = qs('#uploadResult');
  if (!files || files.length === 0) {
    toast('Choose a file first.');
    return;
  }

  const form = new FormData();
  [...files].forEach(file => form.append('files', file));
  form.append('relatedType', qs('#relatedType').value || '');
  form.append('relatedNumber', qs('#relatedNumber').value || '');
  out.className = 'upload-result';
  out.innerHTML = `<div class="empty-state"><div class="empty-icon">⇪</div><div class="empty-title">Uploading and indexing...</div><div class="empty-desc">Saving the file locally and creating an Audit Doc.</div></div>`;

  try {
    const response = await fetch('/api/documents/upload', { method: 'POST', body: form });
    if (!response.ok) throw new Error(await response.text());
    const result = await response.json();
    out.innerHTML = renderUploadResult(result);
    toast(`Indexed ${result.count} document${result.count === 1 ? '' : 's'}.`);
  } catch (err) {
    out.innerHTML = `<div class="help-item"><strong>Upload failed</strong><p>${escapeHtml(err.message)}</p></div>`;
    toast(`Upload failed: ${err.message}`);
  }
}

function renderUploadResult(result) {
  const docs = result.documents || [];
  return `
    <div class="help-item">
      <strong>Uploaded and indexed ${result.count} document${result.count === 1 ? '' : 's'}.</strong>
      <p>These are now Audit Docs. Next, either create the business record they prove, or edit the Audit Doc and add missing details.</p>
    </div>
    <div class="upload-next-grid">
      ${docs.map(doc => `
        <div class="upload-next-card">
          <span class="badge">${escapeHtml(doc.documentType || 'Proof')}</span>
          <strong>${escapeHtml(doc.fileName || 'Uploaded file')}</strong>
          <p>${escapeHtml(doc.filePathOrUrl || '')}</p>
          <div class="actions">
            <button class="ghost-button" onclick='openModal(configs.auditDocs, ${JSON.stringify(doc).replaceAll("'", "&#39;")})'>Edit Audit Doc</button>
            <button class="primary-button" onclick="showPage('quickAdd')">Create Related Record</button>
          </div>
        </div>`).join('')}
    </div>
    <div class="card">
      <h3>Which Related Record?</h3>
      <div class="workflow-list">
        <div class="workflow-row"><div><span>Customer paid you</span><strong>Quick Add → Sale</strong></div><p>Use Etsy Sale or Direct Paid Sale.</p></div>
        <div class="workflow-row"><div><span>You paid money</span><strong>Quick Add → Paid Expense</strong></div><p>Use this for receipts, supplies, shipping labels, software, tools, and paid purchases.</p></div>
        <div class="workflow-row"><div><span>Customer owes you</span><strong>Quick Add → Open Invoice / AR</strong></div><p>Use this for invoices sent but not paid yet.</p></div>
        <div class="workflow-row"><div><span>You owe vendor</span><strong>Quick Add → Bill / AP</strong></div><p>Use this for bills you have not paid yet.</p></div>
      </div>
    </div>`;
}

function renderImportExport(el) {
  el.innerHTML = `
    <div class="page-head"><div><h2>Import / Export / Backup</h2><p>Export CSVs, back up the SQLite database, and optionally migrate old invoice-app records into this unified app.</p></div></div>
    <div class="grid two">
      <div class="card">
        <h3>Old Invoice App Migration</h3>
        <p>The unified app now has its own estimate/invoice builder and database tables. Use this only if the old invoice app is running and you want to pull its saved documents into this database.</p>
        <div class="actions">
          <button class="primary-button" onclick="showPage('invoiceRecords')">Open Invoice Records</button>
          <button class="ghost-button" id="tryImportBtn">Check Old App API</button>
        </div>
        <pre id="importResult" class="card" style="white-space:pre-wrap; overflow:auto; max-height:360px; display:none;"></pre>
      </div>
      <div class="card">
        <h3>Exports</h3>
        <p>CSV exports open in Excel. Use them when you need a copy, tax backup, or accountant handoff.</p>
        <div class="actions">
          ${Object.entries(configs).map(([key,c]) => `<a class="ghost-button" href="/api/export/${c.route}">${c.nav || c.title}</a>`).join('')}
        </div>
      </div>
    </div>
    <div class="card"><h3>Backup</h3><p>The Backup DB button downloads a copy of the SQLite database. Do this before big edits, before Windows updates, and at least monthly.</p><button class="primary-button" onclick="backupDb()">Backup DB Now</button></div>`;
  qs('#tryImportBtn').onclick = tryInvoiceImport;
}

async function renderAdmin(el) {
  let info = null;
  try {
    info = await api('/api/app-info');
  } catch {
    info = null;
  }
  const dbPath = String(info?.dbPath || 'Unknown');
  const isOneDrive = dbPath.toLowerCase().includes('onedrive');
  const dbStatus = info ? (isOneDrive ? 'OneDrive path' : 'Local path') : 'Path unavailable';
  el.innerHTML = `
    <section class="workspace-hero">
      <div>
        <span class="eyebrow">Admin / Data Safety</span>
        <h2>Control the parts that affect trust.</h2>
        <p>Use this page to see the active database, back it up, and decide what should become editable instead of hard-coded.</p>
      </div>
      <div class="hero-actions">
        <button class="primary-button" onclick="backupDb()">Backup DB Now</button>
        <button class="ghost-button dark" onclick="showPage('importExport')">Open Exports</button>
      </div>
    </section>

    <div class="grid two">
      <div class="card">
        <div class="card-header-lite"><h3>Active Database</h3><span class="badge ${!info || isOneDrive ? 'warn' : 'good'}">${dbStatus}</span></div>
        <p><code>${escapeHtml(dbPath)}</code></p>
        <div class="help-list">
          <div class="help-item"><strong>Single database</strong><p>The ledger, estimates, invoices, jobs, products, proof, actions, and tax prep all use this SQLite database.</p></div>
          <div class="help-item"><strong>OneDrive caution</strong><p>An active SQLite file inside OneDrive can hit sync locks or conflict copies while the app is writing. The safer pattern is local active DB plus automatic backups copied to OneDrive.</p></div>
          <div class="help-item"><strong>Current safety move</strong><p>Use Backup DB before big edits. Close the app before manually copying or replacing the database.</p></div>
        </div>
      </div>
      <div class="card">
        <div class="card-header-lite"><h3>Good Admin Settings To Add</h3><span class="badge">Roadmap</span></div>
        <div class="help-list">
          <div class="help-item"><strong>Data location</strong><p>Choose active DB folder and backup folder from the app.</p></div>
          <div class="help-item"><strong>Tax buckets</strong><p>Edit expense categories, tax buckets, deductibility defaults, business-use defaults, and review rules.</p></div>
          <div class="help-item"><strong>Products/pricing</strong><p>Manage default products, materials, machine rates, packaging rates, and invoice dropdown options.</p></div>
          <div class="help-item"><strong>Document rules</strong><p>Map receipt filenames/vendors to Expense, Asset, Audit Doc, or Review automatically.</p></div>
        </div>
      </div>
    </div>

    <div class="card">
      <div class="card-header-lite"><h3>Careful Delete Policy</h3><span class="badge warn">Designed for audit history</span></div>
      <p>Delete should be available for drafts, accidental duplicates, and uploaded proof that is genuinely wrong. For tax/audit records, archive is safer than permanent removal because you can explain what changed later.</p>
      <div class="actions">
        <button class="ghost-button" onclick="showPage('actions')">Review Actions</button>
        <button class="ghost-button" onclick="showPage('auditDocs')">Review Audit Docs</button>
        <button class="ghost-button" onclick="showPage('products')">Review Products</button>
      </div>
    </div>`;
}

async function tryInvoiceImport() {
  const out = qs('#importResult');
  out.style.display = 'block';
  out.textContent = 'Checking the old invoice app endpoints...';
  try {
    const result = await api('/api/import/invoice-app', { method: 'POST', body: JSON.stringify({ baseUrl: 'http://localhost:5057/' }) });
    out.textContent = JSON.stringify(result, null, 2);
  } catch (err) {
    out.textContent = err.message;
  }
}

function renderHelp(el) {
  const scenarios = [
    ['Estimate needed', 'Estimates -> New Estimate', 'Create and save the estimate here. The ledger creates or updates a Customer Job with status Quoted. It does not count as income, AR, or a sale.'],
    ['Estimate sent outside this app', 'Quick Add -> Estimate Sent', 'Enter it as a Customer Job with status Quoted. It does not count as income, AR, or a sale.'],
    ['Etsy order', 'Quick Add → Etsy Sale', 'Enter once as a Sale. Attach the Etsy order PDF/receipt. Later fill in Etsy fees, label cost, packaging, and COGS on the same Sale row. You usually do not need a separate AR Invoice because Etsy already paid/processed the order.'],
    ['Direct customer wants a quote/job', 'Estimates or Customer Jobs', 'Use Estimates for customer-facing PDFs. Use Customer Jobs for internal tracking. When you save an invoice, AR is updated. When they pay, enter Amount Paid and the Sale is updated automatically.'],
    ['Direct invoice needed', 'Invoices -> New Invoice', 'Create and save the invoice here. The ledger creates or updates AR. Do not enter it as a Sale yet unless money was actually received.'],
    ['Direct invoice sent outside this app', 'Quick Add → Open Invoice / AR', 'Enter the invoice once as AR. Do not enter it as a Sale yet unless money was actually received. Attach the invoice PDF as proof.'],
    ['Direct invoice paid', 'Invoices or AR Invoice', 'In Invoices, enter Amount Paid and status Paid. The app updates AR and creates/updates the matching Direct Sale so cash income is counted.'],
    ['Customer already paid without open AR', 'Quick Add → Direct Paid Sale', 'Enter one Sale row. Add invoice/order number if available. Attach the proof file.'],
    ['Bought supplies and already paid', 'Quick Add → Paid Expense', 'Enter one Expense row. Attach receipt proof. No AP Bill is needed because you do not owe anything.'],
    ['Vendor invoice not paid yet', 'Quick Add → Bill / AP', 'Enter one AP Bill. When paid, mark paid and optionally create/confirm an Expense if you want paid-expense reporting.'],
    ['Only have a PDF/receipt right now', 'Document Intake first, then Quick Add if needed', 'Upload proof to create an Audit Doc. Then use Quick Add or Attach File on an existing row for the actual business event. Intake does not silently auto-post accounting records.']
  ];
  const tabs = [
    ['Dashboard', 'Shows totals, charts, open AR/AP, review count, and action items. It is a view, not where you usually enter data.'],
    ['Quick Add', 'Best starting point for new business events. Pick what happened and the app opens the right form.'],
    ['Estimates', 'Customer-facing quote builder with calculator, line items, live PDF preview, and Customer Job sync.'],
    ['Invoices', 'Customer-facing invoice builder with PDF preview. Saving updates AR; paid invoices update Direct Sales.'],
    ['Calculator', 'Pricing calculator from the invoice tool. Use it to build a quote before pushing to an estimate.'],
    ['Invoice Records', 'Saved estimates and invoices from the unified database.'],
    ['Workflow', 'Visual map plus step-by-step “where do I enter this?” page. Use it whenever you feel unsure.'],
    ['Document Intake', 'Upload proof files. It creates Audit Docs and stores files locally. It does not yet automatically decide accounting entries.'],
    ['Sales', 'Money that came in or sales/orders that should count toward revenue when included in dashboard.'],
    ['AR Invoices', 'Money customers owe you from direct invoices. This is not cash until paid.'],
    ['AP Bills', 'Money you owe vendors but have not paid yet.'],
    ['Expenses', 'Money already paid out for supplies, shipping, software, tools, etc.'],
    ['Customer Jobs', 'The project/work history. Use when tracking the work matters separately from payment.'],
    ['Audit Docs', 'Proof index: PDFs, receipts, screenshots, order files, and file paths.'],
    ['Actions', 'Cleanup list for missing fees, missing proof, follow-ups, and bookkeeping tasks.']
  ];
  el.innerHTML = `
    <section class="workspace-hero">
      <div>
        <span class="eyebrow">Start Here</span>
        <h2>Enter the business event once. Attach proof once. Review it from many places.</h2>
        <p>Use Estimates/Invoices for customer PDFs. Use Quick Add for simple sales, bills, expenses, and manually-created documents. Use Document Intake when the file comes first.</p>
      </div>
      <div class="hero-actions">
        <button class="ghost-button dark" onclick="showPage('estimates')">New Estimate</button>
        <button class="ghost-button dark" onclick="showPage('invoices')">New Invoice</button>
        <button class="primary-button" onclick="showPage('quickAdd')">Start Quick Add</button>
        <button class="ghost-button dark" onclick="showPage('workflowGuide')">Open Workflow Guide</button>
      </div>
    </section>

    <div class="grid two">
      <div class="card">
        <h3>Start Here: What Button Do I Click?</h3>
        <div class="help-steps">
          <div><span>1</span><p><b>If money came in:</b> use Quick Add → Etsy Sale or Direct Paid Sale.</p></div>
          <div><span>2</span><p><b>If you need an estimate PDF:</b> use Estimates. It creates a quoted Customer Job and does not count as income.</p></div>
          <div><span>3</span><p><b>If you need an invoice PDF:</b> use Invoices. It creates AR until paid, then updates Sales.</p></div>
          <div><span>4</span><p><b>If you sent an estimate manually and are waiting for approval:</b> use Quick Add → Estimate Sent.</p></div>
          <div><span>5</span><p><b>If you sent an invoice manually and are waiting to be paid:</b> use Quick Add → Open Invoice / AR.</p></div>
          <div><span>6</span><p><b>If you spent money:</b> use Quick Add → Paid Expense. If you owe it but have not paid, use Bill / AP.</p></div>
          <div><span>7</span><p><b>If you only have a receipt/PDF:</b> use Document Intake. That creates an Audit Doc only. Then create or attach the business record it proves.</p></div>
        </div>
      </div>
      <div class="card">
        <h3>What “Enter Once, Show Many” Means</h3>
        <p>If you enter an Etsy order as a Sale, that one Sale can appear on the Dashboard, charts, exports, search results, tax prep, and review queues. You do not retype that Etsy order into every tab.</p>
        <p>You only add another record when it is a different real-world thing. A proof PDF is different from the sale. An unpaid invoice is different from a paid sale. A customer job is different from the money.</p>
        <p><b>Think of tabs as views/ledgers, not chores to fill out one by one.</b></p>
      </div>
    </div>

    <div class="card">
      <div class="card-header-lite"><h3>Where Do I Enter This?</h3><span class="badge">Use this table first</span></div>
      <div class="workflow-list">
        ${scenarios.map(([event, start, detail]) => `<div class="workflow-row"><div><span>${escapeHtml(event)}</span><strong>${escapeHtml(start)}</strong></div><p>${escapeHtml(detail)}</p></div>`).join('')}
      </div>
    </div>

    <div class="grid two">
      <div class="card">
        <h3>What Document Intake Does</h3>
        <div class="help-list">
          <div class="help-item"><strong>Does now</strong><p>Saves uploaded files locally, creates Audit Docs, stores file paths, and keeps best-effort extracted text previews for readable files/PDFs. You can edit the Audit Doc after upload.</p></div>
          <div class="help-item"><strong>Does not do yet</strong><p>It does not reliably turn a PDF into a Sale, AR Invoice, Expense, or Bill by itself.</p></div>
          <div class="help-item"><strong>Best process</strong><p>Upload proof, then use Quick Add for the business event. Or open an existing Sale/Expense/Invoice/Bill and use Attach File on the proof field. The saved file path is filled into Source/Proof, Receipt/Proof, or File Path/URL.</p></div>
        </div>
      </div>
      <div class="card">
        <h3>When Do I Enter More Than One Record?</h3>
        <div class="help-list">
          <div class="help-item"><strong>Etsy order</strong><p>Usually one Sale record plus attached proof. Do not create AR because Etsy is not an unpaid direct invoice.</p></div>
          <div class="help-item"><strong>Direct custom job</strong><p>Job first if you need to track the work. AR Invoice when you send the invoice. Sale when money is received.</p></div>
          <div class="help-item"><strong>Receipt for filament you already bought</strong><p>One Expense record plus attached receipt. No AP Bill because you do not owe anything.</p></div>
          <div class="help-item"><strong>Vendor bill you have not paid</strong><p>AP Bill now. Mark it paid later, and add/confirm an Expense only if you want the paid cash outflow tracked separately.</p></div>
          <div class="help-item"><strong>Proof file</strong><p>Audit Doc stores the file/path. The Sale, Expense, Bill, or Invoice stores the business numbers.</p></div>
        </div>
      </div>
    </div>

    <div class="card">
      <div class="card-header-lite"><h3>What Each Tab Is For</h3><span class="badge">Reference</span></div>
      <div class="help-list">
        ${tabs.map(([tab, detail]) => `<div class="help-item"><strong>${escapeHtml(tab)}</strong><p>${escapeHtml(detail)}</p></div>`).join('')}
      </div>
    </div>`;
}

function workflowDiagram() {
  const nodes = [
    ['quote', 'Customer asks price', 'Estimate builder'],
    ['approve', 'Customer approves', 'Convert / create invoice'],
    ['paid', 'Money received', 'Sale + cash reporting'],
    ['etsy', 'Etsy order', 'Quick Add sale'],
    ['expense', 'Paid purchase', 'Quick Add expense'],
    ['bill', 'Vendor bill unpaid', 'AP bill'],
    ['proof', 'Receipt / PDF / screenshot', 'Document Intake']
  ];
  return `
    <section class="card workflow-diagram-card">
      <div class="card-header-lite"><h3>Workflow Map</h3><span class="badge">Possible paths</span></div>
      <div class="workflow-diagram" aria-label="Business ledger workflow diagram">
        <div class="flow-lane">
          ${flowNode(nodes[0])}
          ${flowArrow('approved')}
          ${flowNode(nodes[1])}
          ${flowArrow('invoice sent')}
          <div class="flow-node"><span>AR</span><strong>Customer owes you</strong><small>Open invoice / receivable</small></div>
          ${flowArrow('paid')}
          ${flowNode(nodes[2])}
        </div>
        <div class="flow-lane">
          ${flowNode(nodes[3])}
          ${flowArrow('already paid')}
          <div class="flow-node"><span>SALE</span><strong>Revenue record</strong><small>Dashboard, charts, exports, tax prep</small></div>
        </div>
        <div class="flow-lane">
          ${flowNode(nodes[4])}
          ${flowArrow('paid now')}
          <div class="flow-node"><span>EXP</span><strong>Expense record</strong><small>Deduction/proof/tax prep</small></div>
          ${flowNode(nodes[5])}
          ${flowArrow('pay later')}
          <div class="flow-node"><span>AP</span><strong>Bill record</strong><small>What you owe vendors</small></div>
        </div>
        <div class="flow-lane proof-lane">
          ${flowNode(nodes[6])}
          ${flowArrow('attach to')}
          <div class="flow-node wide"><span>PROOF</span><strong>Audit Docs</strong><small>Links back to Sale, Expense, Invoice, Bill, Job, or Asset</small></div>
        </div>
      </div>
    </section>`;
}

function flowNode([key, title, detail]) {
  return `<div class="flow-node ${key}"><span>${escapeHtml(key)}</span><strong>${escapeHtml(title)}</strong><small>${escapeHtml(detail)}</small></div>`;
}

function flowArrow(label) {
  return `<div class="flow-arrow"><span>${escapeHtml(label)}</span></div>`;
}

async function backupDb() {
  try {
    const response = await fetch('/api/system/backup', { method: 'POST' });
    if (!response.ok) throw new Error(await response.text());
    const blob = await response.blob();
    const disposition = response.headers.get('content-disposition') || '';
    const match = disposition.match(/filename="?([^";]+)"?/i);
    const filename = match ? match[1] : `epata-business-ledger-${Date.now()}.db`;
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
    toast('Backup downloaded.');
  } catch (err) {
    toast(`Backup failed: ${err.message}`);
  }
}

qs('#backupBtn').onclick = backupDb;
bindTooltips();
qs('#globalSearch').oninput = () => {
  clearTimeout(appState.globalSearchTimer);
  appState.globalSearchTimer = setTimeout(() => showPage('globalSearch'), 220);
};
qs('#modalClose').onclick = closeModal;
qs('#modalCancel').onclick = e => { e.preventDefault(); closeModal(); };
qs('#modalBackdrop').onclick = closeModal;
qs('#modalSave').onclick = e => { e.preventDefault(); saveModal(); };

document.addEventListener('keydown', e => { if (e.key === 'Escape') closeModal(); });

renderNav();
api('/api/app-info').then(info => {
  if (info?.isTest) qs('#testModeBanner')?.classList.remove('hidden');
}).catch(() => {});
showPage('dashboard');
