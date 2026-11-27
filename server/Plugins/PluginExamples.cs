using System.Text;

namespace PadNook.Plugins;

/// <summary>Writes the guide and an example plugin the first time the plugins folder is created.</summary>
internal static class PluginExamples
{
    public static void Write(string root)
    {
        try
        {
            File.WriteAllText(Path.Combine(root, "PLUGIN.md"), Guide, new UTF8Encoding(false));
            var dir = Path.Combine(root, "esempio");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "plugin.json"), Manifest, new UTF8Encoding(false));
            // PowerShell 5 needs the BOM to read accented characters correctly.
            File.WriteAllText(Path.Combine(dir, "notifica.ps1"), Notify, new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(dir, "disco.ps1"), Disk, new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(dir, "acceso-da.ps1"), Uptime, new UTF8Encoding(true));
        }
        catch (Exception ex) { Core.Log.Error("Unable to write the example plugin", ex); }
    }

    private const string Manifest = """
{
  "id": "esempio",
  "name": "Esempio",
  "version": "1.0.0",
  "author": "PadNook",
  "description": "Plugin di esempio: copialo e modificalo per creare le tue azioni.",
  "actions": [
    {
      "id": "notifica",
      "name": "Mostra una notifica sul PC",
      "fields": [
        { "id": "titolo", "label": "Titolo", "type": "text", "default": "PadNook" },
        { "id": "messaggio", "label": "Messaggio", "type": "textarea", "default": "Ciao dal telefono!" }
      ],
      "run": {
        "file": "powershell.exe",
        "args": ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "notifica.ps1", "-Titolo", "{titolo}", "-Messaggio", "{messaggio}"]
      }
    }
  ],
  "sources": [
    {
      "id": "disco",
      "name": "Spazio libero su C:",
      "intervalSec": 60,
      "run": { "file": "powershell.exe", "args": ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "disco.ps1"] }
    },
    {
      "id": "acceso-da",
      "name": "PC acceso da",
      "intervalSec": 60,
      "run": { "file": "powershell.exe", "args": ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "acceso-da.ps1"] }
    }
  ]
}
""";

    private const string Notify = """
param([string]$Titolo = 'PadNook', [string]$Messaggio = '')
Add-Type -AssemblyName System.Windows.Forms
$n = New-Object System.Windows.Forms.NotifyIcon
$n.Icon = [System.Drawing.SystemIcons]::Information
$n.Visible = $true
$n.ShowBalloonTip(5000, $Titolo, $Messaggio, 'Info')
Start-Sleep -Seconds 6
$n.Dispose()
""";

    private const string Disk = """
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$d = Get-PSDrive -Name C
$free = [math]::Round($d.Free / 1GB)
$total = [math]::Round(($d.Used + $d.Free) / 1GB)
@{ text = "$free GB"; value = $free; sub = "liberi su $total GB" } | ConvertTo-Json -Compress
""";

    private const string Uptime = """
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$u = (Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
'{0}g {1}h {2}m' -f $u.Days, $u.Hours, $u.Minutes
""";

    public const string Guide = """
# Plugin di PadNook

Ogni sottocartella di questa cartella che contiene un file `plugin.json` è un plugin.
Dopo aver aggiunto o modificato un plugin: menu dell'icona di PadNook → «Ricarica plugin»
(oppure nell'editor: pannello del layout → Plugin → Ricarica).

Un plugin può aggiungere:

- **azioni**: compaiono nell'editor dei tasti, nel tipo di azione «Plugin». Alla pressione PadNook
  avvia un programma o uno script, passando i valori che hai inserito nell'editor;
- **sorgenti dinamiche**: compaiono nei tasti «Dinamico». PadNook avvia il programma ogni
  `intervalSec` secondi e mostra sul tasto quello che stampa.

## plugin.json

```json
{
  "id": "mio-plugin",                 // lettere, numeri, . _ -   (unico)
  "name": "Il mio plugin",
  "version": "1.0.0",
  "author": "Io",
  "description": "Cosa fa",
  "actions": [
    {
      "id": "saluta",
      "name": "Saluta",
      "fields": [
        { "id": "nome", "label": "Nome", "type": "text", "default": "Mondo" },
        { "id": "volte", "label": "Quante volte", "type": "number", "default": 1, "min": 1, "max": 10 },
        { "id": "forte", "label": "Ad alta voce", "type": "checkbox", "default": false },
        { "id": "lingua", "label": "Lingua", "type": "select", "default": "it",
          "options": [["it", "Italiano"], ["en", "Inglese"]] },
        { "id": "note", "label": "Note", "type": "textarea" }
      ],
      "run": {
        "file": "powershell.exe",
        "args": ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "saluta.ps1", "-Nome", "{nome}"],
        "hidden": true,        // nasconde la finestra (predefinito: true)
        "wait": false,         // true = aspetta la fine e mostra l'errore se il codice di uscita non è 0
        "timeoutSec": 30
      }
    }
  ],
  "sources": [
    {
      "id": "ora",
      "name": "Ora del server",
      "intervalSec": 30,
      "run": { "file": "ora.cmd" }
    }
  ]
}
```

- `file`: programma da avviare. Se è un percorso relativo e il file esiste nella cartella del plugin,
  viene usato quello; altrimenti viene cercato nel PATH (es. `powershell.exe`, `python.exe`, `node.exe`).
- `args`: argomenti, uno per elemento (nessun problema di virgolette). `{id-campo}` viene sostituito
  con il valore del campo, `{pluginDir}` con la cartella del plugin.
- Variabili d'ambiente: ogni campo è disponibile anche come `PADNOOK_<ID>` (es. `PADNOOK_NOME`),
  più `PADNOOK_PLUGIN_DIR` e `PADNOOK_VERSION`.
- La cartella di lavoro è quella del plugin.
- I file `.json` possono contenere commenti `//`.

## Output delle sorgenti

Il programma stampa una riga di testo (es. `23 GB`) oppure un oggetto JSON su una riga:

```json
{ "text": "23 GB", "value": 23, "state": true, "sub": "liberi su 500 GB", "icon": "💾" }
```

- `text`: testo mostrato (segnaposto `{text}` nell'etichetta del tasto);
- `value`: numero, usato dalle regole «maggiore di / minore di» e da `{value}`;
- `state`: vero/falso, usato dalle regole «è attivo / non è attivo»;
- `sub`: testo secondario (`{sub}`); `icon`: emoji mostrata se il tasto usa l'icona della sorgente.

Se il testo è un numero (anche con `%` finale), viene usato anche come `value`.

## Sicurezza

I plugin sono programmi come gli altri: installa solo quelli di cui ti fidi.
Possono essere usati solo da chi ha il PIN della modalità modifica per configurare i tasti.
""";
}
