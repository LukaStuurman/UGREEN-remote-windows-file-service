# Windows-SMB in de gewone Remote Drive-app

Bij **Instellingen → Schijf en opstarten** staat **Windows-SMB rechtstreeks gebruiken (zonder Dokany)**. Ontkoppel eerst de huidige schijf en zet deze optie aan. De voorkeur wordt onthouden. Bestaande configuraties blijven standaard de oorspronkelijke virtuele schijf gebruiken.

Download voor deze route de gewone **UGREENRemoteDrive.exe**. Die bevat de runtime en de Tailscale-onderdelen. De setup voor de oorspronkelijke virtuele schijf installeert nog steeds Dokany; die setup is voor Windows-SMB niet nodig. Windows-SMB start zonder de WebView2-runtime te initialiseren.

## Verbinden

1. Vul het lokale adres `\\NASNAAM\Techbase` en/of de volledige Tailscale-NAS-naam / Tailscale IPv4 in.
2. Kies een schijfletter. Een bestaande koppeling naar exact dezelfde NAS-share wordt hergebruikt. Een letter voor een andere share, lokale schijf of onbekende koppeling wordt geweigerd; de app verwijdert die niet.
3. Klik **Verbinden en schijf openen**. De app probeert eerst de lokale SMB-poort, daarna de ingebouwde Windows-Tailscale-route. **Via Tailscale verbinden** kiest die laatste expliciet.
4. Meld aan met het lokale NAS/UGOS-account in de eigen pop-up. Onthouden gebruikt uitsluitend de bestaande eigen Windows Credential Manager-entries. Een hostname blijft de credential-identiteit, terwijl de verbinding naar het exact geverifieerde Tailscale-peeradres gaat.

Windows verzorgt zelf SMB-handles, schrijven, flush, bestandssloten en hernoemen. Deze route gebruikt geen Dokany-bestandssysteem en geen eigen bestandsbuffer.

## Ingebouwde Tailscale met Windows-SMB

De oorspronkelijke ingebouwde tsnet-tunnel is uitsluitend voor de app en kan geen Windows-netwerkschijf dragen. Daarom gebruikt deze optie de gepinde Tailscale-daemon met een aangepaste eigen Wintun-adapter-ID. De EXE bevat beide helpers, de officiële ondertekende Wintun-DLL en licentieteksten. Uitpakken gebeurt in het eigen gebruikersprofiel; er wordt geen Windows-service, geplande taak of aparte Tailscale-app geïnstalleerd.

**Een portable EXE betekent niet dat er geen driver nodig is.** Alleen het starten van de eigen Tailscale-daemon vraagt de Windows-beheerdersprompt voor Wintun. Het hoofdprogramma, de schijfkoppeling, Verkenner en AutoCAD blijven onder de gewone gebruiker draaien. Bevestig UAC met hetzelfde Windows-account dat de app start; een ander beheerdersaccount heeft een ander privékanaal en een andere identiteit. Bedrijfsbeleid kan de driver of deze programma's alsnog blokkeren.

Vanaf `v0.7.1-preview.1` heeft deze route een eigen adapter-ID, privékanaal en identiteit onder `%LOCALAPPDATA%\UGREEN Remote Drive Windows SMB`. Zo gebruikt de nieuwe versie geen vastgelopen privékanaal van een oudere versie. De eerdere portable-identiteit, oorspronkelijke tsnet-route en systeem-Tailscale-state worden niet gekopieerd, gewijzigd of gestopt. Bij eerste gebruik van deze versie is daarom een nieuwe auth key en eventueel apparaatgoedkeuring nodig. Daarna wordt de eigen identiteit onthouden en een gestopte verbinding bij opnieuw verbinden hervat. De eigen daemon gebruikt Tailscales unattended-modus, zodat het profiel actief blijft wanneer een korte aanmeld- of statusopdracht eindigt. Sleutels worden nooit in instellingen of logs bewaard. Verloop, goedkeuring, tailnetbeleid en NAS-rechten blijven gelden.

De app kiest de NAS uitsluitend uit de geauthenticeerde peerlijst van deze eigen daemon en gebruikt het bijbehorende Tailscale IPv4-adres. Er is geen publieke DNS-fallback. Tailscale neemt geen Windows-DNS of subnetroutes over. De netwerkadapter maakt Tailscale wel beschikbaar voor Windows; beperk toegang met het tailnetbeleid en een NAS-account met alleen de benodigde Techbase-rechten. Vermijd gelijktijdige conflicterende VPN-adapters.

## Ontkoppelen en meldingen

**Schijf ontkoppelen** verwijdert alleen een door deze appsessie gemaakte koppeling en forceert geen open bestanden dicht. Een vooraf bestaande koppeling blijft van Windows; ontkoppel die zelf in Verkenner. Bij afsluiten blijven Windows-SMB-sessies en de eigen daemon beschikbaar, zodat open bestanden niet door het sluiten van het venster hun verbinding verliezen. Dat is geen garantie bij netwerkuitval, afmelden of uitschakelen van de pc.

**Tailscale-toestemming wissen** werkt voor de geselecteerde route. Bij Windows-SMB vraagt de app eerst alle bestanden te sluiten; wissen raakt de eigen Windows-Tailscale-identiteit, niet een geïnstalleerde Tailscale-service. Wisselen van route kan een andere eenmalige aanmelding vragen.

Meldingen benoemen de stap: adapter starten, toestemming, SMB-poort 445 of Windows-schijf koppelen. Een gestopte adapter meldt ook de exitcode. Apparaatgoedkeuring wordt uitsluitend getoond bij de werkelijke status `NeedsMachineAuth`. Een afgebroken registratie is geen bewijs van ontbrekende goedkeuring.

## Verificatie

Lokale tests gebruiken fictieve Windows-API's voor hergebruik, bezette letters, eigendom, open bestanden en foutcodes, plus een pure adapter-ID-controle. Windows-CI controleert het starten van de echte privé-Wintun-daemon naast een tweede daemon met de standaard Tailscale-adapter-ID, zonder NAS of tailnet-account, en de verpakte EXE. Lokaal is geen driver gestart of geïnstalleerd. Op de gebruikerscomputer is na het herstellen van de eigen unattended-instelling de status Running en de NAS als online peer bevestigd. Een echte Windows-SMB-mount en AutoCAD-save via deze route zijn nog niet bewezen. Bewaar goede originele tekeningen en test de eerste save met een kopie.

Upstream: [Tailscale v1.104.1 Windows-daemon](https://github.com/tailscale/tailscale/tree/v1.104.1/cmd/tailscaled), [Wintun](https://www.wintun.net/), [Windows WNetAddConnection2](https://learn.microsoft.com/windows/win32/api/winnetwk/nf-winnetwk-wnetaddconnection2w).
