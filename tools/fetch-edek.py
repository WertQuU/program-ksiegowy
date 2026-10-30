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


# 1) Wykaz struktur (aktualny serwis podatki.gov.pl i archiwum): najnowsza wersja każdego formularza
best = {}


def scan(text, final):
    for full, ctx in anchors(text, final):
        if "crd.gov.pl/wzor/" in full or full.lower().endswith(".xsd"):
            links.append(f"{full}\t{ctx[-260:]}")
            m = re.search(r"crd\.gov\.pl/wzor/(\d{4}/\d{2}/\d{2}/\d+)", full)
            if not m:
                continue
            for f in FORMS:
                v = version(ctx[-260:], f)
                if v is not None and (f not in best or v > best[f][0]):
                    best[f] = (v, m.group(1))


queue = [
    "https://podatki.gov.pl/e-deklaracje-zloz-deklaracje-podatkowa-online/bramka-ubd-i-dokumentacja-it",
    "https://podatki.gov.pl/podatki-firmowe/cit/formularze-cit",
    "https://podatki.gov.pl/podatki-firmowe/pit/formularze-pit",
    "https://podatki.gov.pl/podatki-firmowe/vat/formularze-vat",
    "https://podatki-arch.mf.gov.pl/e-deklaracje/dokumentacja-it/struktury-dokumentow-xml/",
]
visited = set()
i = 0
while queue and i < 40 and time.time() - START < 300:
    url = queue.pop(0)
    if url in visited:
        continue
    visited.add(url)
    text, final = page(url, f"wykaz{i}")
    i += 1
    if not text:
        continue
    scan(text, final)
    for full, ctx in anchors(text, final):
        l = full.lower()
        if "podatki.gov.pl" in l and ("dokumentacja-it" in l or "struktur" in l or "formularze-" in l) and full not in visited and len(queue) < 60:
            queue.append(full)
open(os.path.join(OUT, "odnosniki.txt"), "w", encoding="utf-8").write("\n".join(sorted(set(links))))
for f, (v, wz) in sorted(best.items()):
    log.append(f"NAJNOWSZY {f}({v}) {wz}")
    fetch_xsd(f"http://crd.gov.pl/wzor/{wz}/schemat.xsd", f"{f}({v})")

# 2) ZUS — dokumentacja KEDU (struktura dokumentów programu Płatnik): przeszukanie serwisu od strony głównej
zq, zv, zi = ["https://www.zus.pl/", "https://www.zus.pl/firmy/programy-i-aplikacje"], set(), 0
while zq and zi < 25 and time.time() - START < 500:
    url = zq.pop(0)
    if url in zv:
        continue
    zv.add(url)
    text, final = page(url, f"zus{zi}")
    zi += 1
    if not text:
        continue
    for full, ctx in anchors(text, final):
        l = full.lower()
        if "kedu" in l or "kedu" in ctx.lower() or l.endswith((".xsd", ".zip")):
            links.append(f"ZUS\t{full}\t{ctx[-200:]}")
            if l.endswith((".xsd", ".zip")) or "/documents/" in l:
                fetch_xsd(full, "KEDU")
        elif "zus.pl" in l and any(k in l for k in ("platnik", "płatnik", "dokumentacja", "programy")) and full not in zv and len(zq) < 40:
            zq.append(full)
open(os.path.join(OUT, "odnosniki.txt"), "w", encoding="utf-8").write("\n".join(sorted(set(links))))
open(os.path.join(OUT, "log.txt"), "w", encoding="utf-8").write("\n".join(log))
print("\n".join(log))
