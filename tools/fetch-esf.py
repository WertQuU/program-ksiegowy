#!/usr/bin/env python3
"""Szybkie wyszukanie oficjalnych schematów e-Sprawozdań finansowych (struktury logiczne MF). Uruchamiany w GitHub Actions."""
import io, os, re, socket, subprocess, sys, urllib.parse, urllib.request, zipfile

socket.setdefaulttimeout(20)
OUT = sys.argv[1] if len(sys.argv) > 1 else "schemas/MF/esf"
os.makedirs(OUT, exist_ok=True)
UA = {"User-Agent": "Mozilla/5.0 (pelna-ksiegowosc schema fetcher)"}
log = []


def get(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=20) as r:
        return r.read(), r.geturl(), r.headers.get("Content-Type", "")


def save(name, data):
    path = os.path.join(OUT, re.sub(r"[^A-Za-z0-9._()\-]", "_", name))
    with open(path, "wb") as f:
        f.write(data)
    log.append(f"ZAPISANO {path} ({len(data)} B)")


def handle(url, depth=0, seen=set()):
    if url in seen or len(seen) > 120:
        return
    seen.add(url)
    try:
        data, final, ctype = get(url)
    except Exception as e:
        log.append(f"! {url}: {e}")
        return
    log.append(f"{url} -> {final} [{ctype}] {len(data)} B")
    low = final.lower()
    if data[:2] == b"PK":
        z = zipfile.ZipFile(io.BytesIO(data))
        log.append(f"  ZIP: {z.namelist()[:40]}")
        for n in z.namelist():
            if n.lower().endswith((".xsd", ".xml")):
                save(os.path.basename(n), z.read(n))
        return
    if b"http://www.w3.org/2001/XMLSchema" in data[:4000]:
        u = urllib.parse.urlparse(final)
        rel = os.path.join(u.netloc, u.path.lstrip("/"))
        if not rel.endswith(".xsd"):
            rel += ".xsd"
        full = os.path.join(OUT, rel)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        open(full, "wb").write(data)
        log.append(f"ZAPISANO {full} ({len(data)} B)")
        for loc in re.findall(rb'schemaLocation="([^"]+)"', data):
            handle(urllib.parse.urljoin(final, loc.decode()), 0)
        return
    if data[:4] == b"%PDF":
        p = os.path.join(OUT, "doc.pdf")
        open(p, "wb").write(data)
        txt = subprocess.run(["pdftotext", "-layout", p, "-"], capture_output=True, text=True).stdout
        save(os.path.basename(urllib.parse.urlparse(final).path) + ".txt", txt.encode())
        for u in sorted(set(re.findall(r'https?://[^\s<>")\]]+', txt))):
            handle(u.rstrip(".,;"), depth + 1)
        return
    if depth < 2 and b"<html" in data[:3000].lower():
        text = data.decode("utf-8", "replace")
        if depth == 0:
            save(re.sub(r"\W", "_", final)[-80:] + ".html", data)
        for h in sorted(set(re.findall(r'href="([^"]+)"', text))):
            full = urllib.parse.urljoin(final, h).split("#")[0]
            l2 = full.lower()
            if l2.endswith((".xsd", ".zip")) or "/attachment/" in l2 or ("/media/" in l2 and ("sf" in l2 or "sprawozd" in l2 or "struktur" in l2)) or "/schematy/" in l2:
                handle(full, depth + 1)


# Struktury e-SF w CRWDE (numery wzorów z gov.pl/web/kas/struktury-e-sprawozdan) — jednostki inne / małe / mikro, zł i tys. zł
for wz in ["2025/08/07/13817", "2025/08/04/13810", "2025/08/07/13818", "2025/09/10/13846", "2025/08/12/13821", "2025/08/11/13819"]:
    handle(f"http://crd.gov.pl/wzor/{wz}/schemat.xsd")

for u in [
    "https://www.gov.pl/web/kas/struktury-e-sprawozdan",
    "https://www.podatki.gov.pl/e-sprawozdania-finansowe/",
    "http://www.mf.gov.pl/schematy/SF/DefinicjeTypySprawozdaniaFinansowe/2018/07/09/",
    "https://e-sprawozdania.mf.gov.pl/ap/",
]:
    handle(u)

open(os.path.join(OUT, "log.txt"), "w", encoding="utf-8").write("\n".join(log))
print("\n".join(log))
