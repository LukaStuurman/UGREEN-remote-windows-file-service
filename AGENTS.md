# AGENTS.md

## Doel en overdracht

Deze repository bouwt een Windows Explorer-schijf voor uitsluitend de UGREEN NAS-share met de naam `Techbase`. Op het thuisnetwerk moet de client de SMB-share `Techbase` gebruiken. Buiten het LAN gebruikt de client een eigen API-container op de NAS, bereikbaar via een UGREENlink Docker-desktopshortcut met een `*.ugapp.link`- of `*.ugdocker.link`-adres.

Lees voor gebruikersgerichte installatie- en configuratiestappen ook [README.md](README.md). Dit bestand is de technische gids en overdracht voor agents die de implementatie voortzetten.

De eerste implementatie staat op `main` in commit `456fe4d` (`feat: add UGREENlink remote Windows drive`). Er was nog geen `AGENTS.md` voor deze repo.

## Huidige status — 2026-09-27

### Transparant Techbase-icoon — 2026-10-16

- `v0.6.1-preview.1` exporteert het oorspronkelijke beeldmerk als één donkeroranje kleur `#FF5A00` met transparante achtergrond. App-, venster-, systeemvak- en installericoon verwijzen naar dezelfde ICO. Oorspronkelijk koplogo blijft intact.
- `Assets/Build-Icon.ps1` maakt negen echte RGBA-PNG-frames (16–256 px) in ICO en een PNG-preview uit het originele logomasker. Geen Icon.Save-quantisatie/opaak palet. Alleen RGB wordt vastgezet; de originele outline/alpha wordt bij schalen behouden. Kleur, alpha, Windows-decodering en compilatie controleren; geen NAS-/profielwijziging of lokale driverinstallatie nodig.

### Tailscale-bestandsintegriteit — 2026-10-16

- `v0.6.0-preview.1` corrigeert alleen de ingebouwde Tailscale-bestandsroute. Gebruiker meldt verbinding maar corrupte AutoCAD-DWG bij rechtstreeks DXF→DWG opslaan; lokaal, SMB en UGREENlink slagen. Screenshot meldt 57643 fouten. De exacte oorzaak van die concrete tekening is niet onafhankelijk vastgesteld; tekeningen/NAS blijven onaangeraakt.
- Dokan Context bewaart een echt SMB-handle tot CloseFile, inclusief paging na Cleanup. Operaties gebruiken file-ID in plaats van heropend pad; geen path-readcache. Access/share/disposition, flush, allocation/EOF, attributen en server-side atomic replace worden doorgegeven. Geen pre-delete of onzekere write-retry; fout bij schrijven/flush blijft fout op dat handle. Native Windows SMB en UGREENlink blijven op hun bestaande codepad.
- `embedded-tailscale/third_party/go-smb2` bevat ongewijzigde gepinde v1.1.0-runtimebronnen/licentie en één aparte uitbreiding. SHA-manifest-test bewaakt originele bytes; .gitattributes voorkomt newlineconversie. Geen algemene SMB-proxy. Bestaande Techbase-/pad-/peergrenzen blijven verplicht.
- Verificatie: lokale callbacktests, CI-Samba random/header-write/replace/share/allocation-tests en een echte Dokany-mount met synthetische geheugenbestanden op uitsluitend wegwerpbare Windows-CI. Dit is geen echte AutoCAD-/NAS-save of tekeningherstel. Bestaande goede lokale bestanden/DXF bewaren; eerste gebruik met kopie en nieuwe naam. Cross-client byte-range-locking blijft onbewezen en niet ondersteund.

### Herstel Tailscale-registratie — 2026-10-16

- `v0.5.2-preview.1` herstelt registratie van een nieuwe eigen node. Alleen LocalClient.Start met AuthKey installeert een nieuwe control-client maar start voor een node zonder bewaarde node-key geen login. De private helper doet nu dezelfde expliciete StartLoginInteractive-trigger als officiële tsnet-startup, uitsluitend na de expliciet ingevoerde auth key. De naam van deze methode betekent hier geen browseraanmelding: geen browser openen, geen auth-URL, key, accountdata of rauwe notificatie naar UI/logs.
- Helper observeert de private IPN-bus voor echte Running/NeedsMachineAuth en asynchrone fouten. Alleen vaste gesaniteerde berichten verlaten het proces. Een timeout wordt niet meer ten onrechte apparaatgoedkeuring genoemd. Alleen NeedsMachineAuth toont goedkeuringsinstructies en de knop Goedkeuring controleren; opnieuw verbinden in die toestand vraagt geen nieuwe sleutel.
- Tests: lokale nagebootste LocalAPI controleert de ontbrekende login-trigger, succes, approval, geweigerde sleutel, timeout en geen geheim in fouten. CI draait bovendien echte tsnet tegen Tailscales lokale in-memory testcontrol met synthetische sleutel/state, lokale DERP/STUN en geen echte tailnet/NAS. Dit vervangt geen live NAS-/gebruikersregistratietest.

### Techbase-vormgeving — 2026-10-16

- `v0.5.1-preview.1` geeft de client een Techbase-kop met door de gebruiker geleverd logo, eigen applicatie-/setupicoon, oranje hoofdactie, verbindingskaarten en aparte tab Instellingen. Native NAS-/Tailscale-popups en de officiële UGREENlink-popup gebruiken dezelfde stijl. Instellingsnamen, stores, verbindingen en beveiligingsregels blijven behouden.
- Branding staat lokaal ingebed in `client/UGREENRemoteDrive/Assets`; bron en merkrechten staan daar in README.md. Geen runtime-downloads. Layout op normale/kleine vensterbreedte gecontroleerd met een geïsoleerde XAML-render zonder clientcode, profiel, aanmelding of NAS. Dit is geen echte verbindings-/NAS-test.

### Ingebouwde Tailscale en aanmeldpop-ups — 2026-10-16

- Gebruiker verving de eigen WireGuard-aanpak door zijn bestaande Tailscale op de NAS, vraagt geen aparte Tailscale-app op Windows en geen Tailscale-login. `v0.5.0-preview.1` gebruikt officiële tsnet v1.104.1 in een meegebouwde private Go-component. Eén eenmalige auth key is nodig om de eigen node te autoriseren; geen Tailscale-inlogpagina. Geen sleutel vragen in chat, uitlezen uit bestaande clients of bundelen in Git/releases. Eigen node-state per Windows-gebruiker DPAPI-versleuteld; bestaande Tailscale-service/state blijft onaangeraakt. Geen NAS-/routerwijziging gedaan.
- Alleen de ingevoerde .ts.net-naam/100.64.0.0/10-peer uit de eigen geauthenticeerde peerlijst, alleen TCP 445 en Share Techbase. Geen publieke DNS-fallback of algemene proxy/subnetrouter. Eigen SMB-client in de component, geen Windows-adapter, lokale luisterpoort of losse Tailscale-installatie nodig. Ruwe begrensde binaire stdin/stdout-frames, acht gelijktijdige aanvragen; wachtwoorden/sleutels nooit CLI, JSON-config of logs. Go NTLM vereist kortlevende strings in procesgeheugen; beweer niet dat alle geheugenrepresentaties onmiddellijk gewist kunnen worden.
- Hoofdknop probeert eerst bestaande LAN-share en vervolgens ingebouwde Tailscale als een host is ingesteld. Expliciete Tailscale-knop kan die route kiezen na ontkoppelen. Eigen NAS-inlogpopup via bestaande credential-store, alleen succesvolle credentials onthouden. Auth key niet in instellingen bewaren. Logout raakt uitsluitend eigen embedded node. Toegangsregels, goedkeuring, node-keyverloop en Tailnet Lock blijven gelden.
- Browser onderaan verwijderd. Hetzelfde WebView2-control/profiel wordt bij UGREENlink in een afzonderlijk eigen aanmeldvenster gehost. Geen nieuwe UGOS-wachtwoord-API of DOM-/cookie-export; bestaande origin-, popup- en ticketregels blijven intact. LAN en Tailscale gebruiken de native NAS-popup. Runtime blijft nodig voor de fallback.
- Go-helper bouwt eerst via embedded-tailscale/Build.ps1 (Go 1.27.2, Python), daarna .NET. Binary en dependencylicenties zijn resources in dezelfde Windows-EXE; gecontroleerde extractie per SHA256 onder eigen AppData. Geen symlink/ADS/rootmutaties. De oorspronkelijke preview weigerde atomic rename over bestaand doel; de handle-route vanaf v0.6.0-preview.1 gebruikt server-side replace zonder pre-delete. Zie de integriteitsstatus hierboven.
- Lokale Go-/Windows-tests gebruiken geen echte tailnet of NAS. CI voegt SMB over een wegwerpbare Samba-fixture, DPAPI-heropenen, private RPC en gereparent WebView2-control toe. Dit bewijst nog geen eigen tailnetregistratie, verbinding via de NAS-Tailscale-installatie of echte NAS-mount. Zie docs/embedded-tailscale.md voor setup/limieten.

### Eigen NAS-inlogvenster — 2026-10-16

- Gebruiker vroeg expliciet om een eigen gebruikersnaam-/wachtwoordvenster in plaats van WebView. `v0.4.1-preview.1` voegt dit toe voor LAN/WireGuard-SMB met het lokale UGOS-account. De eerdere algemene regel dat de app geen SMB-wachtwoord verwerkt wordt hiermee uitsluitend voor deze expliciet gekozen route vervangen. UGREENlink blijft de eigen browseraanmelding gebruiken; de openbare UGOS app-integratie documenteert geen rechtstreekse wachtwoordaanmelding voor deze losse Windows-client.
- `SmbSignIn.cs` gebruikt WNetAddConnection2 zonder driveletter of het verbreken van andere sessies. PasswordBox/SecureString en tijdelijk gewiste native buffers; geen wachtwoord in JSON/logs/browserinjectie. Onthouden uitsluitend na succes, als eigen Generic credential `UGREENRemoteDrive:SMB:<gevalideerde-root>` in Windows Credential Manager. Leest/vervangt geen bestaande SMB-, Edge- of WebView-wachtwoorden. Geen NAS-inloggegevens uitlezen tijdens agentwerk.
- Herstel eenmaal per adres per appstart, uitsluitend bij bereikbare SMB-poort; de statustimer herhaalt geen verkeerde wachtwoorden. Een andere bestaande Windows-identiteit (1219) wordt gemeld zonder sessies te verbreken. Vergeten raakt alleen de eigen entry voor het huidige adres; bestaande SMB-sessies eindigen daardoor niet. LAN en WireGuard zijn afzonderlijke adressen/entries.
- Bij lokale SMB geen NAS-webdesktop openen; eigen verbindingspaneel. UGREENlink-browser zichtbaar voor benodigde aanmelding, daarna verborgen; expliciete knop UGREENlink openen blijft beschikbaar. Runtime blijft vereist. Geen browser-origin-, popup-, ticket- of authenticatiebeleid versoepeld.
- Verificatie: unit-tests met fictieve store/connector en een Credential Manager roundtrip uitsluitend op wegwerpbare GitHub-runner. Geen lokale credentials gelezen of geschreven door tests, geen echte NAS-login of schrijftest uitgevoerd. Geen nieuwe NAS-container of routerwijziging nodig voor deze client-update.

### Eigen directe WireGuard-route — 2026-10-15

- Gebruiker vroeg een Docker-app voor direct verbinden en koos expliciet WireGuard. `v0.4.0-preview.1` voegt daarvoor een apart Compose-project toe. De bestaande UGREENlink-container en browser-originregels blijven intact. Geen live NAS-/routerwijzigingen zonder concrete configuratie en afstemming.
- `direct-wireguard/` gebruikt standaard WireGuard-kernel of wireguard-go bij ontbrekende kernelondersteuning, met NET_ADMIN en /dev/net/tun. Geen privileged/hostnet/SYS_MODULE. Eén peer, privé Docker-statevolume, geen NAS-datamap-mount. Publiceert uitsluitend IPv4 UDP 51820 (of gekozen WG_PORT); vereist bereikbare eigen IPv4/DDNS en router-forwarding. Geen cloud/P2P-coördinatieservice of relayfallback.
- Interne tunnel 10.203.0.1/32 met Windows-peer 10.203.0.2/32. INPUT/OUTPUT/FORWARD standaard DROP; uitsluitend versleuteld WireGuard-UDP en proxy-TCP van wg0:445 naar de ingestelde private NAS_ADDRESS:445. Geen NAT of algemene LAN-/internetforwarding. Root-namespacefilters wijzigen geen hostfirewall.
- De TCP-proxy kan SMB-sharenamen niet filteren: verplichte aparte UGOS-SMB-accountrechten beperken toegang tot Techbase. De Windows-app weigert andere shares/submap-roots. Gebruik geen algemeen NAS-beheeraccount of guest. Windows bewaart zijn SMB-identiteit; app/config/logs krijgen geen wachtwoord.
- Privésleutels worden uitsluitend tijdens starten gegenereerd en blijven in het statevolume; cliëntprofiel via geautoriseerde Docker-export, nooit logs/Git/release. Status toont uitsluitend ready/handshake/leeftijd. Release-ZIP bevat alleen publieke bron/configtemplates, geen gegenereerde profielen.
- Windows ondersteunt nu SMB-only (UGREENlink-veld leeg met geldige Techbase UNC) en probeert bereikbare SMB direct vóór browseraanmelding. Gereserveerd endpoint geeft WireGuard-SMB als status, zonder daarmee een handshake te bewijzen.
- Netwerkvoorcontrole op gebruikerscomputer: lokale UDP 51820 niet bezet, geen bruikbare globale IPv6, router-UPnP niet uitleesbaar. Gebruiker zit bij computer/NAS op hetzelfde netwerk en weet alleen van een Ubiquiti-punt. Routermodel/WAN-adres, CGNAT, NAS-LAN-adres, TUN/kernel en externe UDP-bereikbaarheid zijn nog onbekend; claim geen open poort of werkende live VPN.
- CI toetst WireGuard-invoer/firewall/keys en een echte userspace-tunnel met bytegetrouwe SMB upload/download plus geblokkeerde forwarding tussen wegwerpcontainers. Dit is geen NAS-/WAN-integratietest. Zie docs/direct-wireguard.md.

### Binaire overdracht, prefetch en metadata — 2026-10-15

- `v0.3.7-preview.1` is een client-update: ruwe gedeelde WebView2-buffers voor reads/writes, begrensde achtergrond-prefetch van het volgende venster en single-flight voor stat/list. Geen NAS-deployment nodig voor deze drie wijzigingen; server-update uit `v0.3.6-preview.1` blijft apart aanbevolen.
- Exacte origin/root/auth/navigatiecontroles blijven vóór delen en bij replies verplicht. Uploadbuffer read-only, readbuffer maximaal aangevraagde lengte en 4 MiB, maximaal acht native buffers tegelijk. Browser streamt met overflowcontrole en abort na 30 seconden; beide kanten geven hun buffer vrij. Geen base64 voor bestandsblokken. Actuele WebView2 Runtime vereist.
- Prefetch pas vanaf de helft van een volledig sequentieel 1 MiB-venster, maximaal één volgend venster per bestand en twee achtergrondworkers. Acht bestanden kunnen samen 16 MiB aan huidige/volgende vensters behouden. TTL één seconde; mutaties, sessiewijziging en eviction verwerpen resultaten. Geen uitgestelde writes.
- Metadata deelt uitsluitend gelijktijdige reads voor hetzelfde genormaliseerde pad/operatie; invalidatie maakt lopende loads los en beschermt de nieuwe cache tegen oude afronding.
- Native shared-buffer-integratietest uitsluitend op wegwerpbare GitHub Windows-runner: onzichtbare HWND, nieuw tijdelijk profiel, alle fixture-HTTPS-responses in RAM. Geen NAS- of gebruikersprofiel. Lokale unit-/jsdom-tests zijn geen echte NAS-snelheidsmeting.

### Remote lezen en transport — 2026-10-15

- `v0.3.6-preview.1` voegt adaptief vooruit lezen toe: eerste/willekeurige kleine read blijft klein, een aansluitende read haalt maximaal 1 MiB vooruit. Maximaal acht RAM-vensters van elk 1 MiB, één seconde geldig, geen offline-/schrijfbuffer. Externe wijzigingen kunnen één seconde oude bytes geven. Lokale mutaties wissen vóór en na de aanvraag; sessiegeneratie/authenticatie worden gecontroleerd. Identieke aanvragen delen hun fetch; verschillende offsets blijven parallel.
- SMB-probes delen een lopende worker en kunnen een resultaat vijf seconden hergebruiken. De backendselector blijft behouden bij gelijke instellingen. Connect op de reeds geladen root valideert de sessie zonder reload.
- Serverversie `0.3.0` ondersteunt HTTP/1.1, correcte Content-Length, 30 seconden idle-timeout en TCP_NODELAY. Foutantwoorden sluiten de verbinding; ambigue of onverwachte bodies worden geweigerd. `O_BINARY` houdt Windows-tests bytegetrouw. Dit serverdeel vereist image-rebuild/containerupdate op de NAS; die is voor deze release niet uitgevoerd.
- Deterministische clienttest: 1 MiB in zestien blokken van 64 KiB gebruikt twee fetches met bytevergelijking. Servertests controleren acht reads en drie bevestigde writes op telkens één TCP-verbinding. Dit bewijst geen echte NAS-snelheid, proxy-keepalive of millisecondenlatency. Zie `docs/performance.md`.

### Aanmelding en tegelactivatie — 2026-10-15

- `v0.3.5-preview.1` gebruikt het bestaande vaste WebView2-profiel, zet op gebruikerskeuze de native wachtwoordmanager aan en selecteert de bestaande UGOS-optie ingelogd blijven. Clientcode leest geen wachtwoorden, browser-passworddatabase, cookies of opslagtokens. Een native browserbewaarvraag blijft gebruikerskeuze; er komen geen credentials in config/logs. De NAS kan de sessie nog verlopen/intrekken of MFA vereisen.
- `DesktopAutomation.cs` en het ingebedde `Browser/DesktopAutomation.js` vervangen de oude vierseconden-anchorzoeker. De helper wacht door aanmelding en SPA-routeveranderingen heen, gebruikt het echte `.apply-list .list-item` / `.click-region`-klikvlak en bewaakt eenmalig openen. De generieke selectors zijn gecontroleerd tegen de openbaar geserveerde UGOS-frontendcode, niet tegen een uitgelezen ingelogde browsersessie.
- De standaardtegelnaam is `ugreen-remote-drive`; afwijkende namen zijn instelbaar. Een recente tegelnaam/module-id wordt uitsluitend na goedgekeurde popupbestemming lokaal onthouden. Popup-origin-, frame- en bootstrapcontroles blijven intact. Geen persoonlijke link of NAS-identifier hardcoden of opnemen in Git.
- De aanmeld- en verbindingsvoorkeuren staan standaard aan. Met opgeslagen configuratie probeert de app bij openen automatisch te verbinden. `Wis lokale aanmelding` wist ook site-opslag en native opgeslagen credentials; updates behouden het profiel. Uitzetten van onthouden verwijdert oude browserdata niet vanzelf.
- Browsertests staan in `client/browser-tests` (npm ci, npm test) en voeren de echte helper uit in jsdom met UGOS-vormige fixtures. Ze toetsen trage aanmelding, latere desktoprendering, echte klikvlakken, dubbele namen, module-id, één klik en geweigerde origins. .NET toetst instellingsmigratie en beperkte identiteit/config. Een echte NAS-reconnect of sessie na Windows-herstart is hiermee nog niet aangetoond; rapporteer die beperking.

### Installer-update — 2026-10-15

- `v0.3.4-preview.1` voegt `UGREENRemoteDrive-Setup-win-x64.exe` toe. Dit is de aanbevolen download; de losse EXE en ZIP zijn portable en vereisen een reeds geïnstalleerde Dokany-driver.
- `installer/Build-Installer.ps1` verifieert SHA-256 en Authenticode van de vaste officiële Dokany 2.3.1.1000-bundle en bouwt met Inno Setup 6 één installer met die bundle ingebed. Compileren installeert geen driver op de ontwikkelcomputer.
- De setup installeert ontbrekende Dokany met beheerdersrechten, hergebruikt een complete compatibele 2.3+-installatie, meldt herstarts en start de app pas als de driver beschikbaar is. Een bestaande oude/onvolledige 2.x-installatie wordt niet automatisch verwijderd: upstream vereist eerst verwijderen en herstarten. De app start na setup als oorspronkelijke Windows-gebruiker.
- `installer/Test-Installer.ps1` mag uitsluitend op een schone, wegwerpbare GitHub-hosted runner draaien. CI toetst vers installeren, laden van native DLL/driver, herinstalleren zonder driververvanging en verwijderen met behoud van gedeelde Dokany en gebruikersinstellingen. Dit is geen NAS-integratietest. Installeer/test de driver niet op de gebruikerscomputer zonder afzonderlijke toestemming.
- Het bestaande WebView2-profiel en de NAS-instellingen blijven behouden. WebView2 Runtime blijft een Windows-voorwaarde. De NAS-container hoeft voor deze installerwijziging niet te worden aangepast.

- Laatste gepubliceerde versie: GitHub-prerelease [`v0.3.3-preview.2`](https://github.com/LukaStuurman/UGREEN-remote-windows-file-service/releases/tag/v0.3.3-preview.2), vanaf de documentatie-update na `v0.3.3-preview.1`. Deze tweede preview werkt de documentatie bij; de Windows-appfunctionaliteit is gelijk aan `.1`. De eerdere `v0.1.0-preview.1` is verouderd; gebruik die niet.
- PR [#4](https://github.com/LukaStuurman/UGREEN-remote-windows-file-service/pull/4) bracht een best-effort automatische tegelopening bij reconnect en is samengevoegd. De client probeert op het vertrouwde UGREENlink-`/desktop/` één keer een link te openen waarvan HTTPS-origin exact overeenkomt met de opgeslagen Remote Drive-shortcut. Alleen de root- of goedgekeurde auth-bootstrapbestemming kan door een eenmalige popup-toestemming.
- Eerste discovery vanaf `https://ug.link/<ID>` vereist nog steeds één handmatige Remote Drive-tegelklik: de gegenereerde shortcut-hostnaam is daarvoor nog onbekend. Bij reconnect valt de app terug op een handmatige klik als de tile-link ontbreekt of de automatische poging niet werkt. Zie [README.md](README.md) en [docs/ugreenlink-shortcut-discovery.md](docs/ugreenlink-shortcut-discovery.md).
- Dit is geen UGREEN-ondersteunde auto-activerings-API en de automatische klik is nog niet op de echte NAS/UGOS-firmware geverifieerd. De gebruiker heeft eerder gemeld Techbase in Verkenner te kunnen openen; dat bevestigt niet dat deze reconnect-functie werkt.
- Lokale verificatie op 2026-09-27: Windows-clienttests 36 geslaagd; Python-servertests 8 geslaagd, 1 overgeslagen omdat symlink-aanmaak niet beschikbaar was. GitHub Actions voor `v0.3.3-preview.1` slaagde voor Windows-build/tests, servertests, container-smoketest en publicatie. De `.2`-tag bevat alleen documentatievernieuwing en krijgt opnieuw dezelfde CI-controles.
- De toegestane datascope blijft uitsluitend de gedeelde map `Techbase`. Geen NAS-hostpad, NAS-ID, persoonlijke shortcut-URL, wachtwoord, cookie of ticket opnemen in code, Git, logs, tests of release-assets.
- Voor dit project heeft de gebruiker gevraagd **geen computer-use/UI-automatisering en geen agents te gebruiken**. Werk repo/GitHub-taken rechtstreeks uit en verander dit alleen als de gebruiker later anders vraagt.

Vermeld voortaan duidelijk welke resultaten lokaal zijn getest en welke op de NAS zijn getest. Claim geen werkende remote mount voordat die end-to-end is geverifieerd.

## Architectuur

```text
Windows Explorer
  -> vaste driveletter (DokanNet / Dokany)
  -> DriveFileSystem.cs
  -> BackendSelector
       -> SMB-backend (voorkeur als UNC-share bereikbaar is)
       -> Remote-backend
            -> RemoteBridge.cs
            -> WebView2-pagina met actieve UGREENlink-login
            -> HTTPS same-origin fetch met UGREENlink-sessie en client-header
            -> UGREENlink desktopshortcut voor de NAS-container
            -> Python API-container
            -> alleen de hostmap die als /data is gemount
```

### UGREENlink-authenticatie en transport

- Elke deployment gebruikt de eigen Docker-desktopshortcut van deze container. Hergebruik of publiceer geen persoonlijke UGREENlink-shortcuts.
- De UGREENlink-login gebeurt in de client in WebView2. API-calls worden als `fetch` vanuit die pagina gedaan, met `credentials: include`, zodat de browser zijn normale UGREENlink-sessie gebruikt.
- De client leest, kopieert of exporteert geen UGREENlink-cookies en leest/bewaart zelf geen NAS-wachtwoord. Op expliciet verzoek van de gebruiker kan de native WebView2-wachtwoordmanager het wachtwoord in het eigen lokale profiel bewaren na de native bewaarvraag. De appconfig en logs bevatten geen credentials; Edge-profielen blijven ongemoeid.
- De nieuwe implementatie gebruikt geen tweede bearer-token. UGREENlink-login beschermt de shortcut; fetch-verzoeken gebruiken `credentials: include`. De API verwacht daarnaast `X-UGREEN-Remote-Drive: 1` als browser-/client-header om gewone cross-site form-verzoeken te blokkeren. Dit is geen gedeelde geheime sleutel en geeft binnen de shortcut geen per-gebruiker-autorisatie.
- De bridge controleert HTTPS, uitsluitend `.ugapp.link` of `.ugdocker.link`, en dezelfde exacte origin voor de huidige pagina en API-doel-URL. Verruim deze controle niet naar willekeurige domeinen.
- De UGREENlink-proxy moet de containershortcut op poort `18765` kunnen bereiken, hoewel de Docker-hostpoort op `127.0.0.1` gebonden is. De gebruiker rapporteerde eerder een werkende Techbase-schijf; test de proxy opnieuw bij wijzigingen of als remote toegang faalt.
- Als de shortcut een loopback-hostbinding niet kan bereiken, wijzig de poortbinding niet stilzwijgend naar `0.0.0.0`. Beoordeel eerst de UGOS-proxyroute, firewall en veiligste minimale binding; vraag toestemming voordat de netwerkblootstelling wordt vergroot.
- UGREEN documenteert dat remote toegang tot Docker-desktopshortcuts alleen beschikbaar is voor gebruikers die via UGREENlink zijn aangemeld: https://support.ugnas.com/detail/article/en-US/715. De gewone Compose-container krijgt geen individuele UGREEN-accountidentiteit; iedere account die de shortcut mag openen krijgt dezelfde read/write-toegang tot Techbase. Beperk shortcutrechten tot vertrouwde NAS-accounts.

### Servercontainer

- `server/app.py` is een Python-standaardbibliotheek-HTTP-server. `server/Dockerfile` bouwt de image.
- De container luistert intern op `0.0.0.0:8080`; Compose publiceert alleen `127.0.0.1:18765:8080` op de NAS-host.
- Alleen het hostpad van de gedeelde map `Techbase` mag als `/data` worden gemount. Het is een read/write-mount omdat Explorer bestanden en mappen moet kunnen wijzigen. Compose voorkomt het automatisch aanmaken van een ontbrekend bronpad en de server weigert hostpaden die niet absoluut zijn of niet eindigen op `Techbase`. Die naamcontrole bewijst niet dat een gelijknamige map echt de UGOS-share is; verifieer daarom het exacte pad in UGOS. Mount nooit `/volume1`, een parentmap of een andere share.
- De NAS Compose-configuratie staat in `docker-compose.ugreen.yaml`; `UGREEN_DRIVE_REF` kiest de branch/tag van de GitHub-buildcontext en image-tag. Gebruik voor een release de bijbehorende geteste tag; de standaard `main` is alleen voor development. De lokale ontwikkelconfiguratie staat in `docker-compose.yaml`.
- De container gebruikt de ingestelde `PUID:PGID` (standaard in Compose `1000:10`), read-only root filesystem, tijdelijke `/tmp`, geen Linux-capabilities, `no-new-privileges`, en CPU-/procesbeperkingen.
- Er is geen `UGREEN_DRIVE_TOKEN` meer. De server weigert te starten als `DATA_ROOT` of `TECHBASE_HOST_PATH` ongeldig is.

### API-overzicht

Alle bestands-API-routes behalve `/` en `/api/v1/health` vereisen de header `X-UGREEN-Remote-Drive: 1`. Deze niet-standaard browserheader voorkomt normale cross-site browserforms; de externe UGREENlink-sessie is de toegangscontrole. Bestandspaden zijn relatief aan `/data` en gebruiken `/` als scheidingsteken.

| Methode en route | Functie |
| --- | --- |
| `GET /` | Statuspagina; geen bestandsinhoud |
| `GET /api/v1/health` | Ongeauthenticeerde container-healthcheck |
| `GET /api/v1/list?path=` | Directory-inhoud en metadata |
| `GET /api/v1/stat?path=` | Metadata van een bestand of map |
| `GET /api/v1/read?path=&offset=&length=` | Lees een bytebereik; maximaal 4 MiB per verzoek |
| `GET /api/v1/space` | Beschikbare en totale schijfruimte |
| `POST /api/v1/create?path=` | Maak een leeg bestand aan |
| `PUT /api/v1/write?path=&offset=` | Schrijf ruwe bytes vanaf een offset; `offset=-1` voegt atomair achteraan toe; body maximaal 4 MiB |
| `POST /api/v1/mkdir?path=` | Maak één map aan |
| `POST /api/v1/resize?path=&size=` | Wijzig bestandsgrootte |
| `POST /api/v1/rename` | JSON-body met `source`, `target`, optioneel `replace` |
| `DELETE /api/v1/file?path=` | Verwijder een bestand |
| `DELETE /api/v1/directory?path=` | Verwijder een lege map |
| `POST /api/v1/times` | JSON-body met `path` en `modifiedUtcEpoch` |

De server weigert absolute paden, `..`, paden buiten de ingestelde root, symbolische links en niet-reguliere bestandstypen. De share-root zelf mag niet verwijderd of hernoemd worden. De requestlogger schrijft client-IP, methode, API-route, status en grootte; querystrings met bestandspaden, bodies, tokens en cookies worden niet gelogd.

### Windows-client

- `client/UGREENRemoteDrive/` is een WPF-app voor .NET 8 (`net8.0-windows`). Belangrijke onderdelen: `DriveFileSystem.cs` vertaalt Dokany-operaties; `StorageBackends.cs` implementeert SMB en remote API; `RemoteBridge.cs` koppelt Dokan-workerthreads aan de WebView2-browser; `DriveSettings.cs` bewaart instellingen en logt.
- NuGet-referenties: DokanNet `2.3.0.3`, WebView2 `1.0.4191.47`. De officiële Dokany 2.3 runtime/driver wordt door de nieuwe setup meegenomen en geïnstalleerd als hij ontbreekt; portable downloads blijven een bestaande driver vereisen.
- De SMB-backend gebruikt de bestaande Windows SMB-identiteit. Het nieuwe expliciet gekozen NAS-inlogvenster kan een eigen aanmelding onthouden in Windows Credential Manager; zie de status hierboven. Configuratie en backend accepteren uitsluitend de UNC-share-root `\\server\Techbase`, zonder andere shares of submappen. De client controleert SMB periodiek; bereikbare SMB krijgt voorrang boven remote.
- De statuscontrole loopt via een 5-seconden UI-timer; SMB-probes hebben een timeout van 2 seconden en remote-authenticatie wordt periodiek opnieuw geprobeerd. Een onderbroken lopende bestandsactie wordt niet gegarandeerd hervat: de app meldt de fout zodat die opnieuw kan worden uitgevoerd.
- De client draait in het systeemvak. Optioneel kan Windows-aanmelding worden ingesteld via de current-user Run-registersleutel. Bij autostart kan de app zichtbaar worden als opnieuw aanmelden vereist is.
- Configuratie en WebView2-profiel staan onder `%LOCALAPPDATA%\UGREEN Remote Drive`; logs staan in de submap `logs`. Oude versies bewaarden een DPAPI-versleuteld token; de nieuwe client negeert het legacyveld en verwijdert het bij de volgende configuratie-save.
- Logs mogen geen bestandsnamen, tokens, cookies of request bodies bevatten. Verander dit niet bij diagnostiek.

## Beperkingen van de huidige versie

- Geen betrouwbare byte-range file locking; databases of apps die locks vereisen mogen de mount niet gebruiken.
- Geen alternate data streams, ACL-/security descriptor-bewerking of offline write-cache.
- Geen automatische retry/roll-forward van een afgebroken schrijfverzoek; gedeeltelijke remote writes zijn mogelijk bij netwerkuitval.
- Symbolische links/reparse points worden bewust geweigerd.
- De gebruiker heeft eerder toegang tot Techbase en zichtbare bestanden via de Windows-schijf gemeld; dit is geen onafhankelijke controle van de nieuwste release. Vooral automatische tegelopening bij reconnect, UGREENlink-proxygedrag en herstel na sessieverloop zijn voor de huidige code nog niet end-to-end op de NAS geverifieerd.

## Bouwen en controleren

Voer geen extra tests uit tenzij de gebruiker daarom vraagt of de wijziging verificatie nodig maakt. Tests die in deze repo al bestaan:

```powershell
python -m unittest discover -s server/tests -v
python -m py_compile server/app.py server/tests/test_app.py
dotnet restore client/UGREENRemoteDrive.sln
dotnet build client/UGREENRemoteDrive.sln -c Release
dotnet test client/UGREENRemoteDrive.sln -c Release --no-build
dotnet publish client/UGREENRemoteDrive/UGREENRemoteDrive.csproj -c Release -r win-x64 --self-contained true
```

De publicatie-output staat in `client/UGREENRemoteDrive/bin/` en hoort niet in Git. Een Docker CLI was niet beschikbaar in de oorspronkelijke ontwikkelomgeving; containerbuild/deploy moet door GitHub Actions en later de echte NAS-test worden gevalideerd.

## GitHub CI en releases

`.github/workflows/ci-release.yml` voert bij pull requests en pushes naar `main` drie controles uit: de servertests op Linux, een containerbuild en health/client-header-smoketest met een wegwerpmap op een GitHub-runner, en de Windows-clientbuild op Windows. Een push van een tag met prefix `v` voert dezelfde controles uit en publiceert de self-contained Windows-EXE, ZIP en SHA-256-sidecars als GitHub Release-assets; GitHub biedt ook source archives van de getagde commit aan.

`RELEASING.md` bevat het vrijgaveproces. Een stabiele release mag pas worden gepubliceerd nadat de Techbase-only NAS-tests, SMB op LAN, UGREENlink remote toegang en basisbestandsbewerkingen zijn geslaagd. Als integratie nog niet volledig is, gebruik uitsluitend een duidelijk herkenbare `-preview.N`-prerelease en claim geen NAS-validatie. CI-smoketests zijn geen bewijs van echte NAS-integratie.

## NAS-test en voortzetting

De enige toegestane share voor deze toepassing is `Techbase`. De gebruiker heeft eerder gemeld de gekoppelde schijf en Techbase-bestanden te kunnen openen. De automatische tegelopening in de nieuwste preview is niet op de echte NAS geverifieerd; CI en lokale unit-tests bewijzen dat gedrag niet. Een NAS-deployment geeft de code read/write-toegang tot de gemounte share. Leg het hostpad nooit vast in Git of logs en controleer vóór elke deployment dat uitsluitend de UGOS-share `Techbase` is gemount.

Voer schrijftests alleen uit als de huidige opdracht ze uitdrukkelijk omvat. Gebruik dan een nieuw, duidelijk benoemd tijdelijk submapje binnen `Techbase`; mount geen tweede testshare. Verwijder uitsluitend testitems die de test zelf heeft aangemaakt. Lees of toon geen bestaande bestandsnamen of inhoud buiten wat strikt nodig is om de verbinding te verifiëren.

Bij goedgekeurde test:

1. Controleer de Docker-projectvelden, NAS UID/GID-rechten en dat `DATA_PATH` exact het geverifieerde hostpad van de gedeelde map `Techbase` is.
2. Stel geen API-token in; UGREENlink-login is de remote toegangscontrole. Controleer dat alleen vertrouwde NAS-accounts de Docker-shortcut mogen openen.
3. Deploy `docker-compose.ugreen.yaml` met uitsluitend de Techbase-share als `DATA_PATH`.
4. Na expliciete toestemming, maak een tijdelijk submapje en benoemd testbestand binnen `Techbase`; test lezen/schrijven/hernoemen/verwijderen en verwijder alleen die eigen testitems.
5. Maak in UGREEN Docker een desktopshortcut voor poort `18765`; noteer de nieuw gegenereerde URL alleen in de lokale Windows-clientconfiguratie, niet in deze repo.
6. Als shortcut-proxy of HTTPS same-origin fetch faalt, stop daar en onderzoek de oorzaak. Breid de hostbinding of de toegankelijke NAS-map niet uit zonder nieuwe toestemming.
7. Test daarna op Windows eerst de remote API zonder Dokany-mount. Installeer de officiële driver en mount pas na afzonderlijke afstemming als dat nog nodig is; documenteer elk gewijzigd NAS-/Windows-item en verwijder uitsluitend de expliciet aangemaakte tijdelijke testbestanden.

Een veilige test moet expliciet rapporteren: NAS-containerstatus, health-resultaat, shortcut-auth voor aangemelde en niet-aangemelde gebruikers, client-headercontrole, create/read/write/rename/delete, Windows-build, Dokany-status en wat niet getest kon worden.

## Bronnen en upstream documentatie

- [UGREEN-handleiding voor Docker-shortcuts en UGREENlink-remote access](https://support.ugnas.com/detail/article/en-US/715): remote toegang tot containers is volgens UGREEN alleen beschikbaar voor aangemelde UGREENlink-gebruikers.
- [UGREEN login-authenticatie voor geïntegreerde UGOS-apps](https://developer.ugnas.com/doc/backend/system-capabilities/login-auth.html) en [UGREEN Docker-apps ontwikkelen](https://developer.ugnas.com/en/doc/backend/quick-start/develop-docker-app.html). Deze beschrijven de app-integratieroute; een gewone Compose-container erft die integratie niet vanzelf.
- [Officiële Dokany 2.3.1.1000-release](https://github.com/dokan-dev/dokany/releases/tag/v2.3.1.1000) en [DokanNet 2.3.0.3-release](https://github.com/dokan-dev/dokan-dotnet/releases/tag/v2.3.0.3). Controleer compatibiliteit en release-informatie opnieuw voordat je een driver installeert.

## Werkwijze voor agents

- Inspecteer eerst `git status`, `README.md`, dit bestand en de relevante code. Behoud de bestaande repositorystructuur.
- Houd wijzigingen klein, benoem expliciet aannames en actualiseer README of dit bestand als gedrag, configuratie of teststatus verandert.
- Behoud de security-invarianten: uitsluitend de gedeelde map `Techbase` expliciet mounten en als enige SMB-share accepteren, loopback-hostbinding, UGREENlink-login voor remote toegang, HTTPS- en same-origin-validatie, client-header tegen gewone browser-CSRF, geen cookie-export en geen gevoelige logs.
- Hardcode geen NAS-ID, UGREENlink-URL, account, sharepad, token of gebruiker-specifieke waarde.
- Voer geen destructieve of productie-NAS-acties uit. Vraag concrete bevestiging voordat je nieuwe code op de NAS draait, een andere map mount, een driver installeert of de netwerkblootstelling verruimt.
- Rapporteer lokale tests afzonderlijk van echte NAS-integratietests. Noem openstaande onzekerheden expliciet.
- Gebruik voor repositorywijzigingen een featurebranch en PR naar `main`; wacht op de GitHub Actions-controles voordat je de PR samenvoegt. Publiceer pas daarna een nieuwe, unieke prerelease-tag wanneer de integratie nog niet op echte NAS-hardware is gevalideerd. Verplaats of hergebruik nooit een bestaande release-tag.
