# Tailscale ingebouwd in Remote Drive

De Windows-app bevat zijn eigen Tailscale-node via de officiële `tsnet`-bibliotheek.
Je hoeft de losse Tailscale Windows-app niet te installeren of aan te melden.
Je bestaande Tailscale-installatie op de NAS blijft nodig. Dit vervangt de eerdere
eigen WireGuard-container; voor deze route is geen nieuwe NAS-container nodig.

## Eenmalig instellen

1. Controleer op de NAS dat Tailscale online is en dat SMB voor de bestaande share
   **Techbase** aan staat. Een Tailscale-Dockercontainer moet de SMB-dienst van de
   NAS daadwerkelijk bereikbaar maken; alleen een online container bewijst dat niet.
2. Een Tailscale-beheerder maakt een **eenmalige, niet-ephemeral auth key** voor
   deze app. Gebruik voorafgaande apparaatgoedkeuring als het netwerk dat vereist.
   Houd de normale toegangsregels beperkt tot de NAS op TCP 445. De app maakt of
   verruimt zelf geen toegangsregels en schakelt geen Tailnet Lock uit.
3. Open Remote Drive en vul bij **Verbinding → Veilig onderweg** de volledige
   NAS-naam eindigend op `.ts.net` of het Tailscale IPv4-adres in. Geen `https://`,
   poort of sharepad. Het NAS-adres is persoonlijke configuratie, geen broncode.
4. Kies **Via Tailscale verbinden**. Vul de auth key alleen in het
   afgeschermde eenmalige instelvenster in. Deel deze niet via chat, Git of logs.
   De app opent geen Tailscale-inlogpagina. Zonder een bestaande eigen identiteit
   of een geldige sleutel kan een nieuwe app geen toegang tot een privétailnet krijgen.
5. Vul vervolgens in de NAS-pop-up je **lokale UGOS-gebruikersnaam en wachtwoord**
   in. Die gebruiker moet toegang tot uitsluitend Techbase hebben. Optioneel
   onthoudt Windows deze NAS-aanmelding in de eigen credential-entry van de app.
6. De schijf opent in Verkenner. De ingestelde schijfletter en bestaande lokale
   SMB-verbinding blijven beschikbaar. De gewone knop Verbinden kiest eerst de
   bereikbare lokale SMB-share en daarna ingebouwde Tailscale als die is ingesteld.
   Een gekoppelde schijf wisselt niet midden in een bestandsactie van verbinding.

Na herstart hergebruikt deze app zijn eigen identiteit. Eenmalige toestemming is
dus nog steeds nodig; opnieuw inloggen bij Tailscale per verbinding niet. Intrekking,
node-keyverloop, apparaatgoedkeuring of Tailnet Lock kan nieuwe beheerderactie vereisen.
**Instellingen → Tailscale-toestemming wissen** meldt uitsluitend de ingebouwde node af. De losse
Windows-client en de NAS-node worden niet uitgelezen, afgemeld of gewijzigd.

## Registratie, goedkeuring en verloop

Vanaf v0.5.2-preview.1 start de app de registratie expliciet nadat je de sleutel
invoert. De eerdere melding “nog niet goedgekeurd” kon ook betekenen dat de
registratie nooit gestart was; alleen een wachttijd was dus geen bewijs van
vereiste apparaatgoedkeuring.

- Bij een echt geweigerde sleutel meldt de app dat. Een eenmalige sleutel die al
  gebruikt, ingetrokken of verlopen is, kan niet opnieuw een apparaat toevoegen.
- Bij een timeout is nog geen registratiebevestiging ontvangen. Sluit het venster
  en klik opnieuw op **Via Tailscale verbinden** om de actuele toestand te controleren.
- Alleen bij **NeedsMachineAuth** is de app geregistreerd en wacht hij op
  goedkeuring. Open Tailscale **Machines**, zoek **ugreen-remote-drive** en kies
  **⋯ → Approve**. Klik daarna in de app op **Goedkeuring controleren**; voer de
  sleutel niet opnieuw in. Gebruik hiervoor een beheerder in hetzelfde netwerk.
- De auth key heeft maximaal 90 dagen om een nieuwe node toe te voegen. Dat is
  niet de looptijd van een eenmaal geregistreerde verbinding. Kies na registratie
  bij de eigen **ugreen-remote-drive**-node **Disable key expiry** voor geen
  periodiek apparaatverloop. De NAS-node heeft een afzonderlijke instelling.
  Intrekken, wissen van de eigen identiteit en netwerkbeleid blijven van kracht.
- Funnel deelt een specifieke dienst openbaar via HTTPS/relay; het is geen
  rechtstreekse vervanging van de private SMB-route van deze client. De app biedt
  geen Funnel-bestandsbackend. Publiceer de huidige API-container niet zomaar via
  Funnel: die vertrouwt voor aanmelding op de bestaande UGREENlink-route.

## Aanmeldvensters

De NAS-aanmelding over LAN en ingebouwde Tailscale gebruikt hetzelfde eigen
gebruikersnaam-/wachtwoordvenster. De browser onderin het hoofdvenster is verwijderd.
Als je expliciet UGREENlink gebruikt, opent het officiële UGREENlink-aanmeldscherm
in een afzonderlijke pop-up met hetzelfde eigen WebView2-profiel. Dat is geen nieuwe
ongedocumenteerde UGREENlink-wachtwoord-API. De bestaande origin-, popup- en
ticketcontroles blijven gelden. WebView2 Runtime blijft vereist voor deze terugval.

## Verbinding en opslag

- De ingebouwde node gebruikt geen Windows-VPN-adapter, bestaande Tailscale-service,
  SOCKS-proxy, luisterende lokale SMB-poort of publieke bestands-API. De meegebouwde
  transportcomponent communiceert via privé stdin/stdout-pipes met de app en stopt
  bij afsluiten. De installer en portable EXE bevatten de component en licentieteksten.
- De NAS wordt uitsluitend in de eigen aangemelde Tailscale-peerlijst opgezocht.
  Geen openbare DNS-terugval voor het NAS-bestandsverkeer. Alleen de vast ingestelde
  peer op TCP 445 en de share Techbase zijn bereikbaar via het bestandsprotocol.
- Tailscale kiest direct verkeer waar mogelijk en kan DERP/peer-relay gebruiken.
  De app toont de waargenomen route na verbinden; een adres of online node alleen
  bewijst geen P2P. Hiervoor is geen internet-forwarding van SMB-poort 445 nodig.
- De auth key wordt niet in appinstellingen of logs opgeslagen. De officiële
  registratie verwerkt de sleutel tijdelijk in geheugen. De daarna bewaarde
  Tailscale-state is per Windows-gebruiker versleuteld met DPAPI. Bestandsblokken
  gaan als ruwe begrensde bytes door de pipes, zonder base64. Geen offline writes
  en geen automatisch herhalen van onzekere writes na een timeout.
- Relatieve paden blijven binnen Techbase. Rootmutaties, `..`, alternate streams,
  symbolische links en reparse points worden geweigerd.
- Vanaf v0.6.0-preview.1 behoudt ieder geopend bestand zijn SMB-bestandsreferentie.
  Lees-/schrijfoperaties, bestandsgrootte, attributen, hernoemen en verwijderen
  gebruiken dat bestand, ook als hetzelfde pad intussen naar een ander bestand wijst.
  Toegangs- en deelrechten worden naar SMB doorgegeven. Vervangen gebruikt één
  server-side rename; de app verwijdert het bestaande doel niet vooraf.
  Een nog geopend doel kan vervanging blokkeren; die serverfout blijft gelden.
  De CAD-backupvolgorde (oud bestand hernoemen, nieuw bestand plaatsen) behoudt
  de oude bestandsreferentie. Zie [Microsofts SMB-specificatie](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/1d2673a8-8fb9-4868-920a-775ccaa30cf8).
- Schrijven en groottewijzigingen krijgen pas succes na een bevestigde SMB-flush.
  FlushFileBuffers doet een echte flush. Allocatie vergroot de logische lengte niet;
  verkleinen en paging-writes respecteren EOF. Geen pad-readcache op open handles.
  Dit kan meer netwerkverkeer geven; gegevensintegriteit heeft hier voorrang.
- De gebruiker meldt dat Tailscale verbindt, maar dat AutoCAD-opslaan via de oude
  route tekeningen beschadigt; lokaal, LAN-SMB en UGREENlink werken volgens de gebruiker.
  De correctie is nog niet met echte AutoCAD/NAS-bestanden bevestigd. Bewaar de
  oorspronkelijke DXF en goede lokale DWG. Test de update met een kopie en nieuwe
  bestandsnaam. Bestaande corrupte tekeningen worden hiermee niet hersteld.
  Er is geen betrouwbare byte-range-locking tussen verschillende NAS-clients;
  tegelijk dezelfde tekening bewerken blijft buiten de ondersteunde werking.

## Verificatie en bouwen

Windows: gebruik Go volgens `embedded-tailscale/go.mod`, .NET 8 en Python 3.
Voer eerst `embedded-tailscale/Build.ps1` uit, daarna de .NET build/publish. De
component wordt met zijn licenties in de Windows-EXE ingebed. Zonder Go-build kan
een ontwikkelbuild de component niet bevatten; de app meldt dat bij verbinden.

CI toetst paden, peerresolutie, begrensde binaire frames, een echte SMB-roundtrip
op een wegwerpbare Samba-share, versleutelde identity-state en heropenen daarvan,
private helper-pipes en het verplaatsen/verbergen van de UGREENlink-WebView.
Aanvullende tests vergelijken willekeurige schrijfposities/headerupdates byte voor
byte, allocatie versus EOF, delen/exclusieve opens en vervanging met oude open handles.
Een native Dokany-mount met uitsluitend geheugenbestanden draait alleen op de
wegwerpbare Windows-runner na de installerproef. Geen AutoCAD of NAS is daarbij betrokken.
De gepinde SMB-bron heeft een beperkte lokale uitbreiding; de originele bestanden
worden gecontroleerd tegen hun SHA-256-manifest.
De SMB-fixture vervangt uitsluitend in de Go-test de netwerkverbinding; productie
heeft die bypass niet. Er worden geen gebruikersaccounts, echte NAS-bestanden of
Tailscale-sleutels gebruikt. Dit bewijst nog geen echte tailnetregistratie of NAS-mount.

Bronnen: [officiële tsnet-documentatie](https://tailscale.com/docs/features/tsnet),
[auth keys](https://tailscale.com/docs/features/access-control/auth-keys),
[apparaatgoedkeuring](https://tailscale.com/docs/features/access-control/device-management/device-approval),
[key expiry](https://tailscale.com/docs/features/access-control/key-expiry),
[verbindingstypen](https://tailscale.com/docs/reference/connection-types).
