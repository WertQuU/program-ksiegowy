#!/usr/bin/env python3
"""Pobranie najnowszych oficjalnych schematów e-Deklaracji (CRWDE) i dokumentacji KEDU (ZUS). Uruchamiany w GitHub Actions."""
import html, io, os, re, socket, sys, time, urllib.parse, urllib.request, zipfile

socket.setdefaulttimeout(25)
OUT = sys.argv[1] if len(sys.argv) > 1 else "schemas/MF/edek"
os.makedirs(OUT, exist_ok=True)
UA = {"User-Agent": "Mozilla/5.0 (pelna-ksiegowosc schema fetcher)"}
FORMS = ["CIT-8", "PIT-11", "PIT-4R", "PIT-8AR", "VAT-UE", "IFT-2R", "CIT-ST", "PIT-36L", "PIT-36", "PIT-28", "CIT-8AB", "PIT/B"]
START = time.time()
log, seen, links = [], set(), []


def get(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=25) as r:
        return r.read(), r.geturl(), r.headers.get("Content-Type", "")


def store_xsd(final, data, label=""):
    u = urllib.parse.urlparse(final)
    rel = os.path.join(u.netloc, u.path.lstrip("/"))
    if not rel.endswith(".xsd"):
        rel += ".xsd"
    full = os.path.join(OUT, rel)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    open(full, "wb").write(data)
    log.append(f"ZAPISANO {full} ({len(data)} B) {label}")
    if label:
        open(os.path.join(OUT, "wzory.txt"), "a", encoding="utf-8").write(f"{label}\t{final}\t{rel}\n")
    for loc in re.findall(rb'schemaLocation="([^"]+)"', data):
        fetch_xsd(urllib.parse.urljoin(final, loc.decode()))


def fetch_xsd(url, label=""):
    if url in seen or time.time() - START > 480:
        return
    seen.add(url)
    try:
        data, final, ctype = get(url)
    except Exception as e:
        log.append(f"! {url}: {e}")
        return
    if data[:2] == b"PK":
        z = zipfile.ZipFile(io.BytesIO(data))
        d = os.path.join(OUT, "zip", re.sub(r"[^A-Za-z0-9._()\-]", "_", os.path.basename(urllib.parse.urlparse(final).path) or "plik"))
        for n in z.namelist():
            if n.lower().endswith((".xsd", ".xml", ".pdf", ".txt")):
                p = os.path.join(d, re.sub(r"[^A-Za-z0-9._()\-/]", "_", n))
                os.makedirs(os.path.dirname(p), exist_ok=True)
                open(p, "wb").write(z.read(n))
                log.append(f"ZAPISANO {p} {label}")
        return
    if b"http://www.w3.org/2001/XMLSchema" in data[:8000]:
        store_xsd(final, data, label)
    else:
        log.append(f"? {url} -> {final} [{ctype}] {len(data)} B (nie XSD)")


def page(url, name):
    try:
        data, final, ctype = get(url)
    except Exception as e:
        log.append(f"! strona {url}: {e}")
        return None, None
    text = data.decode("utf-8", "replace")
    open(os.path.join(OUT, name + ".html"), "w", encoding="utf-8").write(text)
    log.append(f"STRONA {url} -> {final} ({len(data)} B)")
    return text, final


def anchors(text, base):
    for m in re.finditer(r'href="([^"]+)"', text):
        full = urllib.parse.urljoin(base, html.unescape(m.group(1))).split("#")[0]
        ctx = re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", " ", text[max(0, m.start() - 600):m.start() + 200])))
        yield full, ctx


def version(ctx, form):
    found = [int(v) for v in re.findall(re.escape(form) + r"\s*\((\d+)\)", ctx)]
    return found[-1] if found else None


# 1) Wykaz struktur (archiwum i aktualny serwis): najnowsza wersja każdego formularza
best = {}
for i, url in enumerate([
    "https://www.podatki.gov.pl/e-deklaracje/dokumentacja-it/struktury-dokumentow-xml/",
    "https://podatki-arch.mf.gov.pl/e-deklaracje/dokumentacja-it/struktury-dokumentow-xml/",
    "https://www.gov.pl/web/kas/struktury-dokumentow-xml",
    "https://www.gov.pl/web/kas/e-deklaracje",
    "https://www.podatki.gov.pl/e-deklaracje/",
    "https://www.podatki.gov.pl/e-deklaracje/dokumentacja-it/",
]):
    text, final = page(url, f"wykaz{i}")
    if not text:
        continue
    for full, ctx in anchors(text, final):
        if "crd.gov.pl/wzor/" in full or full.lower().endswith(".xsd"):
            links.append(f"{full}\t{ctx[-260:]}")
            for f in FORMS:
                v = version(ctx[-260:], f)
                if v is not None and "crd.gov.pl/wzor/" in full:
                    key = f
                    wz = re.search(r"crd\.gov\.pl/wzor/(\d{4}/\d{2}/\d{2}/\d+)", full).group(1)
                    if key not in best or v > best[key][0]:
                        best[key] = (v, wz)
open(os.path.join(OUT, "odnosniki.txt"), "w", encoding="utf-8").write("\n".join(sorted(set(links))))
for f, (v, wz) in sorted(best.items()):
    log.append(f"NAJNOWSZY {f}({v}) {wz}")
    fetch_xsd(f"http://crd.gov.pl/wzor/{wz}/schemat.xsd", f"{f}({v})")

# 2) ZUS — dokumentacja KEDU (struktura dokumentów programu Płatnik)
for i, url in enumerate([
    "https://www.zus.pl/firmy/programy-i-aplikacje/program-platnik",
    "https://www.zus.pl/firmy/programy-i-aplikacje/program-platnik/dokumentacja-techniczna",
    "https://www.zus.pl/baza-wiedzy/biblioteka-materialow/dokumentacja-techniczna",
    "https://www.zus.pl/o-zus/akty-prawne/dokumentacja-techniczna",
]):
    text, final = page(url, f"zus{i}")
    if not text:
        continue
    for full, ctx in anchors(text, final):
        l = full.lower()
        if "kedu" in l or "kedu" in ctx.lower() or l.endswith((".xsd", ".zip")):
            links.append(f"ZUS\t{full}\t{ctx[-200:]}")
            if (l.endswith((".xsd", ".zip")) or "/documents/" in l) and ("kedu" in l or "kedu" in ctx.lower()):
                fetch_xsd(full, "KEDU")
open(os.path.join(OUT, "odnosniki.txt"), "w", encoding="utf-8").write("\n".join(sorted(set(links))))
open(os.path.join(OUT, "log.txt"), "w", encoding="utf-8").write("\n".join(log))
print("\n".join(log))
