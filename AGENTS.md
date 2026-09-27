# AGENTS.md

## Doel en overdracht

Deze repository bouwt een Windows Explorer-schijf voor uitsluitend de UGREEN NAS-share met de naam `Techbase`. Op het thuisnetwerk moet de client de SMB-share `Techbase` gebruiken. Buiten het LAN gebruikt de client een eigen API-container op de NAS, bereikbaar via een UGREENlink Docker-desktopshortcut met een `*.ugapp.link`- of `*.ugdocker.link`-adres.

Lees voor gebruikersgerichte installatie- en configuratiestappen ook [README.md](README.md). Dit bestand is de technische gids en overdracht voor agents die de implementatie voortzetten.

De eerste implementatie staat op `main` in commit `456fe4d` (`feat: add UGREENlink remote Windows drive`). Er was nog geen `AGENTS.md` voor deze repo.

## Huidige status — 2026-09-27

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
- De client leest, kopieert of exporteert geen UGREENlink-cookies en bewaart het NAS-wachtwoord niet. De WebView2-profielmap is lokaal voor deze app.
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
- NuGet-referenties: DokanNet `2.3.0.3`, WebView2 `1.0.4191.47`, ProtectedData `8.0.0`. De officiële Dokany 2.3 runtime/driver moet apart geïnstalleerd worden om werkelijk te mounten.
- De SMB-backend gebruikt de bestaande Windows SMB-identiteit en bewaart geen SMB-wachtwoord. Configuratie en backend accepteren uitsluitend de UNC-share-root `\\server\Techbase`, zonder andere shares of submappen. De client controleert SMB periodiek; bereikbare SMB krijgt voorrang boven remote.
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
