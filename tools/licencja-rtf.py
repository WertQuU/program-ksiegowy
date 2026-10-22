#!/usr/bin/env python3
"""Generuje installer/Licencja.rtf (okno licencji instalatora MSI) z pliku LICENSE."""
import pathlib
root = pathlib.Path(__file__).resolve().parent.parent
text = (root / "LICENSE").read_text(encoding="utf-8")
def esc(ch):
    if ch in "\\{}": return "\\" + ch
    if ord(ch) > 127: return "\\u%d?" % (ord(ch) if ord(ch) < 32768 else ord(ch) - 65536)
    return ch
body = "\\par\n".join("".join(esc(c) for c in line) for line in text.splitlines())
rtf = "{\\rtf1\\ansi\\ansicpg1250\\deff0{\\fonttbl{\\f0\\fswiss Segoe UI;}}\\fs18\n" + body + "\n}"
(root / "installer" / "Licencja.rtf").write_text(rtf, encoding="ascii")
