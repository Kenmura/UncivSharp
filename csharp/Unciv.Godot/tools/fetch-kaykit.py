#!/usr/bin/env python3
"""T389: stages the source bodies of the unit model pack so `build-unit-models.py` can be run from a clean checkout.

    python3 -I Unciv.Godot/tools/fetch-kaykit.py [<target folder, default Unciv.Godot/tools/kaykit-src (git-ignored)>]

The five bodies (Barbarian, Knight, Mage, Rogue, Rogue_Hooded) are the glTF binaries (plus the four palette textures the builders read) of "KayKit : Adventurers Character Pack 1.0" by Kay Lousberg (https://kaylousberg.com),
released under CC0 1.0 (public domain, see ASSETS.md), from the author's GitHub repository https://github.com/KayKit-Game-Assets/KayKit-Character-Pack-Adventures-1.0
at the pinned commit below (`addons/kaykit_character_pack_adventures/Characters/gltf/*.glb`). Every file is checked against the SHA-256 listed here, so the rebuild is byte-for-byte
reproducible; an existing file with the right hash is not downloaded again. Stdlib only. The folder is then the first argument of `build-unit-models.py` (which uses this default
folder when run without arguments). The sources are not committed to this repository: only the generated pack `Packs/units` is.
"""
import hashlib
import os
import sys
import urllib.request

COMMIT = "672074b73ba276876a19e8816ecdc5241817ab47"
BASE = f"https://raw.githubusercontent.com/KayKit-Game-Assets/KayKit-Character-Pack-Adventures-1.0/{COMMIT}/addons/kaykit_character_pack_adventures/Characters/gltf/"
FILES = {
    "Barbarian.glb": "cefc311a0e10c7858b6141f5ada7e33268727564fb8ac1347aab97d000669cc6",
    "Knight.glb": "60428e3abc09ba83e595d256e3af8c5c976b46cdae599f0802fc82b4a3445168",
    "Mage.glb": "cf898585da33fab50c724d31605fb931eb2912e6d2280092141e98ca81ad507d",
    "Rogue.glb": "e825437cd4d2ee9c1960b517a74a69101e33eb409ae7fa8cedc7134a998fbb7d",
    "Rogue_Hooded.glb": "93e6e25213009952276d9cf34f5d96a243767334c66f280db0433ddfabb91545",
    "barbarian_texture.png": "7329b2ff9709e8d54c886d5ef49c08bd42b12be3bbb2facb1488a6b48b1e8a80",
    "knight_texture.png": "5d250ccc5da020e6126bfa3839f83bd9a465a951ed223e4d13c08b1925e154d4",
    "mage_texture.png": "ea49f094b960402635fe51db9f1864960c97271b17f2e3554e5aca1b2bbba144",
    "rogue_texture.png": "a4032e877c3b91939f5cdbb630349c1998fdbc3211bbd587c111125500fe4cc5",
}
DEFAULT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "kaykit-src")


def sha256(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def fetch(target=DEFAULT):
    os.makedirs(target, exist_ok=True)
    for name, digest in FILES.items():
        path = os.path.join(target, name)
        if os.path.exists(path) and sha256(path) == digest:
            continue
        with urllib.request.urlopen(BASE + name, timeout=60) as response:
            data = response.read()
        if hashlib.sha256(data).hexdigest() != digest:
            sys.exit(f"{name}: SHA-256 mismatch against the pinned commit {COMMIT}")
        with open(path, "wb") as f:
            f.write(data)
        print(f"fetched {name}")
    return target


if __name__ == "__main__":
    print(fetch(sys.argv[1] if len(sys.argv) > 1 else DEFAULT))
