using System.Text.Json.Nodes;

namespace PadNook.Config;

/// <summary>Configuration created on first run, with a few ready-to-use keys.</summary>
public static class DefaultConfig
{
    public static JsonObject Create()
    {
        var keys = new JsonArray
        {
            Key("k_playpause", L.T("Play/Pausa"), "⏯️", "#3b3f8f", """{"type":"media","command":"playPause"}"""),
            Key("k_prev", L.T("Indietro"), "⏮️", "#2b2f3a", """{"type":"media","command":"prev"}"""),
            Key("k_next", L.T("Avanti"), "⏭️", "#2b2f3a", """{"type":"media","command":"next"}"""),
            Key("k_voldown", "Volume -", "🔉", "#1f4d3a", """{"type":"media","command":"volDown","step":5}"""),
            Key("k_mute", L.English ? "Mute" : "Muto", "🔇", "#5a2a2a", """{"type":"media","command":"mute"}"""),
            Key("k_volup", "Volume +", "🔊", "#1f4d3a", """{"type":"media","command":"volUp","step":5}"""),
            Key("k_desktop", "Desktop", "🖥️", "#2b2f3a", """{"type":"hotkey","keys":["Win","KeyD"]}"""),
            Key("k_taskmgr", L.T("Gestione attività"), "📊", "#2b2f3a", """{"type":"hotkey","keys":["Ctrl","Shift","Escape"]}"""),
            Key("k_notepad", L.T("Blocco note"), "📝", "#2b2f3a", """{"type":"launch","target":"notepad.exe"}"""),
            Key("k_web", "Google", "🌐", "#2b2f3a", """{"type":"launch","target":"https://www.google.com"}"""),
        };

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["rev"] = 1,
            ["settings"] = new JsonObject { ["port"] = ConfigStore.DefaultPort },
            ["profiles"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "p_main",
                    ["name"] = L.T("Principale"),
                    ["layout"] = JsonNode.Parse("""
                        {
                          "portrait":  { "rows": 4, "cols": 3 },
                          "landscape": { "rows": 3, "cols": 5 },
                          "gapX": 12, "gapY": 12,
                          "margin": { "top": 16, "right": 16, "bottom": 16, "left": 16 },
                          "keyShape": "square",
                          "cornerRadius": 18,
                          "align": { "h": "center", "v": "center" },
                          "minKeySize": 56
                        }
                        """),
                    ["background"] = new JsonObject { ["color"] = "#0e0f13" },
                    ["pages"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "pg_home",
                            ["name"] = "Home",
                            ["kind"] = "page",
                            ["keys"] = keys,
                        },
                    },
                },
            },
        };
    }

    private static JsonObject Key(string id, string label, string emoji, string bg, string actionJson) => new()
    {
        ["id"] = id,
        ["span"] = new JsonObject { ["w"] = 1, ["h"] = 1 },
        ["pos"] = new JsonObject(),
        ["style"] = new JsonObject
        {
            ["label"] = new JsonObject { ["text"] = label, ["size"] = 13, ["color"] = "#ffffff", ["position"] = "bottom" },
            ["icon"] = new JsonObject { ["kind"] = "emoji", ["value"] = emoji },
            ["bg"] = new JsonObject { ["color"] = bg },
        },
        ["kind"] = "action",
        ["onPress"] = JsonNode.Parse(actionJson),
        ["onLongPress"] = null,
    };
}
