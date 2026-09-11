import { escapeHtml, money } from './utils.js?v=5';

export function buildProductOptionsHtml(products = []) {
  return products.map(product => {
    const label = escapeHtml(product.name || '');
    const detail = [product.sku, product.targetPrice ? money(product.targetPrice) : '']
      .filter(Boolean)
      .join(' · ');

    return `<option value="${label}">${escapeHtml(detail)}</option>`;
  }).join('');
}

export function findProductByName(products = [], projectName = '') {
  const normalizedName = String(projectName || '').trim().toLowerCase();
  if (!normalizedName) return null;

  return products.find(product => String(product.name || '').trim().toLowerCase() === normalizedName) || null;
}

export function buildSelectedProductPatch(product = null, currentValues = {}) {
  if (!product) return { fields: {}, lineItem: null };

  const fields = {};
  if (product.material) fields.material = product.material;
  if (product.color) fields.color = product.color;
  if (product.grams != null) fields.grams = product.grams;
  if (product.printHours != null) fields.hours = product.printHours;
  if (product.materialCostPerGram != null) fields.gramRate = product.materialCostPerGram;
  if (product.machineRatePerHour != null) fields.hourRate = product.machineRatePerHour;
  if (product.designMinutes != null) fields.designHours = Number(product.designMinutes || 0) / 60;
  if (product.packagingCost != null && !Number(currentValues.postFee || 0)) fields.postFee = product.packagingCost;
  if (product.targetPrice != null && !Number(currentValues.minimum || 0)) fields.minimum = product.targetPrice;

  const lineItem = product.targetPrice && !currentValues.hasMeaningfulLine
    ? {
        description: product.name,
        details: [product.sku, product.category, product.material].filter(Boolean).join(' · '),
        qty: 1,
        rate: product.targetPrice,
      }
    : null;

  return { fields, lineItem };
}
