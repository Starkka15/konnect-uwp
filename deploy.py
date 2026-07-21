#!/usr/bin/env python3
"""Deploy ZorinConnect appx to the Lumia 640 XL via Windows Device Portal.
Uninstall (same-version reinstall is blocked) -> install -> poll -> launch.
WMID pairing cookie expires -> re-pair via POST /api/authorize/pair?pin=<device-PIN> (see revenant-wdp-pairing).
"""
import sys, os, time, base64, requests, urllib3
urllib3.disable_warnings()

# Defaults = 640 XL (daily). Override for the 1520 or drifted IP via env:
#   ZC_HOST=https://192.168.4.245 ZC_WMID=<wmid> python3 deploy.py
HOST = os.environ.get("ZC_HOST", "https://192.168.5.17")
# WDP pairing cookie is device-specific + rotates — kept OUT of the repo. Source order:
#   $ZC_WMID  ->  a local (gitignored) ".wmid" file next to this script.
WMID = os.environ.get("ZC_WMID", "")
if not WMID:
    try:
        with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".wmid")) as _f:
            WMID = _f.read().strip()
    except OSError:
        pass
# Identity Name=Konnect.UWP, Publisher CN=Developer -> hash 1b7q5sa4bwdpa (unchanged: hash derives
# from Publisher only, and Publisher is still CN=Developer)
PFAM = "Konnect.UWP"  # PackageFamilyName as reported by WDP (version-independent)
PRAID = "Konnect.UWP_1b7q5sa4bwdpa!App"  # PackageRelativeId (includes publisher hash)
APPX = "/mnt/ssd-raid/vm-shared/zorinconnect/ZorinConnect_ARM.appx"

s = requests.Session()
s.verify = False
s.cookies.set("WMID", WMID)

def csrf():
    r = s.get(f"{HOST}/api/app/packagemanager/packages", timeout=15)
    # Device can set MULTIPLE CSRF-Token cookies (esp. after reboot/re-pair); take the last.
    toks = [c.value for c in s.cookies if c.name == "CSRF-Token"]
    return toks[-1] if toks else None

def hdrs(tok):
    return {"X-CSRF-Token": tok, "CSRF-Token": tok}

def installed_pkg():
    """(PackageFullName, PackageFamilyName) for our app, matched by family-name prefix so the real
    publisher hash reported by WDP is used even if our guess differs. (None, None) if not installed."""
    r = s.get(f"{HOST}/api/app/packagemanager/packages", timeout=15)
    for p in r.json().get("InstalledPackages", []):
        fam = p.get("PackageFamilyName", "")
        if fam == PFAM or fam.startswith(PFAM + "_"):
            return p.get("PackageFullName"), fam
    return None, None

def installed_pfn():
    return installed_pkg()[0]

def uninstall_family(prefix, tok):
    """Remove any installed package whose family starts with `prefix` (e.g. the old Zorin package)."""
    r = s.get(f"{HOST}/api/app/packagemanager/packages", timeout=15)
    for p in r.json().get("InstalledPackages", []):
        if p.get("PackageFamilyName", "").startswith(prefix):
            pfn = p.get("PackageFullName")
            print(f"Uninstalling old package {pfn}...")
            s.delete(f"{HOST}/api/app/packagemanager/package?package={pfn}", headers=hdrs(tok), timeout=60)
            time.sleep(3)

def main():
    launch_only = "--launch-only" in sys.argv
    tok = csrf()
    print(f"CSRF={tok}")

    if not launch_only:
        # One-time: remove the old Zorin-identity package now that identity is Konnect.UWP.
        uninstall_family("ZorinConnect.W10M_", tok); tok = csrf()

        # NO uninstall — install-over as a version update so the phone's ApplicationData
        # (deviceId/cert/pairing) survives. build.ps1 bumps the appx revision each build so WDP
        # accepts the update. Pass --clean to force a wipe+reinstall (fresh unpaired device).
        if "--clean" in sys.argv:
            pfn = installed_pfn()
            if pfn:
                print(f"Uninstalling (--clean) {pfn}...")
                s.delete(f"{HOST}/api/app/packagemanager/package?package={pfn}", headers=hdrs(tok), timeout=60)
                time.sleep(3)
                tok = csrf()

        print("Installing appx...")
        fname = APPX.split("/")[-1]
        with open(APPX, "rb") as f:
            files = {fname: (fname, f, "application/octet-stream")}
            r = s.post(f"{HOST}/api/app/packagemanager/package?package={fname}",
                       headers=hdrs(tok), files=files, timeout=300)
        print(f"  install HTTP={r.status_code} {r.text[:300]}")
        if r.status_code not in (200, 202):
            print("INSTALL FAILED"); sys.exit(1)

        for i in range(60):
            time.sleep(3)
            st = s.get(f"{HOST}/api/app/packagemanager/state", headers=hdrs(tok), timeout=15)
            if st.status_code == 200:
                print(f"  install complete: {st.text[:200]}")
                break
            if st.status_code == 204:
                continue
            print(f"  state HTTP={st.status_code} {st.text[:200]}")
            if "fail" in st.text.lower() or "error" in st.text.lower():
                print("INSTALL ERROR"); sys.exit(1)

    tok = csrf()
    pfn, fam = installed_pkg()
    if not pfn:
        print("could not resolve installed package full name"); sys.exit(1)
    praid = f"{fam}!App"   # derive from the real installed family name (correct hash guaranteed)
    appid_b64 = base64.b64encode(praid.encode()).decode()
    pfn_b64 = base64.b64encode(pfn.encode()).decode()
    print(f"Launching {pfn}...")
    r = s.post(f"{HOST}/api/taskmanager/app?appid={appid_b64}&package={pfn_b64}",
               headers=hdrs(tok), timeout=30)
    print(f"  launch HTTP={r.status_code} {r.text[:200]}")

if __name__ == "__main__":
    main()
