using System.Globalization;
using System.Text;

namespace IPLMerchandise.Web.Client.Formatting;

/// <summary>
/// Rupee formatting with Indian digit grouping (₹1,00,000.00), always two decimals.
/// Done by hand rather than via the en-IN culture so the output is identical in every browser and in tests,
/// regardless of which ICU data the WebAssembly runtime loaded.
/// </summary>
public static class Money
{
    public static string Format(decimal amount)
    {
        var rounded = decimal.Round(Math.Abs(amount), 2, MidpointRounding.AwayFromZero);
        var text = rounded.ToString("0.00", CultureInfo.InvariantCulture);
        var integer = text[..^3];
        var fraction = text[^2..];

        var grouped = new StringBuilder();
        if (integer.Length <= 3)
        {
            grouped.Append(integer);
        }
        else
        {
            var head = integer[..^3];
            for (var i = 0; i < head.Length; i++)
            {
                if (i > 0 && (head.Length - i) % 2 == 0) grouped.Append(',');
                grouped.Append(head[i]);
            }
            grouped.Append(',').Append(integer[^3..]);
        }
        return $"{(amount < 0 ? "-" : "")}₹{grouped}.{fraction}";
    }
}
