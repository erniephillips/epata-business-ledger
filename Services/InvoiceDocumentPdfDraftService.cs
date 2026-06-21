using System.Globalization;
using System.Text.RegularExpressions;
using EPATA.BusinessLedger.Data;
using EPATA.BusinessLedger.Models;
using Microsoft.EntityFrameworkCore;

namespace EPATA.BusinessLedger.Services;

public sealed class InvoiceDocumentPdfDraftService(AppDbContext db)
{
    private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");

    public async Task<AiEstimateDraftResult> CreateDraftAsync(
        string sourceText,
        string? sourceName,
        IReadOnlyCollection<string>? sourceWarnings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            throw new InvalidOperationException("Upload a readable EPATA invoice or estimate PDF. Scanned/image-only PDFs need OCR before they can be mapped.");
        }

        var flat = Clean(sourceText);
        var docNumber = NormalizeDocumentNumber(FirstMatch(flat, @"(?i)\b(?<value>(?:INV|EST)[-\s]\d{4}-\d{4})\b"));
        var docType = GuessDocumentType(flat, docNumber);
        var docDate = FormatDate(FirstMatch(flat, @"(?i)(?<!Due\s)\bDate\s*[:#-]?\s*(?<value>[A-Z][a-z]+ \d{1,2}, \d{4}|\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{4})"));
        var dueDate = FormatDate(FirstMatch(flat, @"(?i)\b(?:Due Date|Valid Until|Due)\s*[:#-]?\s*(?<value>[A-Z][a-z]+ \d{1,2}, \d{4}|\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{4})"));
        var contactStops = new[] { "Bill To", "Project Details", "Project Name", "Job Name", "Pricing Summary", "Line Items", "Breakdown" };
        var detailStops = new[] { "Description", "Project Description", "Job Description", "Details", "Material", "Filament", "Color", "Colour", "Infill", "Fill", "Pricing Summary", "Line Items", "Breakdown" };
        var projectNameStops = new[] { "Description", "Details", "Material", "Filament", "Color", "Colour", "Infill", "Fill", "Pricing Summary", "Line Items", "Breakdown" };
        var preparedFor = ExtractFieldAny(flat, ["Prepared For", "Prepared For:"], contactStops);
        var billTo = ExtractFieldAny(flat, ["Bill To:", "Bill To", "Client:", "Customer:", "Client", "Customer"], ["Project Details", "Project Name", "Job Name", "Pricing Summary", "Line Items", "Breakdown"]);
        var projectName = ExtractFieldAny(flat, ["Project Name", "Project Title"], projectNameStops)
            ?? ExtractFieldAny(flat, ["Job Name"], ["Job Description", .. projectNameStops]);
        var projectDescription = ExtractFieldAny(flat, ["Project Description", "Job Description", "Description", "Details"], ["Material", "Filament", "Color", "Colour", "Infill", "Fill", "Pricing Summary", "Line Items", "Breakdown"]);
        var material = ExtractFieldAny(flat, ["Material", "Filament"], ["Color", "Colour", "Infill", "Fill", "Pricing Summary", "Line Items", "Breakdown"]);
        var color = ExtractFieldAny(flat, ["Color", "Colour"], ["Infill", "Fill", "Pricing Summary", "Line Items", "Breakdown"]);
        var infill = ExtractFieldAny(flat, ["Infill", "Fill"], ["Pricing Summary", "Line Items", "Breakdown"]);
        var pricingGuide = ExtractFieldAny(flat, ["Pricing Guide", "Pricing Notes", "Pricing"], ["Terms & Notes", "Terms and Notes", "Terms", "Notes", "Payment Status", "Approval", "Accepted", "Voided"]);
        var termStops = new[] { "Payment Status", "Status", "Approval", "Accepted", "Voided", "Thank You!" };
        var termsNotes = ExtractFieldAny(flat, ["Terms & Notes", "Terms and Notes", "Customer Notes", "Terms:", "Notes:"], termStops)
            ?? ExtractFieldAfterLastPattern(flat, @"\bNotes\b", termStops);
        termsNotes = RemoveAfter(termsNotes, "AI-assisted tools may be used");

        var customerEmail = FirstMatch(billTo ?? flat, @"(?i)\b(?<value>[\w.+-]+@[\w.-]+\.[a-z]{2,})\b");
        var customerPhone = FormatUsPhone(FirstMatch(billTo ?? flat, @"(?<!\d)(?<value>(?:\+?1[\s.-]?)?\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4})(?!\d)"));
        var customerName = FirstNonBlank(preparedFor, ExtractCustomerName(billTo, customerEmail, customerPhone));
        var customerAddress = ExtractCustomerAddress(billTo, customerName, customerEmail, customerPhone);

        var lineItems = ExtractLineItems(flat);
        var lineSubtotal = lineItems.Sum(line => line.Quantity * line.Rate);
        var subtotal = FirstMoney(flat, "Subtotal");
        if (subtotal <= 0 && lineSubtotal > 0) subtotal = lineSubtotal;

        var discount = FirstMoneyAny(flat, "Discount", "Less Discount");
        var rushPercent = FirstPercent(flat, @"(?i)\bRush(?:\s+Fee)?\s*\((?<value>\d+(?:\.\d+)?)\s*%\)");
        var taxRate = FirstPercent(flat, @"(?i)\b(?:Sales\s+Tax|Tax)\s*\((?<value>\d+(?:\.\d+)?)\s*%\)");
        var taxAmount = FirstMoneyAny(flat, @"Sales\s+Tax\s*\(\d+(?:\.\d+)?%\)", @"Tax\s*\(\d+(?:\.\d+)?%\)", "Sales Tax", "Tax");
        var invoiceTotal = FirstMoneyAny(flat, "Invoice Total", "Total Due", "Amount Due", "Grand Total", "Total");
        var estimatedTotal = FirstMoneyAny(flat, "Estimated Total", "Estimate Total", "Grand Total", "Total");
        var total = invoiceTotal > 0 ? invoiceTotal : estimatedTotal > 0 ? estimatedTotal : subtotal - discount + (subtotal * rushPercent / 100m) + taxAmount;
        var amountPaid = docType == "INVOICE" ? FirstMoneyAny(flat, "Amount Paid", "Paid", "Payment Received") : 0;
        var status = GuessStatus(flat, docType, amountPaid, total);

        if (lineItems.Count == 0 && total > 0)
        {
            lineItems.Add(new AiEstimateLineItem
            {
                Description = FirstNonBlank(projectName, "Imported 3D print service")!,
                Details = "Recovered from uploaded EPATA PDF. Review line description and pricing before saving.",
                Quantity = 1,
                Rate = Math.Max(0, subtotal > 0 ? subtotal : total)
            });
            lineSubtotal = lineItems.Sum(line => line.Quantity * line.Rate);
        }

        var warnings = new List<string>(sourceWarnings ?? []);
        if (string.IsNullOrWhiteSpace(docNumber))
        {
            warnings.Add("No EPATA document number was found. The builder will use the next available number unless you enter one.");
        }
        else
        {
            var duplicate = await db.InvoiceDocuments.AsNoTracking()
                .Where(document => !document.IsArchived && document.DocNumber == docNumber)
                .Select(document => new { document.Id, document.DocType, document.CustomerName, document.ProjectName })
                .FirstOrDefaultAsync(cancellationToken);
            if (duplicate is not null)
            {
                warnings.Add($"Document number {docNumber} already exists as record #{duplicate.Id} ({duplicate.DocType}, {duplicate.CustomerName}, {duplicate.ProjectName}). Review before saving so you do not create a duplicate.");
            }
        }
        if (lineItems.Count == 0)
        {
            warnings.Add("No line-item table could be recovered. Add at least one reviewed line item before saving.");
        }
        warnings.Add("PDF import used local text-mapping rules. Review every field, amount, tax rate, status, and line item before saving.");

        return new AiEstimateDraftResult
        {
            Provider = "Local rules PDF mapper",
            UsedAi = false,
            SourceName = sourceName ?? "Uploaded invoice or estimate PDF",
            ExecutionReceipt = new AiExecutionReceipt
            {
                Engine = "LOCAL RULES",
                UsedAi = false,
                Provider = "Local rules PDF mapper",
                SourceCharacters = sourceText.Length,
                ModelInputCharacters = 0,
                ExecutedAtUtc = DateTimeOffset.UtcNow
            },
            Prefill = new AiEstimatePrefill
            {
                DocNumber = docNumber,
                DocType = docType,
                Status = status,
                CustomerName = customerName,
                CustomerPhone = customerPhone,
                CustomerEmail = customerEmail,
                CustomerAddress = customerAddress,
                PreparedFor = preparedFor ?? customerName,
                ProjectName = projectName,
                Material = material,
                Color = color,
                Infill = infill,
                ProjectDescription = projectDescription,
                ProjectNotes = string.Join(Environment.NewLine + Environment.NewLine, new[]
                {
                    $"PDF IMPORT ASSISTANCE: Draft mapped from {sourceName ?? "uploaded PDF"}. Review every recovered field before saving.",
                    "LOCAL RULES RECEIPT: Fixed local text-mapping rules prepared this draft. No database record was created by the import."
                }),
                PageSize = "A4",
                DocDate = docDate ?? DateTime.Today.ToString("yyyy-MM-dd"),
                DueDate = dueDate ?? DateTime.Today.AddDays(docType == "INVOICE" ? 7 : 14).ToString("yyyy-MM-dd"),
                PaymentMethod = "Unknown / Review",
                PricingGuide = pricingGuide,
                TermsNotes = termsNotes,
                StandardTurnaround = "Recovered from PDF; review before sending",
                RushTurnaround = "Recovered from PDF; review before sending",
                DocTaxRate = Math.Clamp(taxRate, 0, 30),
                DocRushPercent = Math.Clamp(rushPercent, 0, 200),
                DocDiscount = Math.Max(0, discount),
                AmountPaid = Math.Max(0, amountPaid),
                CalcGramRate = 0.05m,
                CalcHourRate = 3m,
                CalcDesignRate = 25m,
                CalcMinimum = 15m,
                CalcDifficulty = 1m,
                CalcTaxRate = Math.Clamp(taxRate, 0, 30),
                CalcRush = Math.Clamp(rushPercent, 0, 200),
                CalcDiscount = Math.Max(0, discount),
                AssistanceSource = "LOCAL RULES",
                AssistanceProvider = "Local rules PDF mapper",
                LineItems = lineItems
            },
            Pricing = new AiEstimatePricingSummary
            {
                PricingMode = "Recovered EPATA PDF totals",
                RequiresPricingReview = true,
                LineSubtotal = Money(lineSubtotal > 0 ? lineSubtotal : subtotal),
                RushAmount = Money((lineSubtotal > 0 ? lineSubtotal : subtotal) * rushPercent / 100m),
                Discount = Money(discount),
                TaxableSubtotal = Money(Math.Max(0, (lineSubtotal > 0 ? lineSubtotal : subtotal) - discount + ((lineSubtotal > 0 ? lineSubtotal : subtotal) * rushPercent / 100m))),
                TaxAmount = Money(taxAmount),
                Total = Money(total)
            },
            Questions = [],
            Warnings = warnings
        };
    }

    private static string GuessDocumentType(string flat, string? docNumber)
    {
        if (docNumber?.StartsWith("INV-", StringComparison.OrdinalIgnoreCase) == true) return "INVOICE";
        if (docNumber?.StartsWith("EST-", StringComparison.OrdinalIgnoreCase) == true) return "ESTIMATE";
        return Regex.IsMatch(flat, @"(?i)\bINVOICE\b") ? "INVOICE" : "ESTIMATE";
    }

    private static string GuessStatus(string flat, string docType, decimal amountPaid, decimal total)
    {
        var status = FirstMatch(flat, @"(?i)\bStatus\s*[:#-]?\s*(?<value>Draft|Sent|Partial|Paid|Accepted|Void)\b")
            ?? FirstMatch(flat, @"(?i)\b(?<value>Draft|Sent|Paid|Accepted|Void)\b");
        status = string.IsNullOrWhiteSpace(status) ? "Draft" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(status.ToLowerInvariant());
        if (docType == "ESTIMATE" && status.Equals("Paid", StringComparison.OrdinalIgnoreCase)) return "Accepted";
        if (docType == "INVOICE" && amountPaid > 0 && amountPaid + 0.01m >= total && total > 0) return "Paid";
        if (docType == "INVOICE" && amountPaid > 0) return "Partial";
        return status;
    }

    private static List<AiEstimateLineItem> ExtractLineItems(string flat)
    {
        var block = ExtractFieldAny(flat, ["Breakdown", "Line Items", "Items"], ["Pricing Guide", "Pricing Notes", "Pricing", "Terms & Notes", "Terms and Notes", "Terms", "Notes", "Payment Status", "Approval", "Accepted", "Voided"]);
        if (string.IsNullOrWhiteSpace(block)) return [];

        var header = Regex.Match(block, @"(?i)#?\s+Description\s+(?:Calculation\s*/\s*)?Details\s+Qty\s+(?:Unit\s+)?(?:Rate|Price)\s+(?:Amount|Total)");
        if (header.Success) block = block[(header.Index + header.Length)..];

        var matches = Regex.Matches(
            block,
            @"(?is)(?:^|\s)(?<index>\d{1,2})\s+(?<text>.+?)\s+(?<quantity>\d+(?:\.\d+)?)\s+\$?\s*(?<rate>\d[\d,]*(?:\.\d{2})?)\s+\$?\s*(?<amount>\d[\d,]*(?:\.\d{2})?)(?=\s+\d{1,2}\s+|\s*$)");

        return matches
            .Cast<Match>()
            .Select(match =>
            {
                var text = Clean(match.Groups["text"].Value);
                var quantity = DecimalValue(match.Groups["quantity"].Value);
                var rate = DecimalValue(match.Groups["rate"].Value);
                var split = SplitLineText(text);
                return new AiEstimateLineItem
                {
                    Description = Trim(split.Description, 100),
                    Details = Trim(split.Details, 300),
                    Quantity = quantity > 0 ? quantity : 1,
                    Rate = Math.Max(0, rate)
                };
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Description))
            .Take(20)
            .ToList();
    }

    private static (string Description, string Details) SplitLineText(string text)
    {
        var parts = Regex.Split(text, @"\s+-\s+|\s+\|\s+").Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        if (parts.Length >= 2)
        {
            return (parts[0], string.Join(" - ", parts.Skip(1)));
        }

        return text.Length <= 100
            ? (text, string.Empty)
            : (text[..100].Trim(), text[100..].Trim());
    }

    private static string? ExtractCustomerName(string? billTo, string? email, string? phone)
    {
        if (string.IsNullOrWhiteSpace(billTo)) return null;
        var value = billTo;
        if (!string.IsNullOrWhiteSpace(email)) value = value.Replace(email, "", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(phone)) value = value.Replace(phone, "", StringComparison.OrdinalIgnoreCase);
        value = Regex.Replace(value, @"(?<!\d)(?:\+?1[\s.-]?)?\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}(?!\d)", "");
        value = Regex.Replace(
            value,
            @"(?i)\b\d{1,6}\s+[A-Za-z0-9.'-]+(?:\s+[A-Za-z0-9.'-]+){0,4}\s+(?:Street|St|Avenue|Ave|Lane|Ln|Road|Rd|Drive|Dr|Court|Ct|Boulevard|Blvd|Way|Place|Pl)\b.*$",
            "");
        return FirstMeaningfulChunk(value);
    }

    private static string? ExtractCustomerAddress(string? billTo, string? customerName, string? email, string? phone)
    {
        if (string.IsNullOrWhiteSpace(billTo)) return null;
        var value = billTo;
        foreach (var part in new[] { customerName, email, phone })
        {
            if (!string.IsNullOrWhiteSpace(part)) value = value.Replace(part, "", StringComparison.OrdinalIgnoreCase);
        }
        value = Regex.Replace(value, @"(?<!\d)(?:\+?1[\s.-]?)?\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}(?!\d)", "");
        return string.IsNullOrWhiteSpace(value) ? null : Trim(Clean(value), 240);
    }

    private static string? ExtractField(string? input, string label, params string[] stopLabels)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var start = CultureInfo.InvariantCulture.CompareInfo.IndexOf(input, label, CompareOptions.IgnoreCase);
        if (start < 0) return null;

        start += label.Length;
        var end = input.Length;
        foreach (var stop in stopLabels)
        {
            var candidate = CultureInfo.InvariantCulture.CompareInfo.IndexOf(input, stop, start, CompareOptions.IgnoreCase);
            if (candidate >= 0 && candidate < end) end = candidate;
        }

        var value = Clean(input[start..end].Trim(' ', ':', '-', '|'));
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ExtractFieldAny(string? input, IReadOnlyCollection<string> labels, params string[] stopLabels)
    {
        foreach (var label in labels)
        {
            var value = ExtractField(input, label, stopLabels);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private static string? ExtractFieldAfterLastPattern(string? input, string labelPattern, params string[] stopLabels)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var matches = Regex.Matches(input, labelPattern, RegexOptions.IgnoreCase);
        if (matches.Count == 0) return null;

        var match = matches[^1];
        var start = match.Index + match.Length;
        var end = input.Length;
        foreach (var stop in stopLabels)
        {
            var candidate = CultureInfo.InvariantCulture.CompareInfo.IndexOf(input, stop, start, CompareOptions.IgnoreCase);
            if (candidate >= 0 && candidate < end) end = candidate;
        }

        var value = Clean(input[start..end].Trim(' ', ':', '-', '|'));
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static decimal FirstMoney(string input, string labelOrPattern)
    {
        var pattern = labelOrPattern.Contains('\\') || labelOrPattern.Contains('(')
            ? $@"(?i){labelOrPattern}\s*[:#-]?\s*-?\$?\s*(?<value>[\d,]+(?:\.\d{{1,2}})?)"
            : $@"(?i)\b{Regex.Escape(labelOrPattern)}\b\s*[:#-]?\s*-?\$?\s*(?<value>[\d,]+(?:\.\d{{1,2}})?)";
        var match = Regex.Match(input, pattern);
        return match.Success ? DecimalValue(match.Groups["value"].Value) : 0;
    }

    private static decimal FirstMoneyAny(string input, params string[] labelsOrPatterns)
    {
        foreach (var labelOrPattern in labelsOrPatterns)
        {
            var value = FirstMoney(input, labelOrPattern);
            if (value > 0) return value;
        }

        return 0;
    }

    private static decimal FirstPercent(string input, string pattern)
    {
        var match = Regex.Match(input, pattern);
        return match.Success ? DecimalValue(match.Groups["value"].Value) : 0;
    }

    private static decimal DecimalValue(string? value)
    {
        var text = Regex.Replace(value ?? string.Empty, @"[^\d.\-]", "");
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static string? FirstMatch(string input, string pattern)
    {
        var match = Regex.Match(input, pattern);
        return match.Success ? Clean(match.Groups["value"].Value) : null;
    }

    private static string? NormalizeDocumentNumber(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Regex.Replace(value.Trim().ToUpperInvariant(), @"\s+", "-");
    }

    private static string? FormatDate(string? value)
    {
        return DateTime.TryParse(value, UsCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }

    private static string? FormatUsPhone(string? value)
    {
        var digits = Regex.Replace(value ?? string.Empty, @"\D", "");
        if (digits.Length == 11 && digits.StartsWith('1')) digits = digits[1..];
        return digits.Length == 10 ? $"({digits[..3]}) {digits.Substring(3, 3)}-{digits[6..]}" : value;
    }

    private static string? FirstMeaningfulChunk(string value)
    {
        var clean = Clean(value);
        if (string.IsNullOrWhiteSpace(clean)) return null;
        var match = Regex.Match(clean, @"(?<value>[A-Z][A-Za-z0-9.'-]+(?:\s+[A-Z][A-Za-z0-9.'-]+){0,4})");
        return match.Success ? Trim(match.Groups["value"].Value, 120) : Trim(clean, 120);
    }

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? RemoveAfter(string? value, string marker)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var index = CultureInfo.InvariantCulture.CompareInfo.IndexOf(value, marker, CompareOptions.IgnoreCase);
        return index >= 0 ? value[..index].Trim() : value;
    }

    private static string Clean(string value) => Regex.Replace(value, @"[^\S\r\n]+", " ").Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Trim(string? value, int maxLength)
    {
        var clean = Clean(value ?? string.Empty);
        return clean.Length <= maxLength ? clean : clean[..maxLength].Trim();
    }

    private static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
