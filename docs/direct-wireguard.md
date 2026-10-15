# Directe Techbase-verbinding met WireGuard

Dit is een afzonderlijk Docker-project voor een eigen directe VPN:

**Windows → versleutelde WireGuard-tunnel → Docker op de NAS → NAS-SMB → Techbase.**

Er zit geen UGREENlink-, Tailscale- of andere relay in dit bestandspad. Bij een
onbereikbare thuisverbinding werkt deze WireGuard-route niet. De bestaande
UGREENlink-container en zijn loopback-poort blijven ongewijzigd.

## Nodig vóór installatie

- Beheer van de router/firewall op het netwerk waar de NAS staat.
- Een bereikbaar eigen publiek **IPv4-adres**, eventueel met DDNS-hostnaam. Deze
  eerste versie publiceert alleen IPv4. Een UGREENlink-URL is hiervoor ongeschikt.
- Het vaste LAN-IPv4-adres van de NAS en werkende SMB op de NAS.
- Een UGOS-account dat uitsluitend de benodigde lees-/schrijfrechten op
  **Techbase** heeft. De VPN-proxy beperkt tot één NAS-adres en TCP 445, maar kan
  geen afzonderlijke SMB-shares filteren. De NAS-accountrechten bepalen dat;
  de Windows-app weigert daarnaast iedere andere sharenaam of submap-root.
- `/dev/net/tun` en Docker-ondersteuning voor `NET_ADMIN` op de NAS. De container
  kiest de kernelimplementatie als die aanwezig is en anders `wireguard-go`.
  Hij krijgt geen `privileged`, `host network`, `SYS_MODULE` of hostmap-mount.
- Het subnet `10.203.0.0/24` mag niet al voor een ander lokaal/VPN-netwerk worden
  gebruikt. Het gegenereerde profiel routeert uitsluitend `10.203.0.1/32`.

Een vrij poortnummer op Windows bewijst geen inkomende bereikbaarheid van de
NAS. Zonder router-WAN-adres kan dubbele NAT/CGNAT niet worden uitgesloten.
Een Ubiquiti-accesspoint of switch is niet noodzakelijk de router/firewall.

## Docker-project maken

1. Gebruik `docker-compose.wireguard.yaml` in een **nieuw** UGREEN Docker-project
   `techbase-wireguard`; vervang het bestaande Remote Drive-project niet.
2. Vul de projectvariabelen in volgens `wireguard.env.example`:
   `NAS_ADDRESS` is het NAS-LAN-adres; `WG_ENDPOINT` je eigen publieke IPv4 of
   DDNS-hostnaam zonder `https://` of poort; `WG_PORT` standaard `51820`;
   `UGREEN_DRIVE_REF=v0.4.0-preview.1`.
3. Bouw en start het project. Er worden geen NAS-bestandsmappen gemount. De
   privésleutels en het Windows-profiel staan in het eigen Docker-volume
   `wireguard-state`. Verwijder dat volume niet bij een gewone update.
4. Alleen **UDP 51820** (of de gekozen `WG_PORT`) wordt op de NAS gepubliceerd.
   TCP 445 wordt uitsluitend op het interne tunneladres geopend. Er is geen
   publiek SMB-, API- of beheerwebportaal.

De container logt geen sleutels, privéprofielen, NAS-adressen of wachtwoorden.
De healthcheck geeft alleen aan of de lokale tunnel en SMB-doorgang draaien;
een actieve externe verbinding is daarmee nog niet bewezen.

## Routerinstelling en bereikbaarheid

Laat de netwerkbeheerder eerst het WAN-IPv4-adres vergelijken met het publieke
IPv4 van het NAS-netwerk. Bij een privaat WAN-adres of `100.64.0.0/10` kan sprake
zijn van dubbele NAT/CGNAT. Een extra router vereist een extra doorstuurregel;
bij CGNAT kan een bereikbaar publiek adres van de provider nodig zijn.

Stel op de echte router één regel in: **UDP, externe WG_PORT → vast NAS-LAN-IP,
dezelfde WG_PORT**. Docker stuurt die poort door naar de interne UDP 51820.
Open TCP 445 niet op internet. Deze repository maakt geen automatische
UPnP-regels en verandert geen router- of NAS-firewallinstellingen.

Bij een UniFi Gateway hangt het scherm van de versie af: Network 9.4 gebruikt
Settings → Policy Engine → Port Forwarding; Network 9.3 gebruikt Settings →
Policy Table → Create New Policy → Port Forwarding. Alleen een Ubiquiti-punt
hebben betekent niet dat je deze gatewayinstellingen kunt beheren.

Zie [Ubiquiti over port forwarding en dubbele NAT/CGNAT](https://help.ui.com/hc/en-us/articles/235723207-UniFi-Gateway-Port-Forwarding).

## Windows-profiel en schijf

1. Installeer de [officiële WireGuard-app voor Windows](https://www.wireguard.com/install/).
   Dit installeert een VPN-netwerkadapter; de Remote Drive-installer installeert
   deze afzonderlijke VPN-app niet automatisch.
2. Exporteer het privéprofiel met Docker op de NAS naar een privébestand:

   ```sh
   docker cp ugreen-techbase-wireguard:/config/Techbase-direct.conf ./Techbase-direct.conf
   ```

   Gebruik hiervoor een bestaande, geautoriseerde NAS-beheersessie. De
   repository schakelt geen SSH in. Bewaar/transporteer dit bestand privé;
   het geeft toegang tot de tunnel. Zet het niet in Git, logs of de chat.
3. Importeer het bestand in WireGuard op Windows en activeer de tunnel.
4. Open `\\10.203.0.1\Techbase` één keer in Verkenner en meld aan met het beperkte
   UGOS-SMB-account. Windows beheert die SMB-aanmelding; de app bewaart geen
   NAS-wachtwoord. UGREENlink-aanmelding en SMB-aanmelding staan los van elkaar.
5. Gebruik Remote Drive `v0.4.0-preview.1` of later: vul onder Optioneel bij de
   Techbase SMB-share **`\\10.203.0.1\Techbase`** in. Het UGREENlink-veld mag leeg
   blijven voor uitsluitend direct gebruik. Klik Verbinden en schijf openen.
   De status toont `WireGuard-SMB`. Met een bewaard UGREENlink-adres kan de app
   bij een nieuwe koppeling daarop terugvallen; een lopende koppeling blijft
   op zijn gekozen backend en schakelt niet tijdens een bestandsactie.

De profielnaam en sleutel gelden voor één Windows-apparaat. Deel ditzelfde
profiel niet tussen meerdere computers. Deze eerste versie ondersteunt één
peer. Verlies/intrekking vereist vernieuwen van de peerconfiguratie; ontkoppel
eerst en vervang de privésleutels/profielen uitsluitend via een NAS-beheerder.

## Definitief testen

Test vanaf een ander netwerk, bijvoorbeeld een eigen mobiele hotspot. Een test
vanuit hetzelfde LAN kan hairpin-NAT gebruiken of juist daarop vastlopen.
Een gewone TCP-poortchecker test WireGuard-UDP niet; WireGuard antwoordt niet
bruikbaar op willekeurige pakketjes zonder geldige sleutels.

Controleer de recente handshake in WireGuard of met de beperkte statusopdracht:

```sh
docker exec ugreen-techbase-wireguard python3 /app/app.py status
```

`recentHandshake: true` bevestigt een recente versleutelde peerverbinding.
Controleer daarna eerst alleen bereikbaarheid/aanmelding bij Techbase. Echte
NAS-schrijftests vereisen apart afgesproken tijdelijke testitems. Een handshake
bewijst geen correcte SMB-accountrechten of volledige bestandsintegriteit.

## Wat is en wordt getest

Unit-tests controleren invoer, splitsing van routes, firewallbeperking, private
sleutelopslag en profielgeneratie. GitHub CI bouwt de container en toetst een
echte userspace-WireGuard-handshake, SMB-authenticatie, een bytegetrouwe
upload/download en geblokkeerde algemene forwarding tussen wegwerpcontainers.
Dat is geen bewijs van bereikbaarheid op de echte UGREEN NAS/router, kernel-
of TUN-ondersteuning van die NAS, of werkelijke internetsnelheid.

Bronnen: [WireGuard quick start](https://www.wireguard.com/quickstart/),
[WireGuard userspace-implementatie](https://git.zx2c4.com/wireguard-go/about/).
