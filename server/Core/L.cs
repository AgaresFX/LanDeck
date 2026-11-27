using System.Globalization;

namespace PadNook.Core;

/// <summary>
/// UI translation for the PC side (tray, windows, messages sent to the devices).
/// Source strings are Italian and double as keys; <see cref="En"/> maps them to English.
/// Language: settings.language ("auto" | "it" | "en"); auto follows the Windows display language.
/// </summary>
public static partial class L
{
    public static string Setting { get; private set; } = "auto";

    public static bool English { get; private set; } = Detect("auto");

    public static void Init(string? setting)
    {
        Setting = setting is "it" or "en" ? setting : "auto";
        English = Detect(Setting);
    }

    private static bool Detect(string setting) => setting switch
    {
        "it" => false,
        "en" => true,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "it",
    };

    /// <summary>Culture for numbers shown on the keys (decimal comma in Italian).</summary>
    public static CultureInfo Culture => English ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Translate a plain string.</summary>
    public static string T(string it) => English && En.TryGetValue(it, out var en) ? en : it;

    /// <summary>Translate a composite-format string ({0}, {1:0}…) and format it.</summary>
    public static string F(string it, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(it), args);
}
