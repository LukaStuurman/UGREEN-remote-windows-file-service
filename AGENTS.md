# AGENTS.md

## Doel en overdracht

Deze repository bouwt een Windows Explorer-schijf voor uitsluitend de UGREEN NAS-share met de naam `Techbase`. Op het thuisnetwerk moet de client de SMB-share `Techbase` gebruiken. Buiten het LAN gebruikt de client een eigen API-container op de NAS, bereikbaar via een UGREENlink Docker-desktopshortcut met een `*.ugapp.link`-adres.

Lees voor gebruikersgerichte installatie- en configuratiestappen ook [README.md](README.md). Dit bestand is de technische gids en overdracht voor agents die de implementatie voortzetten.

De eerste implementatie staat op `main` in commit `456fe4d` (`feat: add UGREENlink remote Windows drive`). Er was nog geen `AGENTS.md` voor deze repo.

## Belangrijke status op 2026-09-25

- Server-API-tests: 6 tests geslaagd (laatste uitvoering op 2026-09-25).
- Python syntaxcontrole: geslaagd.
- Windows-client `dotnet build` en self-contained `win-x64` publish: geslaagd, 0 buildfouten.
- Windows-clienttests voor SMB-sharevalidatie: 2 tests geslaagd (laatste uitvoering op 2026-09-25).
- De client is nog niet interactief gestart of met een echte Dokany-drive getest.
- De officiële Dokany-driver is niet op Windows geïnstalleerd.
- De gebruiker heeft bevestigd dat alleen de gedeelde map `Techbase` toegankelijk mag zijn. Het hostpad is read-only geverifieerd via UGOS Files > Techbase > Properties > Location; bewaar of commit het gebruiker-specifieke pad niet en verifieer het opnieuw vlak voor deployment.
- De container is niet op de UGREEN NAS gedeployed. Er is geen NAS-pad gemount, token aangemaakt of containerproject gewijzigd.
- De UGREENlink-desktopshortcut voor deze container, de toegang tot de loopback-hostpoort via de shortcut en de volledige remote bestandsstroom zijn dus onbevestigd.
- Expliciete actiebevestiging voor deployment en schrijftests ontbreekt nog. Deployment geeft nieuwe code read/write-toegang tot de volledige Techbase-share.
- Er bestaat een GitHub prerelease `v0.1.0-preview.1` vanaf commit `69c8eba`; die is vóór de Techbase-only guardrails gepubliceerd en is niet NAS-getest. Gebruik die niet als bewezen werkende versie. Er is nog geen NAS-gevalideerde release.

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
            -> HTTPS same-origin fetch met bearer-token
            -> UGREENlink desktopshortcut voor de NAS-container
            -> Python API-container
            -> alleen de hostmap die als /data is gemount
```

### UGREENlink-authenticatie en transport

- De door de gebruiker gedeelde URL was een bestaande Media Hub-appshortcut. Gebruik die alleen als voorbeeld van het `ugapp.link`-mechanisme; hardcode of publiceer die persoonlijke URL niet. De eigen container krijgt na deployment een aparte UGREENlink Docker-desktopshortcut.
- De UGREENlink-login gebeurt in de client in WebView2. API-calls worden als `fetch` vanuit die pagina gedaan, met `credentials: include`, zodat de browser zijn normale UGREENlink-sessie gebruikt.
- De client leest, kopieert of exporteert geen UGREENlink-cookies en bewaart het NAS-wachtwoord niet. De WebView2-profielmap is lokaal voor deze app.
- De API gebruikt daarnaast een aparte bearer-token. Het token wordt aan de toegestane `ugapp.link`-origin meegestuurd en lokaal door Windows DPAPI voor de huidige gebruiker versleuteld bewaard.
- De bridge controleert HTTPS, het `.ugapp.link`-domein en dezelfde exacte origin voor de huidige pagina en API-doel-URL. Verruim deze controle niet naar willekeurige domeinen.
- De UGREENlink-proxy moet de containershortcut op poort `18765` kunnen bereiken, hoewel de Docker-hostpoort op `127.0.0.1` gebonden is. Dit is nog niet op de NAS bewezen. Test dit expliciet.
- Als de shortcut een loopback-hostbinding niet kan bereiken, wijzig de poortbinding niet stilzwijgend naar `0.0.0.0`. Beoordeel eerst de UGOS-proxyroute, firewall en veiligste minimale binding; vraag toestemming voordat de netwerkblootstelling wordt vergroot.
- De officiële UGREEN app-integratieauthenticatie is niet automatisch beschikbaar voor een gewone Compose-container. Dit ontwerp gebruikt daarom UGREENlink voor de browser-login en een eigen API-token voor de bestandsservice.

### Servercontainer

- `server/app.py` is een Python-standaardbibliotheek-HTTP-server. `server/Dockerfile` bouwt de image.
- De container luistert intern op `0.0.0.0:8080`; Compose publiceert alleen `127.0.0.1:18765:8080` op de NAS-host.
- Alleen het hostpad van de gedeelde map `Techbase` mag als `/data` worden gemount. Het is een read/write-mount omdat Explorer bestanden en mappen moet kunnen wijzigen. Compose voorkomt het automatisch aanmaken van een ontbrekend bronpad en de server weigert hostpaden die niet absoluut zijn of niet eindigen op `Techbase`. Die naamcontrole bewijst niet dat een gelijknamige map echt de UGOS-share is; verifieer daarom het exacte pad in UGOS. Mount nooit `/volume1`, een parentmap of een andere share.
- De NAS Compose-configuratie staat in `docker-compose.ugreen.yaml`; `UGREEN_DRIVE_REF` kiest de branch/tag van de GitHub-buildcontext en image-tag. Gebruik voor een release de bijbehorende geteste tag; de standaard `main` is alleen voor development. De lokale ontwikkelconfiguratie staat in `docker-compose.yaml`.
- De container gebruikt de ingestelde `PUID:PGID` (standaard in Compose `1000:10`), read-only root filesystem, tijdelijke `/tmp`, geen Linux-capabilities, `no-new-privileges`, en CPU-/procesbeperkingen.
- `UGREEN_DRIVE_TOKEN` moet minstens 32 tekens zijn. Genereer een cryptografisch willekeurig token en sla het alleen op in de NAS-projectconfiguratie en de Windows-client. Commit, log of post het token nooit.
- De API weigert te starten als token of `DATA_ROOT` ongeldig is. Autorisatie wordt met `hmac.compare_digest` gecontroleerd.

### API-overzicht

Alle API-routes behalve `/` en `/api/v1/health` vereisen `Authorization: Bearer <token>`. Bestandspaden zijn relatief aan `/data` en gebruiken `/` als scheidingsteken.

| Methode en route | Functie |
| --- | --- |
| `GET /` | Statuspagina; geen bestandsinhoud |
| `GET /api/v1/health` | Ongeauthenticeerde container-healthcheck |
| `GET /api/v1/list?path=` | Directory-inhoud en metadata |
| `GET /api/v1/stat?path=` | Metadata van een bestand of map |
| `GET /api/v1/read?path=&offset=&length=` | Lees een bytebereik; maximaal 4 MiB per verzoek |
| `GET /api/v1/space` | Beschikbare en totale schijfruimte |
| `POST /api/v1/create?path=` | Maak een leeg bestand aan |
| `PUT /api/v1/write?path=&offset=` | Schrijf ruwe bytes vanaf een offset; body maximaal 4 MiB |
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
- Configuratie en WebView2-profiel staan onder `%LOCALAPPDATA%\UGREEN Remote Drive`; logs staan in de submap `logs`. Het token in `config.json` is DPAPI-versleuteld voor de huidige Windows-gebruiker.
- Logs mogen geen bestandsnamen, tokens, cookies of request bodies bevatten. Verander dit niet bij diagnostiek.

## Beperkingen van de huidige versie

- Geen betrouwbare byte-range file locking; databases of apps die locks vereisen mogen de mount niet gebruiken.
- Geen alternate data streams, ACL-/security descriptor-bewerking of offline write-cache.
- Geen automatische retry/roll-forward van een afgebroken schrijfverzoek; gedeeltelijke remote writes zijn mogelijk bij netwerkuitval.
- Symbolische links/reparse points worden bewust geweigerd.
- Remote-integratie, UGREENlink-proxygedrag, Dokany-runtime, traygedrag en permanente drive-herstel zijn nog niet op een echte NAS/Windows-installatie end-to-end gevalideerd.

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

`.github/workflows/ci-release.yml` voert bij pull requests en pushes naar `main` drie controles uit: de servertests op Linux, een containerbuild en health/auth-smoketest met een wegwerpmap op een GitHub-runner, en de Windows-clientbuild op Windows. Een push van een tag met prefix `v` voert dezelfde controles uit, publiceert daarna een self-contained Windows ZIP en SHA-256-bestand als GitHub Release-assets, en laat GitHub source archives van de getagde commit aanbieden.

`RELEASING.md` bevat het vrijgaveproces. Release-tags zijn pas toegestaan nadat de Techbase-only NAS-tests, SMB op LAN, UGREENlink remote toegang en basisbestandsbewerkingen zijn geslaagd. CI-smoketests zijn geen bewijs van echte NAS-integratie.

## NAS-test en voortzetting

De enige toegestane share voor deze toepassing is `Techbase`. De gebruiker heeft deze datascope bevestigd, maar deployment en schrijftests op de NAS zijn nog niet goedgekeurd of uitgevoerd. Een NAS-deployment wijzigt de NAS-configuratie en geeft nieuwe code read/write-toegang tot de volledige gemounte share. Het hostpad is read-only in UGOS gecontroleerd; verifieer de `Location` opnieuw vlak vóór deployment en neem het pad niet op in Git of logs. Vraag direct vóór deployment expliciete toestemming en bevestig dat alleen `Techbase` wordt gemount.

Voer na goedkeuring eventuele schrijftests uit in een nieuw, duidelijk benoemd tijdelijk submapje binnen `Techbase`; mount geen tweede testshare. Vraag toestemming voor het aanmaken van die tijdelijke map/bestanden en verwijder alleen de testitems die deze test zelf heeft aangemaakt. Lees of toon geen bestaande bestandsnamen of inhoud buiten wat strikt nodig is om de verbinding te verifiëren.

Bij goedgekeurde test:

1. Controleer de Docker-projectvelden, NAS UID/GID-rechten en dat `DATA_PATH` exact het geverifieerde hostpad van de gedeelde map `Techbase` is.
2. Genereer een nieuw token lokaal/veilig; plaats het niet in GitHub, logs of chat.
3. Deploy `docker-compose.ugreen.yaml` met uitsluitend de Techbase-share als `DATA_PATH`.
4. Na expliciete toestemming, maak een tijdelijk submapje en benoemd testbestand binnen `Techbase`; test lezen/schrijven/hernoemen/verwijderen en verwijder alleen die eigen testitems.
5. Maak in UGREEN Docker een desktopshortcut voor poort `18765`; noteer de nieuw gegenereerde URL alleen in de lokale Windows-clientconfiguratie, niet in deze repo.
6. Als shortcut-proxy of HTTPS same-origin fetch faalt, stop daar en onderzoek de oorzaak. Breid de hostbinding of de toegankelijke NAS-map niet uit zonder nieuwe toestemming.
7. Test daarna op Windows eerst de remote API zonder Dokany-mount. Installeer de officiële driver en mount pas na afzonderlijke afstemming als dat nog nodig is; documenteer elk gewijzigd NAS-/Windows-item en verwijder uitsluitend de expliciet aangemaakte tijdelijke testbestanden.

Een veilige test moet expliciet rapporteren: NAS-containerstatus, health-resultaat, shortcut-toegang, token-auth (geldige/ongeldige token), create/read/write/rename/delete, Windows-build, Dokany-status en wat niet getest kon worden.

## Bronnen en upstream documentatie

- [UGREEN Docker-handleiding voor een desktopshortcut naar een containerwebinterface](https://ai.ugreen.com/blogs/how-to/set-up-navidrome-on-nas). De UGOS-interface kan per versie afwijken; controleer de actuele UI voordat je de shortcut maakt.
- [UGREEN login-authenticatie voor geïntegreerde UGOS-apps](https://developer.ugnas.com/doc/backend/system-capabilities/login-auth.html) en [UGREEN Docker-apps ontwikkelen](https://developer.ugnas.com/en/doc/backend/quick-start/develop-docker-app.html). Deze beschrijven de app-integratieroute; een gewone Compose-container erft die integratie niet vanzelf.
- [Officiële Dokany 2.3.1.1000-release](https://github.com/dokan-dev/dokany/releases/tag/v2.3.1.1000) en [DokanNet 2.3.0.3-release](https://github.com/dokan-dev/dokan-dotnet/releases/tag/v2.3.0.3). Controleer compatibiliteit en release-informatie opnieuw voordat je een driver installeert.

## Werkwijze voor agents

- Inspecteer eerst `git status`, `README.md`, dit bestand en de relevante code. Behoud de bestaande repositorystructuur.
- Houd wijzigingen klein, benoem expliciet aannames en actualiseer README of dit bestand als gedrag, configuratie of teststatus verandert.
- Behoud de security-invarianten: uitsluitend de gedeelde map `Techbase` expliciet mounten en als enige SMB-share accepteren, loopback-hostbinding, sterk bearer-token, HTTPS- en same-origin-validatie, geen cookie-export, DPAPI-tokenopslag, geen gevoelige logs.
- Hardcode geen NAS-ID, UGREENlink-URL, account, sharepad, token of gebruiker-specifieke waarde.
- Voer geen destructieve of productie-NAS-acties uit. Vraag concrete bevestiging voordat je nieuwe code op de NAS draait, een andere map mount, een driver installeert of de netwerkblootstelling verruimt.
- Rapporteer lokale tests afzonderlijk van echte NAS-integratietests. Noem openstaande onzekerheden expliciet.
- Commit/push alleen volgens de door de gebruiker aangegeven repo-flow; er is geen PR nodig zolang direct werken op `main` de aangewezen flow blijft.
