#!/bin/sh
set -eu
mkdir -p /fixture/Techbase
useradd --no-create-home --shell /usr/sbin/nologin fixture
printf '%s\n%s\n' fixture-test-only fixture-test-only | smbpasswd -s -a fixture >/dev/null
chown fixture:fixture /fixture/Techbase
chmod 700 /fixture/Techbase
cat >/tmp/smb.conf <<'CONFIG'
[global]
server role = standalone server
server min protocol = SMB3
map to guest = Never
load printers = no
disable spoolss = yes
smb ports = 445
log level = 0
logging = syslog
[Techbase]
path = /fixture/Techbase
valid users = fixture
read only = no
browseable = yes
guest ok = no
CONFIG
exec smbd --foreground --no-process-group --configfile=/tmp/smb.conf
