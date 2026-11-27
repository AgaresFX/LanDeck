using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PadNook.Actions;
using PadNook.Live;

namespace PadNook.Core;

/// <summary>How a substituted value is made safe for the place it is inserted into.</summary>
public enum Escape { None, Url, Json, Header }

/// <summary>
/// Variables in user text (HTTP requests): {ora} {data} {appunti} {contatore:NAME} {timer:NAME}
/// {segreto:NAME}. Secrets are inserted as they are (they are often whole URLs or tokens).
/// </summary>
public sealed partial class Templating(LiveStateStore state, SecretStore secrets, Config.ConfigStore config)
{
    /// <summary>Variables of the streaming event that started the current action (event rules).</summary>
    public static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> EventVars = new();

    [GeneratedRegex(@"\{(ora|data|appunti|utente|importo|premio|messaggio|contatore:[^{}]{1,60}|timer:[^{}]{1,60}|segreto:[^{}]{1,40})\}", RegexOptions.IgnoreCase)]
    private static partial Regex VarRegex();

    public static bool HasVariables(string? s) => s is not null && VarRegex().IsMatch(s);

    public string Fill(string? text, Escape esc)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string? clipboard = null;
        return VarRegex().Replace(text, m =>
        {
            var token = m.Groups[1].Value;
            var colon = token.IndexOf(':');
            var name = colon < 0 ? token.ToLowerInvariant() : token[..colon].ToLowerInvariant();
            var arg = colon < 0 ? "" : token[(colon + 1)..].Trim();
            if (name == "segreto")
            {
                var secret = secrets.Get(arg) ?? throw new ActionException(L.F("Credenziale «{0}» non trovata (pannello del profilo → Credenziali)", arg));
                // Raw in URLs (often a whole webhook URL or a token in the path), escaped elsewhere.
                return esc switch
                {
                    Escape.Header => StripNewlines(secret),
                    Escape.Json => JsonEncode(secret),
                    _ => secret,
                };
            }
            var value = name switch
            {
                "ora" => DateTime.Now.ToString("HH:mm"),
                "data" => DateTime.Now.ToString("dd/MM/yyyy"),
                "appunti" => clipboard ??= ReadClipboard(),
                "contatore" => state.CounterText(arg),
                "timer" => state.TimerText(TimerByName(arg)),
                "utente" or "importo" or "premio" or "messaggio" =>
                    EventVars.Value is { } ev && ev.TryGetValue(name, out var x) ? x : m.Value,
                _ => m.Value,
            };
            return esc switch
            {
                Escape.Url => Uri.EscapeDataString(value),
                Escape.Json => JsonEncode(value),
                Escape.Header => StripNewlines(value),
                _ => value,
            };
        });
    }

    /// <summary>Timer keys are named after their key id: also accept the title shown on the key.</summary>
    private string TimerByName(string arg)
    {
        var names = state.TimerNames();
        if (names.Contains(arg)) return arg;
        var profiles = config.Snapshot().Profiles;
        return names.FirstOrDefault(n => string.Equals(LiveStateStore.TimerTitle(profiles, n), arg, StringComparison.CurrentCultureIgnoreCase)) ?? arg;
    }

    /// <summary>Contents of a JSON string literal, without the quotes (the user writes "…{ora}…").</summary>
    private static string JsonEncode(string s)
    {
        var quoted = JsonSerializer.Serialize(s);
        return quoted[1..^1];
    }

    private static string StripNewlines(string s) => s.Replace("\r", " ").Replace("\n", " ");

    private static string ReadClipboard()
    {
        // The clipboard needs an STA thread.
        string text = "";
        var t = new Thread(() =>
        {
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
            catch { /* busy clipboard: empty text */ }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(2000);
        return text.Length > 4000 ? text[..4000] : text;
    }
}
