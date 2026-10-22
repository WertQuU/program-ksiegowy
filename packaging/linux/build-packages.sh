#!/bin/sh
# Buduje pakiety dla Linuksa z katalogu publikacji (dotnet publish -r linux-x64 --self-contained):
#   PelnaKsiegowosc-<wersja>-linux-x64.tar.gz  — archiwum z install.sh (instalacja bez uprawnień administratora)
#   pelnaksiegowosc_<wersja>_amd64.deb         — pakiet dla Debiana/Ubuntu (instalacja w /opt/pelnaksiegowosc)
# Użycie: packaging/linux/build-packages.sh <katalog-publikacji> <wersja> <katalog-wyjściowy>
set -eu
PUB="$(cd "$1" && pwd)"
VER="$2"
OUT="$(mkdir -p "$3" && cd "$3" && pwd)"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
ICON="$ROOT/src/PelnaKsiegowosc.App/Assets/ikona-256.png"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# --- tar.gz ---
T="$WORK/PelnaKsiegowosc-$VER-linux-x64"
mkdir -p "$T"
cp -a "$PUB" "$T/program"
find "$T/program" -name '*.pdb' -delete
cp "$HERE/install.sh" "$HERE/uninstall.sh" "$HERE/pelnaksiegowosc.desktop" "$T/"
cp "$ICON" "$T/pelnaksiegowosc.png"
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$T/"
chmod +x "$T/install.sh" "$T/uninstall.sh" "$T/program/PelnaKsiegowosc" "$T/program/pk-cli"
tar -C "$WORK" -czf "$OUT/PelnaKsiegowosc-$VER-linux-x64.tar.gz" "PelnaKsiegowosc-$VER-linux-x64"

# --- deb ---
D="$WORK/deb"
mkdir -p "$D/DEBIAN" "$D/opt/pelnaksiegowosc" "$D/usr/bin" "$D/usr/share/applications" "$D/usr/share/icons/hicolor/256x256/apps" "$D/usr/share/doc/pelnaksiegowosc"
cp -a "$T/program/." "$D/opt/pelnaksiegowosc/"
ln -s /opt/pelnaksiegowosc/PelnaKsiegowosc "$D/usr/bin/pelnaksiegowosc"
ln -s /opt/pelnaksiegowosc/pk-cli "$D/usr/bin/pk-cli"
cp "$HERE/pelnaksiegowosc.desktop" "$D/usr/share/applications/"
cp "$ICON" "$D/usr/share/icons/hicolor/256x256/apps/pelnaksiegowosc.png"
cp "$ROOT/LICENSE" "$D/usr/share/doc/pelnaksiegowosc/copyright"
SIZE="$(du -sk "$D/opt" | cut -f1)"
cat > "$D/DEBIAN/control" <<CTRL
Package: pelnaksiegowosc
Version: $VER
Section: misc
Priority: optional
Architecture: amd64
Installed-Size: $SIZE
Depends: libc6, libgcc-s1, libstdc++6, libssl3 | libssl3t64, zlib1g, libicu74 | libicu72 | libicu70 | libicu-dev, libfontconfig1, libx11-6, libice6, libsm6, libxext6, libxrandr2, libxi6, libxcursor1
Recommends: libsecret-tools, fonts-dejavu-core | fonts-liberation
Maintainer: Kacper Baca
Homepage: https://github.com/WertQuU/program-ksiegowy
Description: Pełna Księgowość — program do prowadzenia ksiąg rachunkowych
 Księgi rachunkowe (dziennik, księga główna, rozrachunki), VAT i JPK_V7M,
 faktury FA(3) i KSeF, środki trwałe, CIT, kapitały, zamknięcie roku,
 sprawozdania, szyfrowane kopie zapasowe. Dane lokalnie (SQLite) lub na
 serwerze PostgreSQL. Licencja: użytkowanie bez prawa sprzedaży (plik copyright).
CTRL
chmod -R u=rwX,go=rX "$D"
chmod 755 "$D/opt/pelnaksiegowosc/PelnaKsiegowosc" "$D/opt/pelnaksiegowosc/pk-cli"
if command -v fakeroot >/dev/null 2>&1; then fakeroot dpkg-deb --build "$D" "$OUT/pelnaksiegowosc_${VER}_amd64.deb"
else dpkg-deb --root-owner-group --build "$D" "$OUT/pelnaksiegowosc_${VER}_amd64.deb"; fi
ls -la "$OUT"
