#!/usr/bin/env python3
"""Re-scrape tier lists from Mobalytics.

Usage:
    python3 scripts/scrape_tiers.py [--moba-relics FILE] [--moba-cards FILE]

Writes updated JSON files to data/tier_lists/. Requires `curl` on PATH.

Mobalytics sits behind a Cloudflare challenge that blocks curl. If the fetch
fails, open the tier list page in a normal browser, save its HTML (or just the
`window.__PRELOADED_STATE__=...` script text) to a file and pass it via
--moba-relics / --moba-cards.
"""

from __future__ import annotations

import argparse
import datetime as dt
import html as html_lib
import json
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA_DIR = ROOT / "data" / "tier_lists"

UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) "
    "Chrome/131.0.0.0 Safari/537.36"
)
TODAY = dt.date.today().isoformat()


def fetch(url: str) -> str:
    """Fetch a URL via curl with a normal browser UA."""
    out = subprocess.run(
        ["curl", "-sL", "-A", UA, url],
        check=True,
        capture_output=True,
    )
    return out.stdout.decode("utf-8", errors="replace")


TIER_LETTERS = "SABCDF"


# --- Mobalytics --------------------------------------------------------------

PRELOAD_RE = re.compile(r"window\.__PRELOADED_STATE__\s*=\s*(\{.*?\});", re.DOTALL)


def _extract_preload(html: str) -> dict:
    m = PRELOAD_RE.search(html)
    if not m:
        hint = " (Cloudflare challenge page)" if "Just a moment..." in html else ""
        raise RuntimeError(
            f"__PRELOADED_STATE__ not found in Mobalytics HTML{hint}. "
            "Save the page from a browser and pass it via --moba-relics / --moba-cards."
        )
    return json.loads(m.group(1))


def _moba_html(url: str, saved: Path | None) -> str:
    if not saved:
        return fetch(url)
    text = saved.read_text(encoding="utf-8")
    if "line-gutter-backdrop" in text:
        # Chrome "view-source:" page saved with Ctrl+S: source is wrapped in
        # per-line table markup and HTML-escaped. Unwrap it back to raw source.
        text = re.sub(r"</td></tr>", "\n", text)
        text = html_lib.unescape(re.sub(r"<[^>]+>", "", text))
    return text


def _doc_data(state: dict) -> dict:
    body = state["sts2State"]["apollo"]["graphqlV2"]["queries"][1]["state"]["data"]
    return body[0]["game"]["documents"]["userGeneratedDocumentBySlug"]["data"]["data"]


def scrape_mobalytics_relics(saved: Path | None = None) -> dict:
    html = _moba_html("https://mobalytics.gg/slay-the-spire-2/tier-lists/relics", saved)
    doc = _doc_data(_extract_preload(html))
    section = doc["tierLists"]["values"][0]
    tiers = {
        s["name"]: [item["name"] for item in (s.get("staticDataItems") or [])]
        for s in section["tierSections"]
    }
    return {"tiers": tiers}


def scrape_mobalytics_cards(prev_path: Path, saved: Path | None = None) -> dict:
    """Scrape cards. Map each tier list to a character by overlap with the
    existing JSON's per-character card sets — Mobalytics' API doesn't label
    them by name."""
    html = _moba_html("https://mobalytics.gg/slay-the-spire-2/tier-lists/cards", saved)
    doc = _doc_data(_extract_preload(html))

    prev = json.loads(prev_path.read_text(encoding="utf-8"))
    sigs = {char: {c for tier in tiers.values() for c in tier} for char, tiers in prev["characters"].items()}

    characters: dict[str, dict[str, list[str]]] = {}
    for tl in doc["tierLists"]["values"]:
        tier_data: dict[str, list[str]] = {}
        all_cards: list[str] = []
        for sec in tl["tierSections"]:
            cards = [it["name"] for it in (sec.get("staticDataItems") or [])]
            tier_data[sec["name"]] = cards
            all_cards.extend(cards)
        scores = {char: sum(1 for c in all_cards if c in sig) for char, sig in sigs.items()}
        best = max(scores, key=scores.get)
        if scores[best] == 0:
            # Mobalytics added a separate list for multiplayer cards, which the
            # game keeps in ColorlessCardPool (Pool.Title == "colorless").
            if "Colorless" in characters:
                raise RuntimeError(f"could not identify character for tier list {tl['id']}")
            print(f"  tier list {tl['id']} matches no character -> Colorless ({len(all_cards)} cards)")
            best = "Colorless"
        characters[best] = tier_data

    return {
        "source": "https://mobalytics.gg/slay-the-spire-2/tier-lists/cards",
        "snapshot_date": TODAY,
        "note": "Mobalytics STS2 card tier list (run scripts/scrape_tiers.py to refresh).",
        "characters": characters,
    }


# --- writers ----------------------------------------------------------------


def _write_inline_chars(path: Path, payload: dict, char_order: list[str]) -> None:
    """Write Mobalytics-style JSON: header keys + characters with inline tier arrays."""
    lines = ["{"]
    lines.append(f'  "source": {json.dumps(payload["source"], ensure_ascii=False)},')
    lines.append(f'  "snapshot_date": {json.dumps(payload["snapshot_date"], ensure_ascii=False)},')
    lines.append(f'  "note": {json.dumps(payload["note"], ensure_ascii=False)},')
    lines.append('  "characters": {')
    char_blocks = []
    for c in char_order:
        if c not in payload["characters"]:
            continue
        tier_lines = []
        for t in TIER_LETTERS:
            if t in payload["characters"][c]:
                arr = json.dumps(payload["characters"][c][t], ensure_ascii=False)
                tier_lines.append(f'      "{t}": {arr}')
        block = f'    "{c}": {{\n' + ",\n".join(tier_lines) + "\n    }"
        char_blocks.append(block)
    lines.append(",\n".join(char_blocks))
    lines.append("  }")
    lines.append("}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def _write_pretty(path: Path, payload: dict) -> None:
    path.write_text(
        json.dumps(payload, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--moba-relics", type=Path, help="saved Mobalytics relic tier list page")
    ap.add_argument("--moba-cards", type=Path, help="saved Mobalytics card tier list page")
    args = ap.parse_args()

    DATA_DIR.mkdir(parents=True, exist_ok=True)

    print("[1/2] Mobalytics relics")
    relics = scrape_mobalytics_relics(args.moba_relics)
    _write_pretty(DATA_DIR / "mobalytics_relics.json", relics)

    print("[2/2] Mobalytics cards")
    moba = scrape_mobalytics_cards(DATA_DIR / "mobalytics_cards.json", args.moba_cards)
    _write_inline_chars(
        DATA_DIR / "mobalytics_cards.json",
        moba,
        char_order=["Ironclad", "Silent", "Regent", "Necrobinder", "Defect", "Colorless"],
    )

    print("Done. Re-build the mod to embed the new resources.")


if __name__ == "__main__":
    main()
