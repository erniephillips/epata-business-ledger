(function (root) {
  function fallbackEscape(value) {
    return String(value ?? '').replace(/[&<>"']/g, char => ({
      '&': '&amp;',
      '<': '&lt;',
      '>': '&gt;',
      '"': '&quot;',
      "'": '&#39;'
    }[char]));
  }

  function fallbackMoney(value) {
    return Number(value || 0).toLocaleString(undefined, {
      style: 'currency',
      currency: 'USD'
    });
  }

  function fallbackEmptyState(title, detail) {
    return `<div class="empty-state"><div class="empty-title">${fallbackEscape(title)}</div><div class="empty-desc">${fallbackEscape(detail)}</div></div>`;
  }

  function createDashboardRenderer(options = {}) {
    const escapeHtml = options.escapeHtml || fallbackEscape;
    const escapeAttr = options.escapeAttr || escapeHtml;
    const formatMoney = options.formatMoney || fallbackMoney;
    const formatShortDate = options.formatShortDate || (value => value ? String(value).slice(0, 10) : '');
    const emptyState = options.emptyState || fallbackEmptyState;

    function renderBreakdownBody(breakdown = {}) {
      const rows = Array.isArray(breakdown.items) ? breakdown.items : [];
      const money = Boolean(breakdown.money);
      const totalRows = Number(breakdown.total || 0);
      const total = money
        ? formatMoney(breakdown.total)
        : `${totalRows} row${totalRows === 1 ? '' : 's'}`;

      return `
    <div class="breakdown-summary"><span>${escapeHtml(breakdown.formula || '')}<br>${rows.length} contributing record${rows.length === 1 ? '' : 's'}.</span><strong>${escapeHtml(total)}</strong></div>
    ${rows.length ? `<div class="table-wrap"><table><thead><tr><th>Date</th><th>Source</th><th>Record</th><th>Why it counts</th><th>Contribution</th><th></th></tr></thead><tbody>
      ${rows.map(row => {
        const amount = Number(row.amount || 0);
        const amountLabel = money ? formatMoney(amount) : String(amount);
        const id = Number(row.id || 0);
        return `<tr>
          <td>${escapeHtml(formatShortDate(row.date))}</td>
          <td>${escapeHtml(row.sourceType || '')}</td>
          <td><strong>${escapeHtml(row.label || '')}</strong></td>
          <td>${escapeHtml(row.detail || '')}</td>
          <td class="breakdown-amount ${amount < 0 ? 'negative' : 'positive'}">${escapeHtml(amountLabel)}</td>
          <td><button class="ghost-button compact-action" type="button" onclick="openDashboardBreakdownRecord('${escapeAttr(row.route || '')}',${id})">Open</button></td>
        </tr>`;
      }).join('')}
    </tbody></table></div>` : emptyState('No contributing records.', 'This total is currently zero.')}
  `;
    }

    function summarizeLineChart(rows = [], series = []) {
      const safeRows = Array.isArray(rows) ? rows : [];
      const safeSeries = Array.isArray(series) ? series : [];
      if (!safeRows.length) {
        return { kind: 'empty', reason: 'no-rows', rows: 0, series: safeSeries.length };
      }

      const values = safeRows.flatMap(row => safeSeries.map(item => Number(row?.[item.key] || 0)));
      const max = Math.max(...values, 1);
      const min = Math.min(...values, 0);
      return {
        kind: 'chart',
        rows: safeRows.length,
        series: safeSeries.length,
        max,
        min,
        span: max - min || 1,
        allZero: values.every(value => value === 0)
      };
    }

    function summarizeDonutChart(items = []) {
      const safeItems = Array.isArray(items) ? items : [];
      const total = safeItems.reduce((sum, item) => sum + Math.max(0, Number(item?.value || 0)), 0);
      if (total <= 0) {
        return { kind: 'empty', reason: 'no-positive-values', total, slices: 0 };
      }

      return { kind: 'chart', total, slices: safeItems.filter(item => Number(item?.value || 0) > 0).length };
    }

    return {
      renderBreakdownBody,
      summarizeLineChart,
      summarizeDonutChart
    };
  }

  root.EpataDashboardState = {
    createDashboardRenderer
  };
})(typeof window !== 'undefined' ? window : globalThis);
