#!/usr/bin/env python3
"""Подпись релиза Мака для автообновления / sign a Mac release for the auto-updater.

Пишет <файл>.sig рядом: base64 подписи RSA-SHA256 PKCS#1 v1.5 — тот же формат и тот же ключ,
что windows/sign-release.ps1. Секретный ключ — заметка записи Bitwarden
«Sync Watchdog — ключ подписи релизов» (RSA XML). Ключ живёт только во временном файле (600)
на время одной команды openssl и сразу удаляется.

Использование / usage:
  python3 sign-release-mac.py dist/SyncWatchdog-mac-1.7.zip            # ключ из Bitwarden (bw разблокирован)
  python3 sign-release-mac.py dist/SyncWatchdog-mac-1.7.zip --key key.xml
"""
import base64, json, os, subprocess, sys, tempfile

ITEM = "Sync Watchdog — ключ подписи релизов"
BW = "/opt/homebrew/bin/bw"


def bw_session_env():
    env = dict(os.environ)
    if "BW_SESSION" not in env:
        p = os.path.expanduser("~/.bitwarden-cli-mcp/session.json")
        if os.path.exists(p):
            s = json.load(open(p))
            for k in ("token", "session", "BW_SESSION"):
                if isinstance(s.get(k), str):
                    env["BW_SESSION"] = s[k]; break
    return env


def key_xml(args):
    if "--key" in args:
        return open(args[args.index("--key") + 1], encoding="utf-8").read()
    out = subprocess.run([BW, "list", "items", "--search", "Sync Watchdog", "--nointeraction"], env=bw_session_env(),
                         capture_output=True, text=True).stdout
    for it in json.loads(out or "[]"):
        n = it.get("notes") or ""
        if n.startswith("<RSAKeyValue>") and "<D>" in n:
            return n
    sys.exit("Ключ в Bitwarden не найден (bw разблокирован? был sync?) / signing key not found")


def tag(xml, name):
    a = xml.index(f"<{name}>") + len(name) + 2
    return int.from_bytes(base64.b64decode(xml[a:xml.index(f"</{name}>")]), "big")


def der_len(n):
    if n < 0x80:
        return bytes([n])
    b = n.to_bytes((n.bit_length() + 7) // 8, "big")
    return bytes([0x80 | len(b)]) + b


def der_int(v):
    b = v.to_bytes(max(1, (v.bit_length() + 7) // 8), "big")
    if b[0] & 0x80:
        b = b"\x00" + b
    return b"\x02" + der_len(len(b)) + b


def der_seq(*items):
    body = b"".join(items)
    return b"\x30" + der_len(len(body)) + body


def pem(der, label):
    b = base64.b64encode(der).decode()
    return f"-----BEGIN {label}-----\n" + "\n".join(b[i:i + 64] for i in range(0, len(b), 64)) + f"\n-----END {label}-----\n"


def main():
    args = sys.argv[1:]
    if not args or args[0].startswith("-"):
        sys.exit(__doc__)
    target = args[0]
    xml = key_xml(args)
    n, e, d, p, q = (tag(xml, x) for x in ("Modulus", "Exponent", "D", "P", "Q"))
    dp, dq, qi = tag(xml, "DP"), tag(xml, "DQ"), tag(xml, "InverseQ")
    priv = pem(der_seq(der_int(0), der_int(n), der_int(e), der_int(d), der_int(p), der_int(q),
                       der_int(dp), der_int(dq), der_int(qi)), "RSA PRIVATE KEY")
    pub = pem(der_seq(der_int(n), der_int(e)), "RSA PUBLIC KEY")
    del xml
    with tempfile.TemporaryDirectory() as td:
        kp = os.path.join(td, "k.pem"); sp = os.path.join(td, "s.bin"); pp = os.path.join(td, "p.pem")
        fd = os.open(kp, os.O_WRONLY | os.O_CREAT, 0o600)
        os.write(fd, priv.encode()); os.close(fd)
        try:
            subprocess.run(["openssl", "dgst", "-sha256", "-sign", kp, "-out", sp, target], check=True)
        finally:
            os.remove(kp)
        open(pp, "w").write(pub)
        # самопроверка открытым ключом / self-check with the public key
        ok = subprocess.run(["openssl", "dgst", "-sha256", "-verify", pp, "-keyform", "PEM", "-signature", sp, target],
                            capture_output=True, text=True)
        if ok.returncode != 0:
            # LibreSSL не понимает «RSA PUBLIC KEY» в -verify — проверяем через pkeyutl
            ok = subprocess.run(["openssl", "rsautl", "-verify", "-pubin", "-inkey", pp, "-in", sp], capture_output=True)
        sig = base64.b64encode(open(sp, "rb").read()).decode()
    open(target + ".sig", "w").write(sig + "\n")
    print(f"Подписано / signed: {target}.sig  (самопроверка: {'ok' if ok.returncode == 0 else 'НЕ ПРОШЛА'})")
    if ok.returncode != 0:
        sys.exit(1)


if __name__ == "__main__":
    main()
