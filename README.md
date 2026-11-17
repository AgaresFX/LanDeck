# LanDeck

**Una "Stream Deck" personale sul tuo iPhone, iPad o qualsiasi browser.**
Un pannello di tasti sul telefono che esegue azioni sul PC Windows attraverso la rete Wi-Fi di casa: scorciatoie, programmi, volume, microfono, OBS, screenshot, macro e molto altro.

➡️ **[Scarica LanDeck.exe dall'ultima release](../../releases/latest)**

---

## Come funziona

```
 iPhone / iPad / browser  ──Wi-Fi di casa──►  LanDeck.exe sul PC  ──►  azioni su Windows
      (i tasti)                                (icona vicino all'orologio)
```

- **Sul PC** gira `LanDeck.exe`: un unico file, senza installazione e senza bisogno di .NET. Vive come icona nell'area di notifica (vicino all'orologio), esegue le azioni e serve l'interfaccia dei tasti.
- **Sul telefono** apri la pagina indicata da LanDeck in Safari (o in qualsiasi browser) e la aggiungi alla schermata Home: diventa un'app a tutto schermo.
- Funziona **solo nella rete locale**: niente account, niente cloud, nessun dato esce da casa (tranne il meteo, se lo usi).

## Requisiti

- PC con **Windows 10 o 11** (64 bit).
- Telefono/tablet sulla **stessa rete Wi-Fi** del PC.

## Installazione

1. Scarica `LanDeck.exe` dalla [pagina delle release](../../releases/latest) e avvialo (anche direttamente dalla cartella Download).
2. Scegli **«Installa LanDeck»**: il programma si copia in `%LOCALAPPDATA%\Programs\LanDeck`, crea il collegamento nel **menu Start**, la voce in **Impostazioni → App** e si avvia con Windows. Non servono permessi di amministratore. Il file scaricato si può poi cancellare.
   (Con «Avvia senza installare» funziona anche come programma portatile, ma senza aggiornamenti automatici.)
3. Al primo avvio:
   - chiede di creare una **regola del firewall di Windows** (serve l'autorizzazione di amministratore, una volta sola). La regola accetta solo connessioni dalla rete locale;
   - si apre una finestra con **indirizzo, QR code e codice di accoppiamento**.
4. Inquadra il QR code con la fotocamera del telefono e tocca il link: il dispositivo viene associato e mostra subito i tasti.
   In alternativa apri l'indirizzo mostrato (es. `http://192.168.1.10:8765`) e scrivi il codice a 6 cifre.
5. **iPhone/iPad**: in Safari tocca *Condividi → Aggiungi alla schermata Home* per averla come app a tutto schermo.

> Windows potrebbe mostrare l'avviso SmartScreen ("PC protetto") perché l'exe non è firmato: *Ulteriori informazioni → Esegui comunque*.

## Aggiornamenti automatici

LanDeck installato controlla le nuove versioni su GitHub **all'avvio e ogni 6 ore**. Quando ce n'è una, compare una notifica vicino all'orologio: cliccandola (o dal menu dell'icona → *Installa aggiornamento…*) vedi le novità e scegli **«Installa e riavvia»**.
LanDeck scarica la nuova versione, ne verifica l'integrità (SHA-256), sostituisce il programma e si riapre da solo in pochi secondi. Tasti, impostazioni, dispositivi associati e regola del firewall restano invariati.
Puoi controllare a mano dal menu dell'icona → *Cerca aggiornamenti*.

## Uso

- **Tocca un tasto** per eseguire la sua azione.
- **Menu**: tieni premuto il puntino di stato in alto (o clic destro) per cambiare profilo o pagina, attivare lo schermo sempre acceso o entrare in modifica.
- **Modifica**: dal menu → *Modifica layout*. La prima volta imposti un **PIN** (4–8 cifre), che protegge la modifica dei tasti. Puoi modificare anche dal PC: menu dell'icona → *Apri editor nel browser*.
- In modifica: tocca un tasto per configurarlo, trascinalo per spostarlo, tocca una cella vuota (+) per crearne uno. Ctrl+C / Ctrl+V / Ctrl+D / Canc funzionano dal PC.

### Organizzazione

- **Profili** (es. "Lavoro", "Giochi", "Streaming"), ognuno con più **pagine** e **cartelle**.
- **Griglia** separata per verticale e orizzontale, con righe, colonne, spazi, margini e allineamento regolabili.
- **Aspetto**: testo, emoji, migliaia di icone, immagini, colori e sfondi personalizzati.
- **Esporta/Importa** un profilo su file (immagini comprese).
- Più dispositivi insieme, ognuno con il suo profilo iniziale; accesso revocabile in qualsiasi momento.

### Tipi di tasto

| Tipo | Cosa fa |
|---|---|
| **Azione** | Esegue un'azione al tocco. Può averne una seconda tenendolo premuto. |
| **Interruttore** | Alterna ON/OFF a ogni tocco, con azioni e aspetto diversi per i due stati. |
| **Dinamico** | Mostra informazioni in tempo reale e cambia colore/icona in base allo stato. |
| **Cartella** | Apre una sotto-griglia di tasti. |

### Azioni disponibili

| Azione | Esempi |
|---|---|
| **Scorciatoia da tastiera** | Ctrl+Shift+S, Win+D… (si può registrare premendo i tasti) |
| **Apri programma, file, cartella o URL** | `notepad.exe`, un gioco, `https://…`, una cartella |
| **Scrivi un testo** | Scrive un testo nella finestra attiva (accenti ed emoji compresi) |
| **Multimedia e volume** | Play/pausa, tracce; volume generale su/giù/muto; **volume di una singola app** (es. Spotify, Discord) |
| **Microfono** | Muto/riattiva, con icona che mostra lo stato |
| **Uscita audio** | Passa da cuffie a casse (e viceversa) con un tocco |
| **Soundboard** | Riproduce file audio mp3, wav, wma, m4a |
| **Sistema** | Blocco, sospensione, spegnimento e riavvio (con conferma), screenshot, desktop virtuali, luminosità |
| **Gestione finestre** | Porta in primo piano, massimizza, riduci, affianca a metà/quarti, sposta sull'altro schermo, sempre in primo piano |
| **Comando o script** | Prompt dei comandi o PowerShell |
| **Macro** | Sequenza di azioni con attese tra un passo e l'altro |
| **Contatore e timer** | Contatori, conto alla rovescia e cronometro condivisi fra i dispositivi |
| **OBS Studio** | Diretta, registrazione, scene, muto delle sorgenti |
| **Navigazione** | Cambia pagina o profilo |
| **Plugin** | Azioni aggiunte da te o da altri (vedi sotto) |

### Tasti dinamici

Mostrano in tempo reale **orologio, data, CPU, RAM, meteo, timer, contatori, volume, microfono, uscita audio, stato di OBS** e i dati dei plugin.
Con le **regole** cambiano aspetto in base allo stato: rosso quando sei in diretta, icona barrata quando il microfono è muto, rosso quando la CPU supera l'80%…
Ci sono **modelli pronti** (Orologio, CPU, Microfono, Meteo, Timer, OBS diretta…) da applicare con un tocco.

### Profili automatici

Collega un profilo a uno o più programmi (Photoshop, OBS, un gioco…): quando quel programma è in primo piano sul PC, il telefono passa da solo a quel profilo, e torna a quello di prima quando lo lasci.

### OBS Studio

In OBS (versione 28 o successiva): *Strumenti → Impostazioni server WebSocket → Abilita*. Se imposti una password, inseriscila nell'editor di LanDeck (pannello del profilo → OBS Studio). La connessione resta sul PC.

### Plugin

Chiunque può aggiungere nuove azioni e tasti dinamici senza ricompilare nulla: ogni cartella in `%APPDATA%\LanDeck\plugins` con un file `plugin.json` è un plugin, che può usare PowerShell, Python, Node o qualsiasi programma.
Al primo avvio LanDeck crea lì una guida completa (`PLUGIN.md`) e un plugin di esempio. Dopo le modifiche: menu dell'icona → *Plugin → Ricarica*.

## Menu dell'icona sul PC

Clic destro sull'icona di LanDeck vicino all'orologio:

- **Mostra indirizzo e QR code** – per associare un nuovo dispositivo
- **Apri editor nel browser** – modifica i tasti dal PC
- **Dispositivi associati** – vedi e revoca i dispositivi
- **Avvia con Windows**, **Configura firewall**, **Cambia porta**
- **Apri cartella dei dati**, **Plugin**
- **Cerca aggiornamenti**, **Disinstalla LanDeck**, **Esci**

## Dati e disinstallazione

- Configurazione, immagini e plugin sono in `%APPDATA%\LanDeck`; il programma in `%LOCALAPPDATA%\Programs\LanDeck`.
- **Disinstallare**: *Impostazioni → App → LanDeck → Disinstalla* (oppure menu dell'icona → *Disinstalla LanDeck…*). Rimuove programma, collegamento, avvio automatico e regola del firewall; puoi scegliere se eliminare anche tasti e impostazioni.

## Problemi comuni

- **Il telefono non si collega**: PC e telefono devono essere sulla stessa rete Wi-Fi (non quella "ospiti"); controlla che la regola del firewall esista (menu dell'icona → *Configura firewall…*).
- **I tasti non funzionano in un programma avviato come amministratore**: Windows blocca l'invio di tasti da programmi normali verso quelli con privilegi elevati.
- **Con il PC bloccato** le scorciatoie e il testo non possono essere inviati.
- **Volume di un'app**: l'app compare solo dopo aver riprodotto almeno un suono.
- **Luminosità di un monitor esterno**: attiva l'opzione "DDC/CI" nel menu del monitor.

## Sicurezza

- Accetta connessioni **solo dalla rete locale**.
- Ogni dispositivo va **associato** con un codice temporaneo e può essere revocato.
- La **modifica dei tasti** (e quindi comandi, script e plugin) è protetta dal PIN.
