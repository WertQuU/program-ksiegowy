#!/bin/sh
# Usuwa program zainstalowany skryptem install.sh. Dane ksiąg (~/.local/share/PelnaKsiegowosc) i kopie zapasowe pozostają.
set -eu
rm -rf "${HOME}/.local/opt/pelnaksiegowosc"
rm -f "${HOME}/.local/bin/pelnaksiegowosc" "${HOME}/.local/bin/pk-cli"
rm -f "${HOME}/.local/share/applications/pelnaksiegowosc.desktop" "${HOME}/.local/share/icons/hicolor/256x256/apps/pelnaksiegowosc.png"
echo "Program usunięty. Dane ksiąg pozostały w ${HOME}/.local/share/PelnaKsiegowosc"
