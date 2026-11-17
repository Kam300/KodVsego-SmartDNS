"""Shared Smart DNS votes. Run behind the local HTTPS reverse proxy."""

import argparse
import hashlib
import ipaddress
import json
import re
import sqlite3
import time
from contextlib import closing
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import parse_qs, urlsplit


WINDOW = 7 * 24 * 60 * 60
MAX_BODY = 2048
TOKEN = re.compile(r"[a-f0-9]{64}")
ROUTE = "/smartdns-ratings/v1"


def endpoint_key(protocol, endpoint):
    if protocol == "plain":
        return str(ipaddress.ip_address(endpoint))
    if protocol == "dot":
        return endpoint.lower().rstrip(".")
    return endpoint


def catalog_targets(catalog):
    targets = set()
    for provider in catalog["providers"]:
        services = {"dns", *provider["services"], *provider["autoGoals"]}
        addresses = [] if provider["encryptedOnly"] else provider["ipv4"] + provider["ipv6"]
        endpoints = [("plain", address) for address in addresses]
        endpoints += [(key, provider.get(key)) for key in ("doh", "dot")]
        targets.update((protocol, endpoint_key(protocol, endpoint), service)
                       for protocol, endpoint in endpoints if endpoint
                       for service in services)
    return targets


class Ratings:
    def __init__(self, database, targets):
        self.database = database
        self.targets = targets
        with closing(sqlite3.connect(database)) as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS vote (
                    protocol TEXT NOT NULL, endpoint TEXT NOT NULL, service TEXT NOT NULL,
                    voter TEXT NOT NULL, value INTEGER NOT NULL CHECK (value IN (-1, 1)),
                    changed INTEGER NOT NULL,
                    PRIMARY KEY (protocol, endpoint, service, voter)
                ) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS vote_expiration ON vote (changed);
            """)

    def target(self, protocol, endpoint, service):
        if not all(isinstance(value, str) for value in (protocol, endpoint, service)):
            raise ValueError("invalid_target")
        target = protocol, endpoint_key(protocol, endpoint), service
        if target not in self.targets:
            raise ValueError("unknown_target")
        return target

    def totals(self, target, token=None, value=None):
        now = int(time.time())
        with closing(sqlite3.connect(self.database)) as db, db:
            if token is not None:
                voter = hashlib.sha256(token.encode("ascii")).hexdigest()
                db.execute("DELETE FROM vote WHERE changed <= ?", (now - WINDOW,))
                if value == 0:
                    db.execute("DELETE FROM vote WHERE protocol=? AND endpoint=? AND service=? AND voter=?",
                               (*target, voter))
                else:
                    db.execute("""INSERT INTO vote VALUES (?, ?, ?, ?, ?, ?)
                        ON CONFLICT(protocol, endpoint, service, voter)
                        DO UPDATE SET value=excluded.value, changed=excluded.changed""",
                               (*target, voter, value, now))
            works, fails = db.execute("""SELECT COUNT(CASE WHEN value=1 THEN 1 END),
                    COUNT(CASE WHEN value=-1 THEN 1 END) FROM vote
                    WHERE protocol=? AND endpoint=? AND service=? AND changed > ?""",
                                      (*target, now - WINDOW)).fetchone()
        return {"works": works, "fails": fails, "windowDays": 7}


class RequestHandler(BaseHTTPRequestHandler):
    def setup(self):
        self.request.settimeout(3)
        super().setup()

    def log_message(self, *args):
        pass

    def respond(self, status, body):
        payload = json.dumps(body, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        path = urlsplit(self.path)
        if path.path == ROUTE + "/health":
            self.respond(200, {"status": "ok"})
            return
        if path.path != ROUTE + "/votes":
            self.respond(404, {"error": "not_found"})
            return
        try:
            query = parse_qs(path.query, max_num_fields=3)
            if any(len(values) != 1 for values in query.values()):
                raise ValueError("invalid_target")
            target = self.server.ratings.target(*(query[key][0] for key in ("protocol", "endpoint", "service")))
        except (KeyError, ValueError):
            self.respond(400, {"error": "invalid_target"})
            return
        self.respond(200, self.server.ratings.totals(target))

    def do_POST(self):
        if self.path != ROUTE + "/votes":
            self.respond(404, {"error": "not_found"})
            return
        try:
            size = int(self.headers.get("Content-Length", "0"))
            if not 0 < size <= MAX_BODY or self.headers.get_content_type() != "application/json":
                raise ValueError("invalid_body")
            data = json.loads(self.rfile.read(size))
            if not isinstance(data, dict):
                raise ValueError("invalid_body")
            token, value = data["token"], data["value"]
            if not isinstance(token, str) or not TOKEN.fullmatch(token) or type(value) is not int or value not in (-1, 0, 1):
                raise ValueError("invalid_vote")
            target = self.server.ratings.target(data["protocol"], data["endpoint"], data["service"])
        except (KeyError, ValueError, RecursionError):
            self.respond(400, {"error": "invalid_vote"})
            return
        # Caddy overwrites this header. It is used only for the in-memory quota.
        peer = self.headers.get("X-SmartDNS-Client", self.client_address[0])
        try:
            address = ipaddress.ip_address(peer)
        except ValueError:
            self.respond(400, {"error": "invalid_client"})
            return
        if address.version == 6:
            address = ipaddress.ip_network((address, 64), strict=False)
        if not self.server.allow_vote(address):
            self.respond(429, {"error": "rate_limit"})
            return
        self.respond(200, self.server.ratings.totals(target, token, value))


class RatingServer(HTTPServer):
    def __init__(self, port, ratings):
        super().__init__(("127.0.0.1", port), RequestHandler)
        self.ratings = ratings
        self.minute = 0
        self.quota = {}

    def allow_vote(self, address):
        minute = int(time.monotonic() // 60)
        if minute != self.minute:
            self.minute = minute
            self.quota.clear()
        if address not in self.quota and len(self.quota) >= 4096:
            return False
        count = self.quota.get(address, 0)
        if count >= 20:
            return False
        self.quota[address] = count + 1
        return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True)
    parser.add_argument("--database", required=True)
    parser.add_argument("--port", type=int, default=18765)
    args = parser.parse_args()
    with open(args.catalog, encoding="utf-8-sig") as catalog:
        ratings = Ratings(args.database, catalog_targets(json.load(catalog)))
    with RatingServer(args.port, ratings) as server:
        server.serve_forever()


if __name__ == "__main__":
    main()
