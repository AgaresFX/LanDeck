# PadNook

Un macro pad touch per il PC Windows: un pannello di tasti su iPhone, iPad o Android che esegue azioni sul PC attraverso la rete Wi-Fi di casa. Open source (licenza MIT): vedi [LICENSE](../LICENSE) e [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

- **Server Windows** (`server/`): un solo `PadNook.exe` con icona nell'area di notifica. Esegue le azioni e serve la webapp.
- **Webapp** (`webapp/`): React + TypeScript. Si apre da Safari o dal browser del PC e si può aggiungere alla schermata Home.

## Compilare l'exe

Requisiti (solo sul PC dove compili, non su quello dove gira l'exe):

- [.NET 10 SDK](https://dot.net)
- [Node.js](https://nodejs.org) LTS

Dalla cartella del progetto, in PowerShell:

```powershell
.\build.ps1
```

Lo script compila la webapp, la incorpora nel server e produce **`dist\PadNook.exe`**: un unico file, che non richiede .NET installato. Con `.\build.ps1 -SkipWeb` riusa l'ultima compilazione della webapp.

## Avviarlo

1. Copia `PadNook.exe` dove preferisci (per esempio `C:\Programmi\PadNook\` o `%LOCALAPPDATA%\PadNook\`) e avvialo.
2. Al primo avvio:
   - chiede di creare una regola in **Windows Firewall**: conferma la richiesta di amministratore (UAC). La regola accetta solo connessioni dalla rete locale;
   - si attiva l'**avvio automatico con Windows** (si toglie dal menu dell'icona);
   - si apre la finestra con **indirizzo, QR code e codice di accoppiamento**.
3. Inquadra il QR code con la fotocamera dell'iPhone o dell'iPad e tocca il link: il dispositivo viene associato e mostra subito i tasti.

Menu dell'icona nell'area di notifica (clic destro):

| Voce | Cosa fa |
|---|---|
| Mostra indirizzo e QR code… | Finestra per associare un nuovo dispositivo (anche con un clic sinistro sull'icona) |
| Apri editor nel browser | Apre la webapp sul PC, già associata, direttamente in modalità modifica |
| Dispositivi associati | Elenco dei dispositivi, con «Revoca accesso…» |
| Avvia con Windows | Attiva o disattiva l'avvio automatico |
| Configura firewall… | Ricrea la regola del firewall |
| Cambia porta… | Imposta un'altra porta e riavvia PadNook |
| Apri cartella dei dati | Apre `%APPDATA%\PadNook` |
| Esci | Chiude PadNook |

Se avvii l'exe una seconda volta, non parte un'altra copia: si riapre la finestra del QR code.

### Dati

Tutto si trova in `%APPDATA%\PadNook\`:

- `config.json`: profili, pagine, tasti, porta;
- `security.json`: dispositivi associati (solo l'hash dei token) e hash del PIN;
- `state.json`: stato ON/OFF dei tasti interruttore;
- `images\`: immagini di icone e sfondi;
- `logs\padnook.log`: log degli errori.

La **porta** predefinita è 8765. Al primo avvio, se è già occupata, PadNook sceglie da solo la prima porta libera successiva (8766, 8767…). Puoi cambiarla dal menu dell'icona con **Cambia porta…**: PadNook si riavvia e chiede di aggiornare la regola del firewall. Sui dispositivi va poi aperto il nuovo indirizzo (inquadra di nuovo il QR code): l'associazione resta valida.

### Aggiornare PadNook

1. Menu dell'icona → **Esci**.
2. Sostituisci `PadNook.exe` con la nuova versione, nella stessa cartella, e avvialo. Configurazione, dispositivi, PIN e immagini restano in `%APPDATA%\PadNook` e non vengono toccati.
3. Su iPhone e iPad chiudi e riapri l'app (oppure menu del pallino → **Ricarica**) per caricare la nuova webapp.

Se sposti l'exe in un'altra cartella, al primo avvio PadNook chiede di nuovo di aggiornare la regola del firewall e l'avvio automatico va riattivato dal menu.

### Disinstallare

1. Menu dell'icona: togli **Avvia con Windows**, poi **Esci**.
2. Cancella `PadNook.exe` e la cartella `%APPDATA%\PadNook`.
3. Togli la regola del firewall, da PowerShell come amministratore:

   ```powershell
   netsh advfirewall firewall delete rule name="PadNook"
   ```

## Aggiungere la webapp alla schermata Home (iPhone/iPad)

1. Dopo aver associato il dispositivo da Safari, compare la schermata «Usala a schermo intero» con un **codice a 6 cifre**.
2. Tocca **Condividi** (il quadrato con la freccia) → **Aggiungi alla schermata Home** → **Aggiungi**.
3. Apri PadNook dall'icona e inserisci il codice.

Il passaggio 3 serve perché iOS tiene i dati dell'app sulla schermata Home separati da quelli di Safari. Se il codice scade, ne trovi uno nuovo nella finestra del QR code sul PC (vale una sola volta e per 5 minuti).

L'app così si apre a schermo intero, senza barre di Safari, in verticale e in orizzontale.

### Schermo sempre acceso

Su `http://` iOS non permette alle pagine web di impedire lo spegnimento dello schermo: la Wake Lock API e i service worker richiedono HTTPS. Le soluzioni, dalla più affidabile:

- **Impostazioni → Schermo e luminosità → Blocco automatico → Mai** (consigliata se l'iPad resta in carica sulla scrivania).
- **Accesso Guidato** (Impostazioni → Accessibilità → Accesso Guidato): blocca l'iPad sulla sola app PadNook. Nelle sue opzioni ha un proprio «Blocco automatico», che puoi mettere su «Mai».
- **Menu del pallino → «Mantieni lo schermo acceso (sperimentale)»**, attivabile per singolo dispositivo:
  - su iPhone e iPad usa il trucco di NoSleep.js (un video muto in loop, invisibile). Si avvia al primo tocco dopo l'apertura e si riattiva al primo tocco quando torni nell'app; non interrompe la musica di altre app;
  - nel browser del PC usa la vera Wake Lock API;
  - su alcune versioni di iOS può non funzionare, per questo resta «sperimentale».

## Uso

- **Modalità Uso**: solo la griglia di tasti. Un tasto agisce nell'istante in cui lo tocchi, senza aspettare il rilascio.
- **Menu**: tieni premuto il pallino in alto a destra (sul PC: clic destro). Da qui cambi profilo o pagina, entri in modifica, attivi lo schermo sempre acceso e ricarichi.
- **Sul PC**: «Apri editor nel browser» dal menu dell'icona apre direttamente la modalità modifica (chiede il PIN). Nel browser del PC c'è anche il pulsante **✎ Modifica** in alto a destra; su iPhone e iPad non compare, per lasciare libera la griglia.
- **Pallino**: verde tenue = connesso, giallo = connessione in corso, rosso = disconnesso. La riconnessione è automatica: mentre il server non è raggiungibile resta visibile l'ultimo layout.
- **Modalità Modifica**: è protetta da un PIN di 4–8 cifre, impostato al primo accesso.
  - Tocca una cella vuota **+** per creare un tasto, oppure un tasto per modificarne testo, emoji, colori, azione e dimensione.
  - Senza tasti selezionati, il pannello regola il layout, lo sfondo e il profilo predefinito del dispositivo.
  - Ogni modifica, anche mentre trascini un cursore, compare subito su tutti i dispositivi collegati.

### Pagine, cartelle e profili

- **Pagine**: nella barra sotto quella dell'editor trovi le pagine del profilo; «+ Pagina» ne aggiunge una. Nel pannello, scegliendo «Solo «pagina»», puoi rinominarla, spostarla ed eliminarla. In modalità Uso passi da una pagina all'altra con un tasto «Navigazione → Vai a una pagina» oppure dal menu del pallino.
- **Cartelle**: nel pannello del tasto scegli **Tipo → Cartella**. Viene creata una sotto-griglia con già un tasto «Indietro»; «Apri cartella ›» la apre per modificarla. Le cartelle possono contenere altre cartelle. Eliminando il tasto si elimina anche il suo contenuto.
- **Profili**: nel pannello «Tutto il profilo» trovi **+ Nuovo**, **Duplica** (copia tutto: pagine, cartelle, tasti) ed **Elimina**. Il profilo si cambia dal menu a tendina in alto o dal menu del pallino.

### Spostare, copiare, duplicare

- **Trascina** un tasto in modalità modifica per spostarlo: la cella di arrivo si illumina. Su una cella vuota il tasto si sposta; sopra un altro tasto i due si scambiano di posto. La posizione vale per l'orientamento che stai modificando (verticale o orizzontale).
- **Copia / Duplica** nel pannello del tasto. Dopo una copia, senza tasti selezionati compare «Incolla tasto copiato», che funziona anche in un'altra pagina o in un altro profilo. Il tasto finisce nella prima cella libera.
- Copiando o duplicando una **cartella** si copia tutto il suo contenuto.
- Sul PC funzionano anche le scorciatoie **Ctrl+C**, **Ctrl+V**, **Ctrl+D** (duplica), **Canc** (elimina) ed **Esc** (deseleziona).

### Esportare e importare un profilo

Nel pannello «Tutto il profilo»:
- **⬇ Esporta su file** salva un file `NomeProfilo.padnook.json` con il profilo completo e le immagini incluse.
- **⬆ Importa da file…** lo aggiunge come nuovo profilo, senza sovrascrivere quelli esistenti.

Va bene per backup e per spostare i layout su un altro PC. Conviene farlo dal browser del PC: su iPhone e iPad il download dei file da una pagina `http://` funziona solo in parte.

### Dispositivi

Nel pannello «Tutto il profilo», la sezione **Dispositivi** elenca tutti i dispositivi associati: pallino verde se connessi, nome modificabile e **profilo da mostrare all'apertura** per ciascuno. C'è anche **Revoca**, che disconnette subito il dispositivo, come dal menu dell'icona.

### Aspetto dei tasti

- **Testo**: dimensione, colore, posizione (in alto, al centro, in basso, nascosto) e grassetto.
- **Icona**:
  - **Emoji**;
  - **Icona**: oltre 1.500 icone Lucide, con ricerca in inglese e alcune parole italiane come «cartella», «luce», «microfono»;
  - **Immagine**: caricata dal telefono o dal PC, ridotta automaticamente a 512 px.

  Si possono regolare colore (solo per le icone Lucide) e dimensione.
- **Sfondo del tasto**: colore e/o immagine.
- **Sfondo della pagina**: nel profilo (colore e/o immagine, con «Riempi», «Adatta» o «Affianca»), oppure diverso per una singola pagina.

Le immagini sono salvate in `%APPDATA%\PadNook\images\` (PNG, JPEG, GIF, WebP; le GIF restano animate). Un'immagine non più usata resta nella cartella: puoi cancellarla a mano, oppure lasciarla.

### Layout

- **Tutto il profilo / Solo questa pagina**: in alto nel pannello scegli se stai modificando i valori predefiniti del profilo o solo la pagina corrente.
  - Un valore cambiato solo per la pagina mostra il badge «Solo questa pagina · ↺»: toccalo per tornare al valore del profilo.
  - «Ripristina tutto» elimina tutte le personalizzazioni della pagina.
- **Griglia**: righe e colonne separate per verticale e orizzontale.
- **Tasti**: quadrati oppure che riempiono lo spazio, raggio degli angoli e dimensione minima. Se lo schermo è troppo piccolo, i tasti non scendono sotto la dimensione minima e la griglia scorre. Un singolo tasto può avere angoli propri (sezione Sfondo del tasto).
- **Spazio tra i tasti**: uguale oppure separato tra orizzontale e verticale.
- **Margini**: uguali oppure diversi per ogni lato. Si aggiungono alle aree riservate di notch e barra Home.
- **Allineamento**: dove sta la griglia (9 posizioni) quando non riempie lo schermo.
- **Tasti più grandi**: nel pannello del tasto, «Dimensione» lo estende su più celle (2×1, 2×2…). Le celle libere restano vuote.
- **Verticale/Orizzontale nella barra in alto**: modifica la griglia dell'altro orientamento senza ruotare il dispositivo (utile per preparare dal PC il layout verticale dell'iPhone). Le posizioni dei tasti sono memorizzate separatamente per i due orientamenti.

### Azioni disponibili

| Azione | Note |
|---|---|
| Scorciatoia da tastiera | Ctrl/Alt/Shift/Win + un tasto. «Registra dalla tastiera» la cattura dal PC o dalla tastiera dell'iPad. I tasti vengono inviati per posizione fisica. |
| Apri programma, file, cartella o URL | Per esempio `notepad.exe`, `C:\Giochi\gioco.exe`, `https://…`, `%USERPROFILE%\Documents`. Accetta argomenti facoltativi. |
| Multimedia, volume e microfono | Play/pausa, avanti, indietro, stop. Volume generale su/giù/imposta/muto. **Volume di una singola app** (su/giù/imposta/muto, es. `spotify`, `discord`: l'app deve aver già riprodotto un suono). **Muto del microfono** (alterna, sempre muto, sempre attivo). **Cambio dell'uscita audio**: con un dispositivo passa a quello, con più dispositivi li alterna (es. cuffie ↔ casse). |
| Soundboard | Riproduce un file audio del PC (mp3, wav, wma, m4a) con volume proprio. Se il suono è già in riproduzione: ricomincia, si ferma o suona sopra. «Ferma tutti i suoni». |
| Sistema | Blocco, sospensione, ibernazione, spegnimento e riavvio (con ritardo e conferma sul dispositivo), annulla spegnimento, disconnetti. Screenshot di tutti gli schermi, dello schermo principale o della finestra attiva, salvato in PNG (`Immagini\PadNook`) e/o copiato negli appunti; cattura di un'area. Desktop virtuali: successivo, precedente, nuovo, chiudi, visualizzazione attività, mostra desktop. Luminosità su/giù/imposta (monitor esterni via DDC/CI, schermo del portatile via WMI). |
| Gestione finestre | Sulla finestra attiva o su quella di un'app: porta in primo piano (ed eventualmente avvia l'app se non è aperta), massimizza, riduci, ripristina, sposta in una metà/quarto/terzo dello schermo o al centro, sposta sullo schermo successivo, sempre in primo piano, chiudi. |
| Contatore | Aumenta, diminuisci, azzera o imposta un contatore con nome (condiviso fra tasti e dispositivi). |
| Timer / cronometro | Avvia, pausa, azzera un conto alla rovescia o un cronometro con nome. |
| OBS Studio / Streamlabs Desktop | Avvia/ferma diretta e registrazione, pausa registrazione, salva replay, avvia/ferma replay buffer, cambia scena, muto di una sorgente, videocamera virtuale. In alto si sceglie il programma; Streamlabs Desktop non permette, dalla sua API, la pausa della registrazione e la videocamera virtuale. |
| Streamlabs (alert) | Alert personalizzato nell'Alert Box di Streamlabs (stile follow/abbonamento/donazione/host, messaggio, immagine, suono, durata); azzera il totale donazioni. |
| Lightroom Classic | Sviluppo: + / − con passo configurabile, imposta valore e azzera per esposizione, contrasto, luci, ombre, bianchi, neri, temperatura, tinta, vividezza, saturazione, texture, chiarezza, foschia; azzera tutto, tono automatico; copia/incolla/sincronizza impostazioni, applica un preset, prima/dopo, annulla/ripeti. Libreria: stelle, bandierine, etichette colore, foto avanti/indietro, cambio modulo (vedi «Lightroom Classic»). |
| Twitch | Marker, clip (link negli appunti del PC), titolo/categoria/tag, pubblicità, messaggio e annuncio in chat, shoutout, raid e annulla raid, modalità chat (slow, solo follower, solo sub, solo emote), sondaggi e pronostici (vedi «Twitch»). |
| Richiesta HTTP / webhook | URL, metodo, header, corpo (JSON, testo o form) e timeout, con variabili e credenziali cifrate. Modelli pronti: Discord, ntfy.sh, Telegram, Home Assistant, Shelly, GET generica (vedi «Richieste HTTP e webhook»). |
| Plugin | Le azioni aggiunte dai plugin installati (vedi «Plugin»). |
| Scrivi un testo | Scrive il testo nella finestra attiva, con qualsiasi carattere (accenti, emoji). A capo e tabulazioni diventano Invio e Tab. |
| Comando o script | Prompt (cmd) o PowerShell. Con «Nascondi la finestra» gira in background; altrimenti la finestra resta aperta per leggere il risultato. Cartella di lavoro facoltativa. |
| Macro | Sequenza di azioni, con un'attesa in millisecondi dopo ogni passo. I passi si riordinano con ↑ ↓. Se un passo fallisce, la macro si ferma e il tasto mostra l'errore. |
| Navigazione | Indietro, pagina iniziale, vai a una pagina, cambio profilo |

### Tipi di tasto

- **Azione**: esegue la sua azione nell'istante del tocco.
- **Interruttore**: alterna ON e OFF a ogni tocco.
  - Ha un'azione per l'attivazione e, se vuoi, una diversa per la disattivazione (altrimenti esegue la stessa).
  - Nell'aspetto ON puoi cambiare testo, colore di sfondo e icona; un puntino verde in alto a sinistra segnala lo stato ON.
  - Lo stato è condiviso da tutti i dispositivi e resta salvato anche se riavvii il PC (`state.json`).
  - Dall'editor, in «Stato attuale», puoi impostarlo a mano senza eseguire azioni, per esempio se si è disallineato.
- **Dinamico**: mostra informazioni in tempo reale e cambia aspetto in base allo stato (vedi «Tasti dinamici»). Può avere un'azione come un tasto normale.
- **Timer**: conto alla rovescia o cronometro sul tasto (tocco = avvia/ferma, tieni premuto = azzera); ogni tasto ha il suo timer.
- **Slider**: trascina il dito per regolare volume, microfono, volume di un'app, luminosità o un cursore di Lightroom; un tocco mette in muto (o azzera il cursore di Lightroom).
- **Cartella**: apre una sotto-griglia (vedi «Pagine, cartelle e profili»).

Su un tasto Azione con muto del microfono, muto, volume di un'app, uscita audio, OBS, timer o contatore compare **«✨ Mostra lo stato sul tasto»**: lo trasforma in un tasto dinamico che mostra lo stato di quella stessa azione (es. icona del microfono barrata e sfondo rosso quando è muto).

### Tasti dinamici

Sorgenti disponibili: orologio e data, timer/cronometro, contatore, uso CPU e RAM, app in primo piano, volume generale, microfono, volume di un'app, dispositivo di uscita, meteo (Open-Meteo, località cercata dall'editor, aggiornato ogni 15 minuti), stato di OBS e di Streamlabs Desktop (diretta, registrazione, replay, scena, muto di una sorgente), Streamlabs (ultima donazione, ultimo evento, totale donazioni della sessione), richiesta HTTP (un valore da un JSON o il codice di stato), Lightroom Classic (valore di un parametro, es. «Esposizione +0,35», stelle, bandierina, etichetta, modulo), Twitch (in diretta, spettatori, follower, abbonati, durata della diretta, titolo, ultimo follower/sub/raid/bits/riscatto/clip, modalità chat) e le sorgenti dei plugin.

- **Modelli pronti**: Orologio, Data, CPU, RAM, Microfono, Volume, Volume app, Uscita audio, Meteo, Timer, Cronometro, Contatore, OBS diretta/registrazione/scena. Impostano tutto in un tocco (anche l'azione: es. il Timer parte/si ferma col tocco e si azzera tenendo premuto).
- **Testo**: l'etichetta è un modello: `{text}` è il valore, `{value}` il numero, `{sub}` il dettaglio (per il meteo anche `{range}`, `{humidity}`, `{feels}`, `{wind}`). Si può andare a capo, es. `CPU` ⏎ `{text}`.
- **Regole**: «quando è attivo / non attivo» (es. in diretta, microfono muto, timer in corso), «valore maggiore / minore / uguale a», «testo contiene» e, per le richieste HTTP, «codice HTTP» (`200`, `2xx`, `4xx`…, `0` = nessuna risposta). La prima regola vera cambia colore di sfondo, testo e/o icona: es. rosso quando sei in diretta, rosso quando la CPU supera l'80%.

### Profili automatici

Nel pannello del profilo, «Attivazione automatica»: aggiungi i programmi (es. `photoshop`, `obs64`, il gioco) e, quando uno di questi è in primo piano sul PC, i dispositivi passano da soli a quel profilo; quando lo lasci tornano al profilo di prima. Scegliere un profilo a mano interrompe il cambio automatico; mentre stai modificando i tasti non cambia nulla. Si può disattivare per ogni dispositivo dal menu (sezione «Profili automatici»).

### OBS Studio

PadNook si collega a OBS sullo stesso PC (127.0.0.1, porta 4455) con il server WebSocket integrato in OBS 28 e successivi: in OBS, *Strumenti → Impostazioni server WebSocket → Abilita*. Se è impostata una password, inseriscila nell'editor (pannello del profilo → OBS Studio). Non serve nessuna regola del firewall.

### Lightroom Classic

PadNook include un plugin per Lightroom Classic, scritto da zero (MIDI2LR è servito solo come riferimento).

**Installazione.**
1. Nel pannello del profilo → «Lightroom Classic» premi «Installa plugin Lightroom». La cartella `PadNook.lrplugin` viene copiata in `%APPDATA%\Adobe\Lightroom\Modules`.
2. Riavvia Lightroom.
3. Se il plugin non compare in *File → Gestione plug-in*, premi «Aggiungi» e scegli quella cartella (con «Apri cartella» la trovi subito).

**Collegamento.** Il plugin ascolta solo su 127.0.0.1: porta 58873 per i comandi, 58874 per lo stato. Sono diverse da quelle di MIDI2LR, quindi i due possono convivere. PadNook si ricollega da solo ogni 3 s: se chiudi e riapri Lightroom, i tasti mostrano «Lightroom non connesso» e poi tornano attivi. Non serve nessuna regola del firewall.

**Azioni e tasti.**
- Azioni: vedi la tabella sopra.
- I comandi di Sviluppo passano da soli al modulo Sviluppo.
- «Sincronizza» incolla le impostazioni copiate su tutte le foto selezionate.
- Le etichette colore si alternano, come i tasti 6–9 di Lightroom.
- I tasti dinamici mostrano il valore attuale (`{sub}` = nome del parametro, `{text}` = valore, es. «Esposizione +0,35»), le stelle (★★★☆☆), la bandierina e l'etichetta della foto selezionata.
- I valori arrivano solo per i parametri usati dai tasti, controllati circa 7 volte al secondo.

**Slider sul telefono.** Nel tipo di tasto «Slider» scegli «Lightroom» e il parametro.
- Trascinando, il cursore di Lightroom si muove in modo fluido e il tasto mostra il valore nell'unità del parametro.
- Il telefono invia al massimo circa 16 valori al secondo; il PC inoltra solo l'ultimo, al massimo 25 volte al secondo.
- Un tocco riporta il parametro al valore predefinito.

**Profilo pronto.** Pannello Profilo → «+ Lightroom» crea il profilo «Lightroom Classic», che si attiva da solo quando Lightroom è in primo piano. Contiene due pagine:
- **Sviluppo**: 13 slider, più Annulla, Ripeti, Prima/dopo, Copia, Incolla (tieni premuto per sincronizzare), Auto (tieni premuto per azzerare tutto), Foto ← e →.
- **Libreria**: stelle, bandierine, etichette, frecce, stato della foto.

**Da sapere.**
- Il riferimento ufficiale delle API Lua di Adobe non era consultabile mentre scrivevo il plugin.
- Per i nomi dei parametri, il plugin prova sia `Exposure` sia `Exposure2012`.
- Copia/incolla, sincronizza e preset usano `photo:getDevelopSettings / applyDevelopSettings / applyDevelopPreset`. Se una funzione non esiste nella tua versione, il tasto mostra un errore invece di bloccare il plugin.

### Streamlabs Desktop

È un'alternativa a OBS e non usa obs-websocket. PadNook si collega in automatico attraverso il canale locale di Streamlabs Desktop (la «named pipe» `slobs`, attiva di serie): basta che Streamlabs sia aperto, senza token né regole del firewall.
- In alternativa, nel pannello del profilo → Streamlabs Desktop, scegli «WebSocket con token».
- In quel caso, in Streamlabs Desktop vai su Impostazioni → Remote Control, attiva i WebSocket (porta 59650) e copia il token.

Nelle azioni e nei tasti dinamici OBS, in alto si sceglie «Streamlabs Desktop» invece di «OBS Studio». Ci sono anche i modelli pronti Streamlabs diretta, registrazione e scena.
- Lo stato del muto delle sorgenti si aggiorna ogni 3 s, perché Streamlabs non lo comunica da sé.
- L'API di Streamlabs non permette di mettere in pausa la registrazione né di comandare la videocamera virtuale.

### Streamlabs (alert e donazioni)

Funziona anche se trasmetti con OBS. Nel pannello del profilo → «Streamlabs (alert e donazioni)» incolli i token dalla dashboard di Streamlabs (Impostazioni → API Settings → API Tokens):
- **Socket API Token**: serve per ricevere gli eventi.
- **Access Token**: serve solo per l'azione «Alert personalizzato».

I token restano cifrati sul PC (DPAPI). Il login OAuth non c'è perché Streamlabs richiede un client secret, che non può stare dentro l'exe.

**Eventi:**
- donazioni;
- follow, abbonamenti, bits e raid di Twitch;
- follow, membri e Super Chat di YouTube.

Come per Twitch, ogni regola può far lampeggiare un tasto e/o eseguire un'azione o una macro, con filtro «importo minimo» e le variabili `{utente}`, `{importo}`, `{messaggio}`. Se usi anche l'integrazione Twitch, crea le regole per gli eventi Twitch in un solo posto, altrimenti scattano due volte.

**Tasti dinamici:** ultima donazione (o Super Chat), ultimo evento, totale donazioni da quando PadNook è aperto. Il modello «Totale donazioni» si azzera tenendo premuto.

**Cosa non si può fare:** le API ufficiali v2.0 non permettono di saltare, mettere in pausa o silenziare la coda degli alert, quindi PadNook non lo fa.

### Twitch

**Registrare l'app (una volta sola).** Twitch chiede che ogni programma abbia il proprio Client ID.
1. Apri [dev.twitch.tv/console](https://dev.twitch.tv/console). Sull'account Twitch deve essere attiva l'autenticazione a due fattori.
2. Scegli «Register Your Application» e compila:
   - **Name**: un nome unico;
   - **OAuth Redirect URLs**: `http://localhost` (non viene usato, ma il campo è obbligatorio);
   - **Category**: Application Integration;
   - **Client Type**: **Public**.
3. Copia il **Client ID** e incollalo in PadNook, nel pannello del profilo → Twitch → «Client ID». Il Client Secret non serve: non generarlo.

**Login.** Premi «Accedi con Twitch»: sul PC si apre twitch.tv/activate con il codice già pronto, e basta confermare.
- PadNook non vede mai la password.
- I token restano cifrati sul PC con DPAPI: non arrivano ai telefoni e non entrano nei profili esportati.
- Il token si rinnova da solo e PadNook lo verifica ogni ora, come chiede Twitch.
- Il rinnovo di un client pubblico vale 30 giorni: dopo serve un nuovo «Accedi con Twitch», e il pannello lo segnala.
- Se il pannello indica permessi mancanti, esci e accedi di nuovo.

**Azioni.** Funzionano sul tuo canale.
- Marker e clip richiedono la diretta in corso.
- Pubblicità solo per affiliati e partner.
- Shoutout al massimo uno ogni 2 minuti, e lo stesso canale una volta all'ora.
- Raid: massimo 10 richieste ogni 10 minuti.
- Testi di chat, annunci e marker accettano le variabili (`{ora}`, `{contatore:Nome}`…).
- «Risolvi pronostico» indica l'esito vincente per numero (1 = il primo).

**Tasti dinamici.**
- Modelli pronti: Twitch in diretta, Spettatori, Follower, Ultimo follower, Durata diretta, Clip, Slow mode, Solo sub.
- Slow mode e Solo sub sono interruttori: il tasto mostra lo stato reale della chat e lo cambia quando lo premi.
- Spettatori e follower si aggiornano ogni 60 s, solo mentre un dispositivo è collegato.
- Il resto arriva in tempo reale dagli eventi.

**Eventi in tempo reale (EventSub).** Eventi seguiti:
- follow;
- abbonamento, abbonamenti regalati, rinnovo;
- raid ricevuto;
- bits;
- riscatto di premi punti canale;
- inizio e fine diretta;
- modifiche al canale e alle modalità chat.

In «Quando succede qualcosa» (pannello Twitch) ogni regola può:
- far lampeggiare un tasto su tutti i dispositivi, con colore e durata a scelta;
- eseguire un'azione, anche una macro.

Filtri disponibili: nome del premio riscattato (basta che lo contenga), bits minimi, spettatori minimi del raid, numero minimo di sub regalati. Nei testi dell'azione puoi usare `{utente}`, `{importo}`, `{premio}` e `{messaggio}`, per esempio un messaggio in chat «Grazie {utente} per {importo} bits!».

Gli eventi avvenuti mentre PadNook è chiuso non vengono recuperati.

### Richieste HTTP e webhook

**Credenziali.** Token, chiavi e URL segreti vanno salvati nel pannello del profilo, sezione «Credenziali (solo su questo PC)». Lì restano cifrati con DPAPI, la protezione dati di Windows legata al tuo utente. Nelle richieste li richiami con `{segreto:Nome}`.
- I profili contengono solo il nome della credenziale, quindi il valore non arriva mai ai telefoni e non finisce nei profili esportati.
- Se l'editor trova un token scritto in chiaro (webhook Discord, bot Telegram, `Bearer …`, `tk_…`), mostra un avviso.
- Anche la password di OBS è salvata in questo modo. Quella scritta in chiaro dalle versioni precedenti viene spostata da sola al primo avvio.

**Variabili** utilizzabili in URL, header e corpo:

| Variabile | Valore |
|---|---|
| `{ora}` | ora attuale (14:05) |
| `{data}` | data attuale (23/11/2026) |
| `{appunti}` | testo negli appunti del PC |
| `{contatore:Nome}` | valore del contatore |
| `{timer:Nome}` | tempo mostrato dal timer (nome o testo del tasto) |
| `{segreto:Nome}` | valore della credenziale |

Ogni valore viene adattato al punto in cui finisce: codificato nell'URL, reso sicuro dentro le stringhe JSON (le virgolette le scrivi tu, es. `"text": "Sono le {ora}"`), senza a capo negli header. Un corpo JSON non valido viene segnalato prima dell'invio.

**Azione «Richiesta HTTP».** Una risposta 4xx o 5xx mostra l'errore sul tasto (es. `HTTP 404: …`). «▶ Prova richiesta» invia la richiesta subito e mostra codice, durata e risposta. Modelli pronti:

| Modello | Cosa fa |
|---|---|
| Discord | `POST` all'URL del webhook, salvato come credenziale, con `content` (max 2000 caratteri). |
| ntfy.sh | `POST` del testo su `server/argomento`, con titolo, priorità e tag come parametri. Token facoltativo per gli argomenti protetti. |
| Telegram | `POST https://api.telegram.org/bot{segreto:Telegram}/sendMessage` con `chat_id` e `text`. |
| Home Assistant | `POST /api/services/<dominio>/<servizio>` con `entity_id` e `Authorization: Bearer` (token a lunga durata). |
| Shelly | Gen2+ con `/rpc/Switch.Toggle` o `Switch.Set`, Gen1 con `/relay/<n>?turn=…`. In rete locale, senza cloud. |
| GET generica | Apre un URL. |

**Tasto dinamico «Richiesta HTTP».** Ripete la richiesta ogni X secondi (minimo 5), solo mentre un dispositivo è collegato. Dalla risposta prende il valore indicato da un percorso, per esempio `current.temperature`, `items[0].name` o `state`; con il percorso vuoto usa tutta la risposta.
- `{text}`: il valore. I numeri sono formattati all'italiana e `true`/`false` diventano ON/OFF.
- `{sub}`: il codice HTTP.
- Lo stato vero/falso è il valore booleano estratto, oppure «risposta OK (2xx)».
- Dopo un'azione HTTP i tasti dinamici HTTP si aggiornano subito, così uno Shelly mostra il nuovo stato senza aspettare l'intervallo.

Modelli pronti per il tasto dinamico:
- **Valore da un JSON**.
- **Home Assistant**: stato di un'entità, con toggle facoltativo alla pressione.
- **Shelly**: verde quando è acceso; la pressione accende o spegne.
- **Sito raggiungibile**: verde con risposta 2xx, rosso con errore.

Le richieste partono dal PC, quindi non servono regole del firewall.

### Plugin

Chiunque può aggiungere azioni e sorgenti dinamiche senza ricompilare PadNook: ogni sottocartella di `%APPDATA%\PadNook\plugins` con un file `plugin.json` è un plugin. Il manifest descrive le azioni (con i campi da mostrare nell'editor: testo, numero, casella, elenco…) e il programma/script da avviare (PowerShell, Python, Node, un exe…), con i valori dei campi passati come argomenti `{campo}` e variabili d'ambiente `PADNOOK_CAMPO`. Le sorgenti stampano una riga di testo o un JSON `{"text", "value", "state", "sub", "icon"}`.

Al primo avvio PadNook crea in quella cartella la guida completa (`PLUGIN.md`) e un plugin di esempio (`esempio`: una notifica sul PC, spazio libero su C:, da quanto è acceso il PC). Dopo le modifiche: menu dell'icona → Plugin → Ricarica (oppure dall'editor).

**Pressione prolungata**: qualsiasi tasto può avere una seconda azione da eseguire tenendolo premuto (durata regolabile, predefinita 0,5 s). Sui tasti che ce l'hanno, l'azione normale parte al rilascio invece che al tocco, perché finché il dito resta giù non si sa ancora quale delle due eseguire.

## Problemi comuni

**Il telefono non si collega / la pagina non si apre**
- PC e telefono devono essere sulla **stessa rete Wi-Fi**. Le reti "ospiti" e alcune reti mesh isolano i dispositivi tra loro: disattiva l'isolamento dei client nel router o usa la rete principale.
- Il telefono non deve passare per una VPN o per i dati cellulari. Su iOS disattiva anche "Relay privato iCloud" per quella rete, se attivo.
- Prova ad aprire l'indirizzo (`http://IP:PORTA`) dal browser del PC stesso: se lì funziona, il problema è la rete o il firewall.

**Firewall**
- Dal menu dell'icona scegli **Configura firewall…** e conferma la richiesta di amministratore.
- Controlla in *Windows Defender Firewall → Impostazioni avanzate → Regole connessioni in entrata* che ci sia la regola **PadNook** e che **non** ci siano regole di blocco per `PadNook.exe`. Se in passato hai cliccato "Annulla" sull'avviso del firewall, Windows ha creato regole di blocco: eliminale.
- Se usi un antivirus con un proprio firewall (Norton, Bitdefender, Kaspersky…), consenti PadNook anche lì.

**L'IP del PC è cambiato** (il router assegna un nuovo indirizzo)
- Apri la finestra del QR code: mostra sempre l'indirizzo attuale. Inquadra di nuovo il QR code, oppure apri il nuovo indirizzo in Safari e riaggiungilo alla schermata Home.
- Soluzione definitiva: nel router imposta un **IP fisso (prenotazione DHCP)** per il PC.
- Se il PC ha più schede di rete (Ethernet, Wi-Fi, VPN, macchine virtuali), nella finestra scegli dal menu **Rete** quella giusta.

**«Server non attivo» / porta occupata**
- Un altro programma usa la stessa porta: menu dell'icona → **Cambia porta…**, poi conferma l'aggiornamento della regola del firewall.
- Se PadNook non parte affatto, chiudilo, cambia `"port"` in `%APPDATA%\PadNook\config.json` e riavvialo.
- Dettagli sugli errori in `%APPDATA%\PadNook\logs\padnook.log`.

**I tasti non rispondono dopo un po' (iPhone/iPad)**
- iOS sospende le app in background e chiude la connessione. Al ritorno PadNook si ricollega da solo, di solito in meno di un secondo: il pallino rosso o giallo in alto a destra indica che la riconnessione è in corso.
- Se resta rosso, controlla che il PC non sia in sospensione e che PadNook sia avviato.

**Le modifiche non compaiono su iPhone/iPad dopo un aggiornamento**
- Chiudi del tutto l'app (scorri verso l'alto nel selettore app) e riaprila, oppure usa menu del pallino → **Ricarica**.

**Un'azione mostra un errore sul tasto**
- Il messaggio dice cosa non va (programma non trovato, comando vuoto, passo della macro fallito…). Un tasto che scuote e si colora di rosso significa che il comando non è arrivato al PC, cioè che il dispositivo non è connesso.

**La scorciatoia non fa nulla**
- Windows non permette di inviare tasti a una finestra avviata **come amministratore** (per esempio Gestione attività o alcuni giochi) se PadNook non è anch'esso avviato come amministratore.
- Con il PC bloccato i tasti non vengono inviati.
- Alcune combinazioni sono riservate al sistema (Ctrl+Alt+Canc, Win+L).

**«Dispositivo non riconosciuto»**
- Il dispositivo è stato revocato oppure `security.json` è stato cancellato. Associalo di nuovo con il QR code.

**Ho dimenticato il PIN**
- Chiudi PadNook, apri `%APPDATA%\PadNook\security.json`, sostituisci il blocco `"pin": { … }` con `"pin": null` e riavvia PadNook: al prossimo accesso alla modifica ti chiederà un nuovo PIN.

## Sicurezza

- **Associazione**: un dispositivo può inviare comandi solo dopo aver usato un codice a 6 cifre, monouso e valido 5 minuti, mostrato sul PC (anche dentro il QR code). In cambio riceve un token personale. Il PC conserva solo l'hash del token.
- **Solo rete locale**: il server rifiuta le connessioni che non arrivano da indirizzi privati (192.168.x.x, 10.x.x.x, 172.16–31.x.x, link-local). La regola del firewall accetta solo la sottorete locale.
- **Pagine di altri siti**: anche dallo stesso PC, un sito aperto nel browser non può usare il WebSocket di PadNook, perché le connessioni con un'origine diversa vengono rifiutate.
- **Modifica protetta da PIN** (memorizzato come hash PBKDF2), con blocco temporaneo dopo 5 tentativi errati.
- **Il dispositivo non decide cosa eseguire**: invia solo «ho premuto il tasto X», e l'azione viene letta dalla configurazione sul PC.
- **Revoca** dal menu dell'icona o dall'editor: il dispositivo viene disconnesso subito.

Attenzione: la connessione è `http://`, non cifrata. Va benissimo in una rete di casa fidata, non su reti pubbliche o condivise.

## Sviluppo

```powershell
# Server: dati in una cartella separata, niente firewall, niente avvio automatico,
# codice di accoppiamento fisso 111111 (solo con PADNOOK_DEV=1)
$env:PADNOOK_DEV = '1'; $env:PADNOOK_DEV_PAIRCODE = '111111'; $env:PADNOOK_DATA = "$PWD\.devdata"
dotnet run --project server

# Webapp con ricaricamento a caldo (proxy di /ws verso la porta 8765)
cd webapp; npm run dev
```

In modalità sviluppo apri `http://localhost:5173/#pair=111111`. Un'istanza con `PADNOOK_DEV=1` può girare insieme a quella installata: al primo avvio sceglie da sola una porta libera. Se il server usa una porta diversa da 8765, aggiorna il proxy in `webapp/vite.config.ts`.

Per provare Twitch senza un account, con `PADNOOK_DEV=1` puoi puntare a un finto server locale: `PADNOOK_DEV_TWITCH_ID` (sostituisce id.twitch.tv), `PADNOOK_DEV_TWITCH_API` (api.twitch.tv) e `PADNOOK_DEV_TWITCH_WS` (EventSub WebSocket). Per Streamlabs: `PADNOOK_DEV_SLOBS_PIPE` (nome della pipe), `PADNOOK_DEV_STREAMLABS_SOCKET` e `PADNOOK_DEV_STREAMLABS_API`. Per Lightroom: `PADNOOK_DEV_LR_PLUGIN_DIR` (cartella in cui installare il plugin).

### Struttura

```
server/
  Program.cs            avvio, istanza singola, composizione
  Config/               config.json (profili come JSON grezzo) e configurazione predefinita
  Security/             dispositivi, token, PIN, codici di accoppiamento
  Web/                  Kestrel, file statici incorporati, WebSocket (WsHub)
  Actions/              azioni come plugin: IActionHandler + ActionCatalog
  Platform/             SendInput, Core Audio, firewall, avvio automatico, IP locali
  Tray/                 icona di notifica e finestra del QR code
webapp/src/
  types.ts              schema della configurazione (riferimento)
  net/                  socket con riconnessione e logica del protocollo
  deck/                 griglia e tasti
  editor/               modalità modifica
  ui/                   menu, PIN, accoppiamento, avvisi
```

### Pubblicare una nuova versione

```powershell
.\release.ps1 -Version 1.2.1 -Notes "Novità: ..."
```

Imposta la versione, compila `dist\PadNook.exe`, calcola `PadNook.exe.sha256` e crea la release su GitHub (`AgaresFX/PadNook`) con i due file. Le copie installate la propongono entro 6 ore; senza il file `.sha256` l'aggiornamento non viene mai installato.

Installazione e aggiornamenti: `server/Core/Installer.cs` (copia in `%LOCALAPPDATA%\Programs\PadNook`, menu Start, voce in Impostazioni → App, `--install`, `--uninstall [--quiet]`) e `server/Core/Updater.cs`. Per i test: `PADNOOK_INSTALL_DIR` (cartella d'installazione di prova, senza avvio automatico) e `PADNOOK_UPDATE_URL` (JSON della release al posto dell'API di GitHub).

### Aggiungere un'azione

Per azioni personali basta un plugin (vedi «Plugin»). Per un'azione integrata:

1. Lato server: crea una classe che implementa `IActionHandler` (`Type` = nome dell'azione, `ExecuteAsync` riceve il JSON dell'azione) e aggiungila in `ActionCatalog.CreateBuiltins()`.
2. Lato webapp: aggiungi il tipo in `types.ts` e il relativo form in `editor/ActionEditor.tsx` (o `editor/ExtraEditors.tsx`).

I valori dei tasti dinamici arrivano da `server/Live/LiveService.cs` (messaggio `live` con `{ sorgente: { text, value, state, sub, icon } }`); l'app in primo piano dal messaggio `fg`.

### Protocollo WebSocket (`/ws`)

Ogni messaggio è JSON: `{ "t": "tipo", "rid"?: "...", ... }`.

- Client → server: `hello {token, editToken?}`, `pair {code, deviceName}`, `press {profileId, pageId, keyId, gesture}`, `pair.code.request`, `edit.unlock {pin}`, `edit.pin.set {pin}`, `edit.lock`, `edit.profile.put {profile}`, `edit.profile.delete {id}`, `edit.devices.list`, `edit.device.update {deviceId, name?, defaultProfileId?}`, `edit.device.revoke {deviceId}`, `edit.toggle.set {keyId, on}`, `edit.info`, `edit.secrets.list`, `edit.secrets.set {name, value}`, `edit.secrets.delete {name}` (risposta `secrets {names}`: solo i nomi), `edit.http.test {request}` (risposta `http.result`), `edit.slobs.set {enabled, mode, port, token?}`, `edit.streamlabs.info|set {socketToken?, accessToken?, enabled?, rules?, clearTokens?}|test`, `edit.lightroom.install|openFolder|presets`, `edit.twitch.info|set {clientId?, rules?}|login|cancel|logout|test|categories {q}` (risposta `twitch.info`), `ping`.
- Server → client: `flash {keyId, color, ms}` (lampeggio da una regola evento), `welcome`, `paired`, `profile.changed`, `profile.deleted`, `profile.saved`, `toggle.state {keyId, on}`, `press.result`, `edit.unlocked`, `edit.locked`, `devices`, `device.updated`, `pair.code`, `revoked`, `error {code, message}`, `pong`.

HTTP: `POST /api/images` (header `Authorization: Bearer <token>` e `X-Edit-Token`) carica un'immagine e restituisce `{id}`; `GET /images/<id>` la serve.

Il client non invia mai il contenuto di un'azione: il server la legge dalla propria configurazione, quindi anche un dispositivo associato può solo «premere» i tasti esistenti.
