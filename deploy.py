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
WMID = os.environ.get("ZC_WMID", "3833765007940722240809168817185100068099660988657185202370593716")
# Identity Name=ZorinConnect.W10M, Publisher CN=Developer -> hash 1b7q5sa4bwdpa (computed, algo verified vs 8wekyb3d8bbwe)
VERSION = "0.1.0.0"
PFN  = f"ZorinConnect.W10M_{VERSION}_arm__1b7q5sa4bwdpa"
PRAID = "ZorinConnect.W10M_1b7q5sa4bwdpa!App"
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

def main():
    launch_only = "--launch-only" in sys.argv
    tok = csrf()
    print(f"CSRF={tok}")

    if not launch_only:
        print("Uninstalling...")
        r = s.delete(f"{HOST}/api/app/packagemanager/package?package={PFN}", headers=hdrs(tok), timeout=60)
        print(f"  uninstall HTTP={r.status_code} {r.text[:200]}")
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
    appid_b64 = base64.b64encode(PRAID.encode()).decode()
    pfn_b64 = base64.b64encode(PFN.encode()).decode()
    print("Launching...")
    r = s.post(f"{HOST}/api/taskmanager/app?appid={appid_b64}&package={pfn_b64}",
               headers=hdrs(tok), timeout=30)
    print(f"  launch HTTP={r.status_code} {r.text[:200]}")

if __name__ == "__main__":
    main()
