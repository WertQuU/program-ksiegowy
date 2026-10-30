#!/usr/bin/env python3
"""Pobranie oficjalnych schematów e-Deklaracji (MF, CRWDE) oraz KEDU (ZUS Płatnik). Uruchamiany w GitHub Actions."""
import html, io, os, re, socket, sys, urllib.parse, urllib.request, zipfile

socket.setdefaulttimeout(20)
OUT = sys.argv[1] if len(sys.argv) > 1 else "schemas/MF/edek"
os.makedirs(OUT, exist_ok=True)
UA = {"User-Agent": "Mozilla/5.0 (pelna-ksiegowosc schema fetcher)"}
WANTED = ["CIT-8", "PIT-11", "PIT-4R", "PIT-8AR", "VAT-UE", "IFT-2R", "CIT-ST", "CIT/BR", "CIT/8S", "CIT/O", "CIT/D", "PIT-36L", "PIT-36", "PIT/B", "PIT/O", "PIT/D", "PIT-28"]
log, seen = [], set()


def get(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=20) as r:
        return r.read(), r.geturl(), r.headers.get("Content-Type", "")


def store(final, data, ext=".xsd"):
    u = urllib.parse.urlparse(final)
    rel = os.path.join(u.netloc, u.path.lstrip("/"))
    if not rel.endswith(ext):
        rel += ext
    full = os.path.join(OUT, rel)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    open(full, "wb").write(data)
    log.append(f"ZAPISANO {full} ({len(data)} B)")


def handle(url, label="", depth=0, follow=lambda u, t: False):
    if url in seen or len(seen) > 400:
        return
    seen.add(url)
    try:
        data, final, ctype = get(url)
    except Exception as e:
        log.append(f"! {url}: {e}")
        return
    log.append(f"{label} | {url} -> {final} [{ctype}] {len(data)} B")
    if data[:2] == b"PK":
        z = zipfile.ZipFile(io.BytesIO(data))
        log.append(f"  ZIP: {z.namelist()[:60]}")
        for n in z.namelist():
            if n.lower().endswith((".xsd", ".xml", ".pdf")) and len(n) < 200:
                p = os.path.join(OUT, "zip", re.sub(r"[^A-Za-z0-9._()\-]", "_", os.path.basename(url.rstrip("/"))), re.sub(r"[^A-Za-z0-9._()\-/]", "_", n))
                os.makedirs(os.path.dirname(p), exist_ok=True)
                open(p, "wb").write(z.read(n))
                log.append(f"ZAPISANO {p}")
        return
    if b"http://www.w3.org/2001/XMLSchema" in data[:6000]:
        store(final, data)
        if label:
            open(os.path.join(OUT, "wzory.txt"), "a", encoding="utf-8").write(f"{label}\t{final}\n")
        for loc in re.findall(rb'schemaLocation="([^"]+)"', data):
            handle(urllib.parse.urljoin(final, loc.decode()), "", depth)
        return
    if depth < 2 and b"<html" in data[:4000].lower():
        text = data.decode("utf-8", "replace")
        name = re.sub(r"\W", "_", final)[-90:] + ".html"
        open(os.path.join(OUT, name), "w", encoding="utf-8").write(text)
        for m in re.finditer(r'href="([^"]+)"', text):
            full = urllib.parse.urljoin(final, html.unescape(m.group(1))).split("#")[0]
            ctx = re.sub(r"<[^>]+>", " ", text[max(0, m.start() - 700):m.start() + 300])
            ctx = re.sub(r"\s+", " ", html.unescape(ctx))
            if follow(full, ctx):
                if "crd.gov.pl/wzor/" in full and not full.endswith(".xsd"):
                    full = full.rstrip("/") + "/schemat.xsd"
                handle(full, ctx[-200:], depth + 1, follow)


def mf(u, ctx):
    l = u.lower()
    return ("crd.gov.pl/wzor/" in l and any(w in ctx for w in WANTED)) or (l.endswith(".xsd") and "crd.gov.pl" in l) \
        or ("podatki.gov.pl/e-deklaracje/dokumentacja-it" in l)


def zus(u, ctx):
    l = u.lower()
    return ("kedu" in l or "kedu" in ctx.lower()) and (l.endswith((".xsd", ".zip")) or "/documents/" in l or "zus.pl" in l)


for start in [
    "https://www.podatki.gov.pl/e-deklaracje/dokumentacja-it/struktury-dokumentow-xml/",
    "https://www.podatki.gov.pl/e-deklaracje/dokumentacja-it/",
]:
    handle(start, "", 0, mf)
for start in [
    "https://www.zus.pl/firmy/programy-i-aplikacje/program-platnik/dokumentacja-techniczna",
    "https://www.zus.pl/baza-wiedzy/biblioteka-materialow/dokumentacja-techniczna",
    "https://www.zus.pl/en/baza-wiedzy/biblioteka-materialow/dokumentacja-techniczna",
]:
    handle(start, "", 0, zus)

open(os.path.join(OUT, "log.txt"), "w", encoding="utf-8").write("\n".join(log))
print("\n".join(log[-300:]))
