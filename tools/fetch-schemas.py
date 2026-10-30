#!/usr/bin/env python3
"""Pobiera oficjalne schematy XSD Ministerstwa Finansów (JPK_V7M/V7K, JPK_KR_PD, e-Sprawozdania) wraz ze schematami
importowanymi. Uruchamiany w GitHub Actions (środowisko z dostępem do serwerów MF). Wynik: katalog schemas/MF
+ manifest.json (adres, plik, targetNamespace, element główny, SHA-256) i strony.txt (znalezione odnośniki)."""
import hashlib, json, os, re, sys, urllib.request, urllib.parse, xml.etree.ElementTree as ET, zipfile, io

OUT = sys.argv[1] if len(sys.argv) > 1 else "schemas/MF"
PAGES = [
    "https://www.podatki.gov.pl/jednolity-plik-kontrolny/jpk_vat-z-deklaracja/",
    "https://www.podatki.gov.pl/jpk/jpk-vat-z-deklaracja/",
    "https://www.podatki.gov.pl/jpk/jpk_vat-z-deklaracja/",
    "https://www.podatki.gov.pl/jpk/jpk-vat-z-deklaracja/schematy-xsd/",
    "https://www.podatki.gov.pl/jednolity-plik-kontrolny/jpk-cit/",
    "https://www.podatki.gov.pl/jpk/jpk-cit/",
    "https://www.podatki.gov.pl/jpk/jpk-cit/struktury-jpk-cit/",
    "https://www.podatki.gov.pl/jpk/",
    "https://www.podatki.gov.pl/e-sprawozdania-finansowe/",
    "https://www.podatki.gov.pl/e-sprawozdania-finansowe/struktury-logiczne-e-sprawozdan-finansowych/",
    "https://www.gov.pl/web/kas/struktury-e-sprawozdan",
    "https://www.gov.pl/web/kas/struktury-jpk",
    "https://www.gov.pl/web/kas/struktury-jpk-w-podatkach-dochodowych",
    "https://podatki-arch.mf.gov.pl/e-sprawozdania-finansowe/",
    "https://podatki-arch.mf.gov.pl/e-sprawozdania-finansowe/struktury-e-sprawozdan/",
    "https://podatki-arch.mf.gov.pl/e-sprawozdania-finansowe/struktury-logiczne/",
    "https://www.podatki.gov.pl/wyszukiwarka/?q=JednostkaInnaWZlotych",
    "https://www.gov.pl/web/kas/wyszukiwarka?query=JednostkaInnaWZlotych",
    "https://www.gov.pl/web/finanse/struktury-e-sprawozdan",
    "https://www.gov.pl/web/finanse/struktury-logiczne-e-sprawozdan-finansowych",
    "https://www.gov.pl/web/kas/struktury-logiczne-e-sprawozdan-finansowych",
]
SEEDS = [
    "http://crd.gov.pl/wzor/2025/12/19/14090/schemat.xsd",
    "http://www.mf.gov.pl/schematy/SF/DefinicjeTypySprawozdaniaFinansowe/2018/07/09/JednostkaInnaWZlotych(1)_v1-3.xsd",
    "https://www.mf.gov.pl/schematy/SF/DefinicjeTypySprawozdaniaFinansowe/2018/07/09/JednostkaInnaWZlotych(1)_v1-3.xsd",
    "http://www.mf.gov.pl/schematy/SF/DefinicjeTypySprawozdaniaFinansowe/2018/07/09/DefinicjeTypySprawozdaniaFinansowe_v1-2.xsd",
]
UA = {"User-Agent": "Mozilla/5.0 (pelna-ksiegowosc schema fetcher)"}
XS = "{http://www.w3.org/2001/XMLSchema}"


def get(url, timeout=60):
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read(), r.geturl()


def local_path(url):
    u = urllib.parse.urlparse(url)
    return os.path.join(OUT, u.netloc, u.path.lstrip("/"))


manifest, seen, links = [], set(), []


def save_xsd(url, data):
    if url in seen:
        return
    seen.add(url)
    try:
        root = ET.fromstring(data)
    except ET.ParseError as e:
        print(f"  ! niepoprawny XML {url}: {e}")
        return
    if root.tag != XS + "schema":
        print(f"  ! nie schemat {url}")
        return
    path = local_path(url)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)
    elems = [e.get("name") for e in root.findall(XS + "element")]
    manifest.append({"url": url, "file": os.path.relpath(path, OUT), "targetNamespace": root.get("targetNamespace"),
                     "elements": elems, "sha256": hashlib.sha256(data).hexdigest()})
    print(f"  + {url} -> {elems[:3]} ns={root.get('targetNamespace')}")
    for imp in root.iter():
        if imp.tag in (XS + "import", XS + "include") and imp.get("schemaLocation"):
            child = urllib.parse.urljoin(url, imp.get("schemaLocation"))
            fetch_xsd(child)


def fetch_xsd(url):
    if url in seen:
        return
    try:
        data, _ = get(url)
    except Exception as e:
        print(f"  ! {url}: {e}")
        seen.add(url)
        return
    save_xsd(url, data)


def fetch_zip(url):
    try:
        data, _ = get(url)
        z = zipfile.ZipFile(io.BytesIO(data))
    except Exception as e:
        print(f"  ! zip {url}: {e}")
        return
    for n in z.namelist():
        if n.lower().endswith(".xsd"):
            save_xsd(url + "#" + n, z.read(n))


KEYWORDS = ("struktur", "jpk", "sprawozd", "schemat", "xsd", "kr_pd", "kr-pd", "cit")
all_hrefs = []
queue = [(p, 0) for p in PAGES]
visited = set()
while queue:
    page, depth = queue.pop(0)
    if page in visited:
        continue
    visited.add(page)
    try:
        html, final = get(page)
    except Exception as e:
        print(f"strona {page}: {e}")
        continue
    ctype_zip = html[:2] == b"PK"
    if ctype_zip:
        links.append((page, page))
        continue
    text = html.decode("utf-8", "replace")
    found = set(re.findall(r'href="([^"]+)"', text))
    for h in sorted(found):
        full = urllib.parse.urljoin(final, h).split("#")[0]
        low = full.lower()
        all_hrefs.append(f"{page}\t{full}")
        if low.endswith(".xsd") or low.endswith(".zip") or "crd.gov.pl/wzor" in low or "/attachment/" in low or "/media/" in low and not low.endswith((".jpg", ".png", ".pdf", ".svg")):
            links.append((page, full))
        elif depth < 2 and urllib.parse.urlparse(full).netloc.endswith(("podatki.gov.pl", "www.gov.pl", "podatki-arch.mf.gov.pl", "mf.gov.pl")) and any(k in low for k in KEYWORDS):
            queue.append((full, depth + 1))
links = list(dict.fromkeys(links))

for page, link in links:
    print(f"[{page}] {link}")
    low = link.lower()
    if low.endswith(".zip"):
        fetch_zip(link)
    elif low.endswith(".xsd"):
        fetch_xsd(link)
    elif "crd.gov.pl/wzor" in low:
        fetch_xsd(link.rstrip("/") + ("/schemat.xsd" if not low.endswith("schemat.xsd") else ""))
    else:
        try:
            data, _ = get(link)
            if data[:2] == b"PK":
                z = zipfile.ZipFile(io.BytesIO(data))
                for n in z.namelist():
                    if n.lower().endswith(".xsd"):
                        save_xsd(link + "#" + n, z.read(n))
            elif b"schema" in data[:3000] and b"http://www.w3.org/2001/XMLSchema" in data[:3000]:
                save_xsd(link, data)
        except Exception as e:
            print(f"  ! {link}: {e}")

for s in SEEDS:
    fetch_xsd(s)

os.makedirs(OUT, exist_ok=True)
with open(os.path.join(OUT, "manifest.json"), "w", encoding="utf-8") as f:
    json.dump(sorted(manifest, key=lambda m: m["url"]), f, ensure_ascii=False, indent=1)
with open(os.path.join(OUT, "odnosniki.txt"), "w", encoding="utf-8") as f:
    f.write("\n".join(sorted(set(all_hrefs))))
with open(os.path.join(OUT, "strony.txt"), "w", encoding="utf-8") as f:
    for page, link in links:
        f.write(f"{page}\t{link}\n")
print(f"Schematy: {len(manifest)}; odnośniki: {len(links)}")
