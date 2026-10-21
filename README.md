# PantePlayer

Lettore video desktop per Windows ottimizzato per Ryzen 9000 + RTX 5080:

* attivazione automatica di NVIDIA RTX Video Super Resolution se il video è sotto i 1080p

* riproduce in sequenza tutti i video di una cartella, con playlist laterale.

* codice ottimizzato per sfruttare istruzioni moderne

## Come si usa

```bash
dotnet run --project "src/LettoreVideo/LettoreVideo.csproj"
```

Oppure lancia l'eseguibile passando una cartella (o un file: viene aperta la sua
cartella), così funziona anche come destinazione di "Apri con":

```bash
"src/LettoreVideo/bin/Release/net8.0-windows/win-x64/LettoreVideo.exe" "D:\Video"
```

Comandi da tastiera: `Spazio` play/pausa, `←`/`→` ±5 s, `PagSu`/`PagGiù` video
precedente/successivo, `F11` schermo intero, `Esc` esce dallo schermo intero.

Le scorciatoie di trasporto hanno la precedenza su quasi tutto: restano attive
anche con il focus su un pulsante o su una voce della playlist, come ci si aspetta
da un lettore. Cedono il passo solo dove il tasto serve davvero al controllo che
ha il focus: `Spazio` su una casella di spunta come `RTX HDR`, le frecce su una
casella a discesa o sul cursore del volume. La playlist si continua a navigare con
`↑`/`↓`, che non vengono mai intercettati.

## RTX Video Super Resolution

Il selettore in basso a destra ha tre stati:

| Modalità | Comportamento |
|---|---|
| **Auto** (default) | VSR attiva solo per sorgenti sotto i 1080p |
| **Sempre ON** | VSR attiva su qualsiasi sorgente |
| **Sempre OFF** | VSR disattivata |

La VSR viene applicata a caldo con `vf set`, senza interrompere la riproduzione,
e il fattore di upscale è calcolato sull'altezza reale in pixel dell'area video
(limite 4×, il massimo supportato da NVIDIA). L'indicatore accanto al selettore
mostra sempre lo stato effettivo e il perché.

`RTX HDR` (`nvidia-true-hdr`) converte SDR→HDR: ha senso solo su schermo HDR.

## Scelte tecniche

- **Decodifica**: `hwdec=d3d11va` — NVDEC, i frame restano in texture D3D11 e non
  passano mai dalla CPU. È anche il prerequisito del filtro `d3d11vpp` che
  implementa la VSR: con una variante `-copy` la VSR non sarebbe disponibile.
- **Rendering**: `vo=gpu-next` su contesto D3D11, profilo `high-quality`.
- **Embedding**: mpv disegna dentro una finestra Win32 figlia (`MpvHost`), passata
  con l'opzione `wid`. Evita di reimplementare la render API e lascia a mpv il
  percorso di presentazione più efficiente.
- **`keep-open=no`**: necessario, perché con `keep-open=yes` mpv si ferma
  sull'ultimo fotogramma senza emettere `end-file`, l'evento su cui si basa
  l'avanzamento automatico della playlist.
- **File illeggibili**: la sequenza prosegue anche quando un file non si apre
  (container corrotto, share sparita), segnalandolo nella barra di stato. Dopo 5
  fallimenti consecutivi si ferma: a quel punto il problema è la sorgente, non il
  singolo file, e scorrere le centinaia di voci rimanenti lo nasconderebbe.
- **Ordinamento**: `StrCmpLogicalW`, lo stesso di Esplora risorse, così la
  playlist rispecchia l'ordine che si vede nella cartella.

## Diagnostica

Impostando la variabile d'ambiente `LETTOREVIDEO_MPVLOG` a un percorso, mpv
scrive il proprio log verboso: mostra quale hwdec è stato scelto e come è stata
costruita la catena filtri (cercare `Using hardware decoding` e
`NVIDIA RTX Super Resolution enabled`).

Gli errori non gestiti finiscono in `%TEMP%\LettoreVideo-crash.txt`.

## Aggiornare libmpv

`src/LettoreVideo/runtimes/win-x64/native/libmpv-2.dll` proviene dalle build
Windows ufficiali di mpv ([shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases)),
pacchetto `mpv-dev-x86_64-v3` — la variante `v3` è compilata per x86-64-v3 (AVX2),
adatta a Zen 5. Per aggiornare basta sostituire il file: il progetto è x64 puro,
non compilare mai come AnyCPU.

## Licenza

Copyright (C) 2026 1Pantegana.

Questo programma è software libero: puoi ridistribuirlo e/o modificarlo secondo i
termini della **GNU General Public License** pubblicata dalla Free Software
Foundation, versione 3 o (a tua scelta) qualsiasi versione successiva
(`GPL-3.0-or-later`). È distribuito nella speranza che sia utile, ma **senza alcuna
garanzia**. Il testo completo è nel file [LICENSE](LICENSE).

## Licenze di terze parti

Il programma non funziona senza `libmpv-2.dll`, che non è inclusa nel repository
(vedi [Aggiornare libmpv](#aggiornare-libmpv)) ma va distribuita accanto
all'eseguibile.

| Componente | Licenza | Fonte |
|---|---|---|
| [mpv / libmpv](https://mpv.io) | GPL-2.0-or-later (build GPL) | [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases) |
| [FFmpeg](https://ffmpeg.org) e le altre librerie incluse staticamente nella DLL | GPL-3.0-or-later (build `--enable-gpl --enable-version3`) | come sopra |

Le build di shinchiro sono compilate con le opzioni GPL, per cui l'insieme
distribuito (eseguibile + DLL) ricade sotto la GPL versione 3: è il motivo per cui
anche questo progetto adotta la GPL-3.0. Chi ridistribuisce i binari deve rendere
disponibile anche il sorgente corrispondente di libmpv e delle sue dipendenze,
ad esempio indicando la release esatta di mpv-winbuild-cmake da cui proviene la DLL.
