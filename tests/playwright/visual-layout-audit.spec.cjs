const { expect, test } = require('@playwright/test');

const VIEWPORTS = [
  { name: 'desktop', width: 1440, height: 900 },
  { name: 'mobile', width: 390, height: 844 },
];

async function waitForApp(page) {
  await expect(page.locator('#app')).not.toContainText('Loading...');
  await expect(page.locator('#app')).not.toContainText('Something broke');
}

async function attachScreenshot(page, testInfo, name, fullPage = true) {
  const path = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path, fullPage });
  await testInfo.attach(name, { path, contentType: 'image/png' });
}

async function visibleLayoutReport(page, rootSelector) {
  return page.locator(rootSelector).evaluate(root => {
    const viewport = { width: innerWidth, height: innerHeight };
    const rectOf = node => {
      const rect = node.getBoundingClientRect();
      return { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom, width: rect.width, height: rect.height };
    };
    const isVisible = node => {
      const style = getComputedStyle(node);
      const rect = node.getBoundingClientRect();
      return style.display !== 'none' && style.visibility !== 'hidden' && Number(style.opacity) !== 0 && rect.width > 0 && rect.height > 0;
    };
    const hasHorizontalScroller = node => {
      for (let parent = node.parentElement; parent && parent !== root.parentElement; parent = parent.parentElement) {
        const style = getComputedStyle(parent);
        if (/(auto|scroll)/.test(style.overflowX) && parent.scrollWidth > parent.clientWidth + 1) return true;
      }
      return false;
    };
    const selector = 'button, input:not([type="hidden"]), select, textarea, summary, a[href], [role="button"]';
    const controls = [...root.querySelectorAll(selector)].filter(isVisible);
    const outsideViewport = controls.flatMap(node => {
      const rect = node.getBoundingClientRect();
      if (rect.left >= -1 && rect.right <= viewport.width + 1) return [];
      if (hasHorizontalScroller(node)) return [];
      return [{
        tag: node.tagName,
        id: node.id,
        text: (node.innerText || node.getAttribute('aria-label') || '').trim().slice(0, 80),
        rect: rectOf(node),
      }];
    });
    return {
      viewport,
      root: rectOf(root),
      documentWidth: document.documentElement.scrollWidth,
      bodyWidth: document.body.scrollWidth,
      outsideViewport,
    };
  });
}

for (const viewport of VIEWPORTS) {
  test(`Document Intake dropdown stays contained and reflows at ${viewport.name} size`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto('/');
    await waitForApp(page);
    await page.evaluate(() => window.showPage('documentIntake'));
    await waitForApp(page);
    await page.locator('#relatedTypeTrigger').click();
    await expect(page.locator('#relatedTypePicker')).toHaveAttribute('open', '');

    const geometry = await page.evaluate(() => {
      const box = selector => {
        const rect = document.querySelector(selector).getBoundingClientRect();
        return { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom, width: rect.width, height: rect.height };
      };
      return {
        picker: box('#relatedTypePicker'),
        trigger: box('#relatedTypeTrigger'),
        menu: box('.descriptive-select-menu'),
        followingField: box('#relatedNumber'),
        uploadButton: box('#uploadDocsBtn'),
        menuPosition: getComputedStyle(document.querySelector('.descriptive-select-menu')).position,
      };
    });
    const report = await visibleLayoutReport(page, '#app');
    console.log(JSON.stringify({ surface: `intake-${viewport.name}`, geometry, report }));
    expect(geometry.menuPosition).toBe('static');
    expect(Math.abs(geometry.menu.left - geometry.trigger.left)).toBeLessThanOrEqual(1);
    expect(Math.abs(geometry.menu.right - geometry.trigger.right)).toBeLessThanOrEqual(1);
    expect(geometry.menu.top).toBeGreaterThanOrEqual(geometry.trigger.bottom);
    expect(geometry.followingField.top).toBeGreaterThanOrEqual(geometry.menu.bottom);
    expect(geometry.uploadButton.top).toBeGreaterThanOrEqual(geometry.followingField.bottom);
    expect(report.documentWidth).toBeLessThanOrEqual(report.viewport.width);
    expect(report.outsideViewport).toEqual([]);
    await attachScreenshot(page, testInfo, `intake-dropdown-${viewport.name}-viewport`, false);
    await attachScreenshot(page, testInfo, `intake-dropdown-${viewport.name}`);
  });

  test(`invoice builder and assistant modal stay contained at ${viewport.name} size`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto('/');
    await waitForApp(page);
    await page.evaluate(() => window.showPage('estimates', { resetInvoiceTool: true }));
    await expect(page.locator('#epataInvoiceMerged #view-builder')).toBeVisible();
    await expect(page.locator('#epataInvoiceMerged #docDate')).not.toHaveValue('');

    const builderReport = await visibleLayoutReport(page, '#epataInvoiceMerged');
    const builderChrome = await page.evaluate(() => {
      const rectOf = node => {
        const rect = node.getBoundingClientRect();
        return { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom, width: rect.width, height: rect.height };
      };
      const describeRail = (railSelector, itemSelector) => {
        const rail = document.querySelector(railSelector);
        const items = [...rail.querySelectorAll(itemSelector)].filter(node => getComputedStyle(node).display !== 'none');
        const rects = items.map(node => ({
          text: node.textContent.trim(),
          rect: rectOf(node),
          clientWidth: node.clientWidth,
          scrollWidth: node.scrollWidth,
        }));
        const overlaps = [];
        for (let i = 0; i < rects.length; i += 1) {
          for (let j = i + 1; j < rects.length; j += 1) {
            const a = rects[i].rect;
            const b = rects[j].rect;
            const intersects = a.left < b.right - 1 && a.right > b.left + 1
              && a.top < b.bottom - 1 && a.bottom > b.top + 1;
            if (intersects) overlaps.push([rects[i].text, rects[j].text]);
          }
        }
        return {
          rect: rectOf(rail),
          clientWidth: rail.clientWidth,
          scrollWidth: rail.scrollWidth,
          overflowX: getComputedStyle(rail).overflowX,
          items: rects,
          overlaps,
        };
      };
      return {
        tabs: describeRail('.merged-tabs', '.nav-item'),
        actions: describeRail('#view-builder .view-actions', 'button'),
      };
    });
    console.log(JSON.stringify({ surface: `builder-${viewport.name}`, report: builderReport, chrome: builderChrome }));
    expect(builderReport.documentWidth).toBeLessThanOrEqual(builderReport.viewport.width);
    expect(builderReport.outsideViewport).toEqual([]);
    expect(builderChrome.tabs.overlaps).toEqual([]);
    expect(builderChrome.tabs.items.filter(item => item.scrollWidth > item.clientWidth + 1)).toEqual([]);
    expect(builderChrome.actions.overlaps).toEqual([]);
    expect(builderChrome.actions.items.filter(item => item.scrollWidth > item.clientWidth + 1)).toEqual([]);
    if (viewport.name === 'mobile') {
      expect(builderChrome.tabs.overflowX).toBe('visible');
      expect(builderChrome.tabs.scrollWidth).toBeLessThanOrEqual(builderChrome.tabs.clientWidth + 1);
      expect(builderChrome.actions.scrollWidth).toBeLessThanOrEqual(builderChrome.actions.clientWidth + 1);
      for (const item of builderChrome.tabs.items) {
        expect(item.rect.left).toBeGreaterThanOrEqual(builderChrome.tabs.rect.left - 1);
        expect(item.rect.right).toBeLessThanOrEqual(builderChrome.tabs.rect.right + 1);
      }
      for (const item of builderChrome.actions.items) {
        expect(item.rect.left).toBeGreaterThanOrEqual(builderChrome.actions.rect.left - 1);
        expect(item.rect.right).toBeLessThanOrEqual(builderChrome.actions.rect.right + 1);
      }
    }
    await attachScreenshot(page, testInfo, `invoice-builder-${viewport.name}-viewport`, false);
    await attachScreenshot(page, testInfo, `invoice-builder-${viewport.name}`);

    await page.locator('#view-builder [data-ai-assistant-open]').first().click();
    await expect(page.locator('#aiAssistantModal')).toBeVisible();
    const modalReport = await page.evaluate(() => {
      const modal = document.querySelector('#aiAssistantModal');
      const panel = modal.querySelector('.ai-assistant-panel');
      const grid = modal.querySelector('.ai-assistant-grid');
      const source = modal.querySelector('.ai-assistant-source');
      const work = modal.querySelector('.ai-assistant-work');
      const footer = modal.querySelector('.ai-assistant-footer');
      const rectOf = node => {
        const rect = node.getBoundingClientRect();
        return { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom, width: rect.width, height: rect.height };
      };
      return {
        viewport: { width: innerWidth, height: innerHeight },
        modal: rectOf(modal),
        panel: rectOf(panel),
        grid: rectOf(grid),
        source: rectOf(source),
        work: rectOf(work),
        footer: rectOf(footer),
        modalZ: Number(getComputedStyle(modal).zIndex),
        topbarZ: Number(getComputedStyle(document.querySelector('.topbar')).zIndex),
        bodyOverflow: getComputedStyle(document.body).overflow,
      };
    });
    const openModalLayout = await visibleLayoutReport(page, '#aiAssistantModal');
    console.log(JSON.stringify({ surface: `assistant-${viewport.name}`, modalReport, report: openModalLayout }));
    expect(modalReport.panel.left).toBeGreaterThanOrEqual(0);
    expect(modalReport.panel.right).toBeLessThanOrEqual(viewport.width);
    expect(modalReport.panel.top).toBeGreaterThanOrEqual(0);
    expect(modalReport.panel.bottom).toBeLessThanOrEqual(viewport.height);
    expect(modalReport.grid.bottom).toBeLessThanOrEqual(modalReport.footer.top + 1);
    expect(modalReport.modalZ).toBeGreaterThan(modalReport.topbarZ);
    expect(modalReport.bodyOverflow).toBe('hidden');
    expect(openModalLayout.outsideViewport).toEqual([]);
    if (viewport.name === 'mobile') {
      expect(modalReport.work.top).toBeGreaterThanOrEqual(modalReport.source.bottom - 1);
    }
    await attachScreenshot(page, testInfo, `invoice-assistant-${viewport.name}-viewport`, false);
    await attachScreenshot(page, testInfo, `invoice-assistant-${viewport.name}`);
  });
}
