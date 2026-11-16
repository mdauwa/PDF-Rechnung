"""Erzeugt die eingebetteten EDIFACT-Verzeichnisse (src/PdfRechnung/Edi/Verzeichnisse/*.json.gz) aus den
Daten-Paketen von EDIFlow (https://github.com/ediflow-lib/core, MIT-Lizenz, Copyright (c) 2026 ediflow-lib).

Aufruf: python3 tools/edi_verzeichnisse.py <Pfad zu ediflow/packages>
Übernommen werden nur Strukturdaten (Nachrichtenaufbau, Segmente, Composites, Datenelemente mit Typ/Länge,
Code-Werte) und die englischen Kurznamen aus den UN/EDIFACT-Verzeichnissen – keine Beschreibungstexte.
"""
import json, gzip, sys, pathlib, os

src = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "/tmp/edisrc/ediflow/packages")
dst = pathlib.Path(__file__).resolve().parents[1] / "src/PdfRechnung/Edi/Verzeichnisse"
dst.mkdir(parents=True, exist_ok=True)
DIRS = {"edifact-d96a": "D96A", "edifact-d01b": "D01B", "edifact-d12a": "D12A", "edifact-d20b": "D20B", "eancom-2002": "EANCOM2002"}

def node(x):
    if x["nodeType"] == "segment":
        return [x["segmentRef"], 1 if x["mandatory"] else 0, x["maxOccurrences"]]
    return [x["id"], 1 if x["mandatory"] else 0, x["maxOccurrences"], [node(y) for y in x["structure"]]]

for pkg, key in DIRS.items():
    d = src / pkg / "data"
    segs = json.load(open(d / "segments.json"))
    comps = json.load(open(d / "composites.json"))
    els = json.load(open(d / "elements.json"))
    out = {
        "key": key,
        "segments": {t: {"n": s["name"], "e": [[e.get("elementCode") or e.get("compositeCode"), 1 if e["mandatory"] else 0] for e in sorted(s["elements"], key=lambda e: e["position"])]} for t, s in segs.items()},
        "composites": {c: {"n": v["name"], "e": [[e["elementCode"], 1 if e["mandatory"] else 0] for e in sorted(v["components"], key=lambda e: e["position"])]} for c, v in comps.items()},
        "elements": {c: [v["name"], v["dataType"], v.get("minLength", 0), v.get("maxLength", 0)] for c, v in els.items()},
        "messages": {},
        "codes": {},
    }
    for f in sorted(os.listdir(d / "messages")):
        m = json.load(open(d / "messages" / f))
        out["messages"][m["messageType"]] = {"n": m.get("name"), "s": [node(x) for x in m["structure"]]}
    if (d / "codes").exists():
        for f in sorted(os.listdir(d / "codes")):
            c = json.load(open(d / "codes" / f))
            vals = c.get("values") if isinstance(c, dict) else c
            if isinstance(vals, dict): vals = [{"code": k} for k in vals]
            code = (c.get("elementCode") or c.get("code")) if isinstance(c, dict) else f[:-5]
            out["codes"][code] = [v["code"] if isinstance(v, dict) else str(v) for v in vals or []]
    raw = json.dumps(out, separators=(",", ":"), ensure_ascii=False).encode()
    (dst / f"{key}.json.gz").write_bytes(gzip.compress(raw, 9))
    print(key, len(out["messages"]), "Nachrichten", len(raw) // 1024, "KB ->", os.path.getsize(dst / f"{key}.json.gz") // 1024, "KB")
