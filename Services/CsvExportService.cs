using System.Globalization;
using System.Reflection;
using System.Text;

namespace EPATA.BusinessLedger.Services;

public static class CsvExportService
{
    public static string ToCsv<T>(IEnumerable<T> rows)
    {
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', props.Select(p => Escape(p.Name))));

        foreach (var row in rows)
        {
            var values = props.Select(p => Escape(FormatValue(p.GetValue(row))));
            sb.AppendLine(string.Join(',', values));
        }

        return sb.ToString();
    }

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
