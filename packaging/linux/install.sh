#!/bin/sh
# Instalacja dla bieżącego użytkownika (bez uprawnień administratora):
#   program:  ~/.local/opt/pelnaksiegowosc
#   polecenia: ~/.local/bin/pelnaksiegowosc, ~/.local/bin/pk-cli
#   skrót w menu aplikacji i ikona
# Dane ksiąg są przechowywane osobno w ~/.local/share/PelnaKsiegowosc i nie są zmieniane przez instalację ani deinstalację.
set -eu
SRC="$(cd "$(dirname "$0")" && pwd)"
DEST="${HOME}/.local/opt/pelnaksiegowosc"
BIN="${HOME}/.local/bin"
APPS="${HOME}/.local/share/applications"
ICONS="${HOME}/.local/share/icons/hicolor/256x256/apps"

mkdir -p "$DEST" "$BIN" "$APPS" "$ICONS"
rm -rf "$DEST/program"
cp -a "$SRC/program" "$DEST/program"
chmod +x "$DEST/program/PelnaKsiegowosc" "$DEST/program/pk-cli"
ln -sf "$DEST/program/PelnaKsiegowosc" "$BIN/pelnaksiegowosc"
ln -sf "$DEST/program/pk-cli" "$BIN/pk-cli"
cp "$SRC/pelnaksiegowosc.png" "$ICONS/pelnaksiegowosc.png"
sed "s|^Exec=.*|Exec=$DEST/program/PelnaKsiegowosc|" "$SRC/pelnaksiegowosc.desktop" > "$APPS/pelnaksiegowosc.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS" >/dev/null 2>&1 || true

echo "Zainstalowano Pełna Księgowość w $DEST"
echo "Uruchom z menu aplikacji albo poleceniem: pelnaksiegowosc"
command -v secret-tool >/dev/null 2>&1 || echo "Wskazówka: zainstaluj pakiet libsecret-tools (secret-tool), aby hasła i klucze były przechowywane w pęku kluczy systemu."
case ":$PATH:" in *":$BIN:"*) ;; *) echo "Uwaga: katalog $BIN nie jest w zmiennej PATH." ;; esac
