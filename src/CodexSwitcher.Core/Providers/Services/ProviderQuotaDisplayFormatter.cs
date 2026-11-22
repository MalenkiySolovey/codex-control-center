using System.Globalization;
using Llm.ProviderKit.Inspection;

namespace CodexSwitcher.Core.Providers.Services;

public static class ProviderQuotaDisplayFormatter
{
    public static string? Format(ProviderMetricDisplay? display, CultureInfo culture, bool symbolAfterAmount = false)
    {
        if (display is not { IsValid: true }) return null;

        var belowCent = display.Value is > 0m and < 0.01m;
        var amount = (belowCent ? 0.01m : display.Value).ToString("N2", culture);
        var text = symbolAfterAmount ? $"{amount} {display.Symbol}" : $"{display.Symbol}{amount}";
        return belowCent ? $"<{text}" : text;
    }
}
