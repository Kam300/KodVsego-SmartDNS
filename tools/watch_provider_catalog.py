"""Observe official DNS setup pages; propose data for human review, never edit the delivery catalog."""
import argparse
import concurrent.futures
import gzip
import html
from html.parser import HTMLParser
import io
import ipaddress
import json
from pathlib import Path
import plistlib
import re
import urllib.request
from urllib.parse import urljoin, urlsplit
import xml.parsers.expat

LIMIT = 1024 * 1024
FIELD_LIMITS = {'ipv4': 64, 'ipv6': 32, 'doh': 32, 'explicitDot': 32}
STATUS_JSON_PATHS = ('/status.json', '/api/status')
MOBILECONFIG_REF_RE = re.compile(r'[\'"`]((?:https://[^\s\'"`<>]+)?/[A-Za-z0-9._/-]+\.mobileconfig)[\'"`]')
STATUS_JSON_REF_RE = re.compile(r'[\'"`]((?:https://[^\s\'"`<>]+)?(?:/status\.json|/api/status))[\'"`]')
SKIP_DOT_SUFFIXES = (
    '.local', '.internal', '.mobileconfig', '.json', '.xml', '.js', '.jsx', '.ts', '.tsx',
    '.mjs', '.cjs', '.css', '.html', '.htm', '.png', '.svg', '.jpg', '.jpeg', '.ico',
    '.txt', '.sh', '.ps1', '.zip', '.apk', '.exe', '.pdf',
)
WARNING_IP_RE = re.compile(
    r'(?i)(?:'
    r'FallbackDNS\s*=\s*[^\n`"\'<>]+'
    r'|(?:не\s+(?:ставьте|используйте|указывайте|добавляйте|оставляйте|прописывайте)'
    r'|рядом\s+стоит|удалите|уберите|instead\s+of|do\s+not\s+(?:use|set))'
    r'[^\d\n.!();]{0,30}?'
    r'(?:(?:1\.1\.1\.[123]|1\.0\.0\.1|8\.8\.[48]\.[48]|9\.9\.9\.9)(?:\s*(?:,|/|или|и|or|and)\s*)?)+'
    r')'
)
DOT_LABEL_RE = re.compile(
    r'(?i)(?:'
    r'DNS-over-TLS(?:\s*\(DoT\))?'
    r'|Имя\s+хоста(?:\s+провайдера)?\s+(?:частн(?:ый|ого|ому|ом)\s+DNS(?:-сервер[аеу]?)?|Private\s+DNS)(?:\s*\(DoT\))?'
    r'|Private\s+DNS(?:\s*/\s*DoT)?'
    r'|(?:частн(?:ый|ого|ому|ом)\s+DNS(?:-сервер[аеу]?)?)(?:\s*\(DoT\))?'
    r'|\(DoT\)'
    r'|DoT\s*[—:-]'
    r'|SNI'
    r')'
)
DOT_HOST_RE = re.compile(
    r'(?<![:/@\w.-])([a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+)(?::853)?(?![\w./:@-])'
)
DOT_SUFFIX_RE = re.compile(
    r'(?i)(?<![:/@\w.-])([a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+)(?::853)?(?![\w./:@-])'
    r'[ \t]+(?:(?:для|for|—|-|:)[ \t]+)?(?:DNS-over-TLS|Private[ \t]+DNS|\(DoT\)|DoT\b)(?![ \t]+(?:закрыт|отключ|не\s))'
)


class PageText(HTMLParser):
    """Collect visible text, copy attributes, script URLs, links, JSON-LD, and inline scripts without executing code."""

    def __init__(self):
        super().__init__()
        self.hidden = 0
        self.parts = []
        self.scripts = []
        self.links = []
        self.json_ld = []
        self.inline_scripts = []
        self._in_json_ld = False
        self._in_inline = False
        self._buf = []

    def handle_starttag(self, tag, attrs):
        attr_map = {k.lower(): v for k, v in attrs if k}
        if tag == 'a' and attr_map.get('href'):
            self.links.append(attr_map['href'])
        if tag == 'script':
            src = attr_map.get('src')
            script_type = (attr_map.get('type') or '').split(';', 1)[0].strip().lower()
            self._in_json_ld = False
            self._in_inline = False
            self._buf = []
            if src:
                self.scripts.append(src)
            elif script_type == 'application/ld+json':
                self._in_json_ld = True
            else:
                self._in_inline = True
        if tag in ('script', 'style'):
            self.hidden += 1
        if not self.hidden:
            for name, value in attrs:
                if name and name.lower() in ('data-copy', 'data-clipboard-text', 'data-clipboard', 'value') and value:
                    self.parts.append(value)

    def handle_endtag(self, tag):
        if tag == 'script':
            if self._in_json_ld:
                self.json_ld.append(''.join(self._buf))
            elif self._in_inline:
                self.inline_scripts.append(''.join(self._buf))
            self._in_json_ld = False
            self._in_inline = False
            self._buf = []
        if tag in ('script', 'style'):
            self.hidden = max(0, self.hidden - 1)

    def handle_data(self, data):
        if self._in_json_ld or self._in_inline:
            self._buf.append(data)
        elif not self.hidden:
            self.parts.append(data)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def _empty_found():
    return {field: [] for field in FIELD_LIMITS}


def _merge_found(target, extra):
    for field, limit in FIELD_LIMITS.items():
        if extra.get(field):
            target[field] = sorted(set(target[field] + extra[field]))[:limit]


def _finalize_found(raw):
    return {field: sorted(set(raw.get(field, [])))[:limit] for field, limit in FIELD_LIMITS.items()}


def _add_public_ip(found, value):
    if isinstance(value, str):
        candidate = value.strip()
        if public_ip(candidate):
            addr = ipaddress.ip_address(candidate)
            found['ipv4' if addr.version == 4 else 'ipv6'].append(str(addr))


def public_ip(value):
    try:
        return ipaddress.ip_address(value).is_global
    except ValueError:
        return False


def public_host(value):
    # Numeric addresses must pass the numeric check, never the hostname branch.
    if re.fullmatch(r'[\d.]+', value or '') or ':' in (value or ''):
        return public_ip(value)
    return bool(re.fullmatch(r'[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+', value or '')) and not value.lower().endswith(('.local', '.internal'))


def dot_host(value):
    """Validate a bare DoT hostname, excluding raw IPs and static asset filenames."""
    host = (value or '').strip().rstrip('.')
    tld = host.rsplit('.', 1)[-1] if '.' in host else ''
    return (
        public_host(host)
        and not public_ip(host)
        and bool(re.fullmatch(r'[a-zA-Z]{2,24}', tld))
        and not host.lower().endswith(SKIP_DOT_SUFFIXES)
    )


def https_url(value, query=False):
    try:
        if not value or any(ch.isspace() or ch in '`\\;,<>()[]{}"\'' for ch in value):
            return False
        u = urlsplit(value)
        return len(value) <= 512 and u.scheme == 'https' and public_host(u.hostname) and u.port in (None, 443) and not u.username and not u.password and not u.fragment and (query or not u.query) and bool(re.fullmatch(r'(?:/[A-Za-z0-9._~/%-]*)?', u.path or ''))
    except (ValueError, TypeError):
        return False


def same_origin(asset, base):
    """Return True only when asset is a safe HTTPS URL on the exact same origin as base."""
    if not https_url(asset):
        return False
    u, origin = urlsplit(asset), urlsplit(base)
    return (
        u.scheme == origin.scheme == 'https'
        and (u.hostname or '').lower() == (origin.hostname or '').lower()
        and (u.port or 443) == (origin.port or 443)
    )


def _json_strings(node, out):
    if isinstance(node, str):
        out.append(node)
    elif isinstance(node, list):
        for item in node:
            _json_strings(item, out)
    elif isinstance(node, dict):
        for key, value in node.items():
            if key not in ('@context', '@type', '@id', 'logo', 'image'):
                _json_strings(value, out)


def extract_structured_dot(text):
    """Extract DoT hostnames from labeled prose contexts without requiring tls://."""
    dots = set()
    for match in DOT_LABEL_RE.finditer(text):
        window = re.split(r'[.!?](?:\s|$)', text[match.end():match.end() + 90], 1)[0]
        if re.search(r'(?i)(?:не\s+(?:подходит|подходят|работает|вписать|поддержива)|закрыт|отключ|only\s+DoH|только\s+(?:по\s+)?DoH|no\s+DoT)', window):
            continue
        for host_match in DOT_HOST_RE.finditer(window):
            if dot_host(host_match.group(1)):
                dots.add(host_match.group(1).lower())
    for match in DOT_SUFFIX_RE.finditer(text):
        if dot_host(match.group(1)):
            dots.add(match.group(1).lower())
    return dots


def extract_static_js_data(script_text):
    """Extract static host constant bindings from inline scripts or bundles without executing JS."""
    found = _empty_found()
    vars_map = {}
    for match in re.finditer(r'\b([A-Za-z_$][\w$]{0,31})\s*=\s*([\'`"])([A-Za-z0-9.-]+)\2', script_text):
        if dot_host(match.group(3)):
            vars_map.setdefault(match.group(1), set()).add(match.group(3).lower())
    for pattern in (
        r'''['"`]https://['"`]\s*\+\s*([A-Za-z_$][\w$]{0,31})\s*\+\s*['"`]/dns-query['"`]''',
        r'`https://\$\{([A-Za-z_$][\w$]{0,31})\}/dns-query`',
    ):
        for match in re.finditer(pattern, script_text):
            for host in vars_map.get(match.group(1), ()):
                endpoint = f'https://{host}/dns-query'
                if https_url(endpoint):
                    found['doh'].append(endpoint)
    for match in re.finditer(r'(?i)(?:частн(?:ый|ого|ому|ом)\s+DNS(?:-сервер[аеу]?)?|Private\s+DNS|DNS-over-TLS|\(DoT\)|DoT\s*:)', script_text):
        window = script_text[match.end():match.end() + 400]
        if re.search(r'(?i)(?:только\s+(?:по\s+)?DoH|не\s+вписать|не\s+поддержива|закрыт)', window[:120]):
            continue
        for var_match in re.finditer(
            r'(?:(?:value|cv|children)\s*:\s*([A-Za-z_$][\w$]{0,31})\b|\[\[\s*([A-Za-z_$][\w$]{0,31})\s*\]\]|\b([A-Za-z_$][\w$]{0,31})\s*\+\s*[\'"`]:853[\'"`])',
            window,
        ):
            ident = var_match.group(1) or var_match.group(2) or var_match.group(3)
            for host in vars_map.get(ident, ()):
                found['explicitDot'].append(host)
    return _finalize_found(found)


def extract_mobileconfig(raw):
    """Parse plain or CMS-signed Apple .mobileconfig DNS payloads safely without XML entity expansion."""
    found = _empty_found()
    raw_bytes = raw.encode('utf-8', errors='replace') if isinstance(raw, str) else raw
    if len(raw_bytes) > LIMIT:
        raise ValueError('Mobileconfig too large')
    if re.search(rb'<!ENTITY\b', raw_bytes, re.I) or re.search(rb'<!DOCTYPE\b[^>]*\[', raw_bytes, re.I):
        raise ValueError('Unsafe XML entity in mobileconfig')
    plist_match = re.search(rb'<plist\b[^>]*>.*?</plist>', raw_bytes, re.S | re.I)
    if not plist_match:
        return found
    try:
        doc = plistlib.loads(b'<?xml version="1.0" encoding="UTF-8"?>\n' + plist_match.group(0))
    except (plistlib.InvalidFileException, xml.parsers.expat.ExpatError, ValueError):
        return found
    if not isinstance(doc, dict):
        return found
    items = list(doc.get('PayloadContent') or []) if isinstance(doc.get('PayloadContent'), list) else []
    items.append(doc)
    for item in items:
        if not isinstance(item, dict) or not isinstance(item.get('DNSSettings'), dict):
            continue
        dns = item['DNSSettings']
        if isinstance(dns.get('ServerAddresses'), list):
            for addr in dns['ServerAddresses']:
                _add_public_ip(found, addr)
        proto = dns.get('DNSProtocol').strip().upper() if isinstance(dns.get('DNSProtocol'), str) else ''
        if proto == 'HTTPS':
            server_url = dns.get('ServerURL')
            if isinstance(server_url, str):
                candidate = server_url.strip()
                if https_url(candidate) and len(urlsplit(candidate).path) > 1:
                    found['doh'].append(candidate)
        elif proto == 'TLS':
            server_name = dns.get('ServerName')
            if isinstance(server_name, str) and dot_host(server_name):
                found['explicitDot'].append(server_name.strip().rstrip('.').lower())
    return _finalize_found(found)


def extract_status_json(raw):
    """Extract structured public endpoints from known same-origin status JSON payloads."""
    found = _empty_found()
    try:
        data = json.loads(raw)
    except (ValueError, TypeError):
        return found
    if not isinstance(data, dict):
        return found
    srv = data.get('server')
    if isinstance(srv, dict):
        for key in ('doh_public', 'doh_name', 'name'):
            host = srv.get(key)
            if isinstance(host, str) and dot_host(host):
                endpoint = f'https://{host.strip().rstrip(".").lower()}/dns-query'
                if https_url(endpoint):
                    found['doh'].append(endpoint)
    dns = data.get('dns')
    if isinstance(dns, dict):
        for key in ('ip', 'ip2'):
            _add_public_ip(found, dns.get(key))
        host = dns.get('host')
        if isinstance(host, str) and dot_host(host):
            clean_host = host.strip().rstrip('.').lower()
            protos = dns.get('protocols')
            if isinstance(protos, list):
                proto_ids = {(p.get('id') or '').lower() for p in protos if isinstance(p, dict) and isinstance(p.get('id'), str)}
                if 'dot' in proto_ids or 'tls' in proto_ids:
                    found['explicitDot'].append(clean_host)
                if 'doh' in proto_ids or 'https' in proto_ids:
                    endpoint = f'https://{clean_host}/dns-query'
                    if https_url(endpoint):
                        found['doh'].append(endpoint)
    return _finalize_found(found)


def _extract_page(page):
    found = extract_text(html.unescape('\n'.join(page.parts)))
    for block in page.json_ld:
        try:
            strings = []
            _json_strings(json.loads(block), strings)
            if strings:
                _merge_found(found, extract_text('\n'.join(strings)))
        except (ValueError, TypeError):
            pass
    for script in page.inline_scripts:
        _merge_found(found, extract_static_js_data(script))
    return found


def extract(source):
    parser = PageText()
    parser.feed(source)
    return _extract_page(parser)


def extract_text(text, allow_structured_dot=True):
    # These are observations, not an IP list to apply: setup pages may describe several profiles.
    cleaned = WARNING_IP_RE.sub(' ', text)
    ips = sorted({value for value in re.findall(r'(?<![\w.:])(?:\d{1,3}\.){3}\d{1,3}(?!(?:\.[\w.]|[\w:]))', cleaned) if public_ip(value)})[:64]
    v6 = sorted({value for value in re.findall(r'(?<![\w:])(?:[0-9a-fA-F]{0,4}:){2,7}[0-9a-fA-F]{0,4}(?![\w:])', cleaned) if public_ip(value)})[:32]
    endpoints = sorted({url.rstrip('.,;)\'"`') for url in re.findall(r'https://[^\s<>"\'`\\;,()]+/dns-query(?:[^\s<>"\'`\\;,()]*)?', cleaned) if https_url(url.rstrip('.,;)\'"`'))})[:32]
    # MagicDNS publishes a nonstandard DoH path. Only recognize it on its official domain.
    if re.search(r'''https://api[.]magicdns[.]ru/sync(?=$|[\s<>"'.,;)])''', cleaned):
        endpoints = sorted(set(endpoints + ['https://api.magicdns.ru/sync']))[:32]
    if re.search(r'''https://api[.]magicinfo[.]art/rpc(?=$|[\s<>"'.,;)])''', cleaned):
        endpoints = sorted(set(endpoints + ['https://api.magicinfo.art/rpc']))[:32]
    dots = {host.rstrip('.,;)').lower() for host in re.findall(r'tls://([A-Za-z0-9.-]+)', cleaned) if dot_host(host.rstrip('.,;)'))}
    if allow_structured_dot:
        dots.update(extract_structured_dot(cleaned))
    return dict(ipv4=ips, ipv6=v6, doh=endpoints, explicitDot=sorted(dots)[:32])


def fetch(url):
    if not https_url(url, query=True):
        raise ValueError('Untrusted source URL')
    request = urllib.request.Request(
        url,
        headers={
            'User-Agent': 'KodVsego-SmartDNS-Catalog-Observer/1.0',
            'Accept': 'text/html,application/json,*/*;q=0.8',
            'Accept-Encoding': 'identity',
        },
    )
    with urllib.request.build_opener(NoRedirect).open(request, timeout=15) as response:
        if response.status != 200:
            raise ValueError('Unexpected HTTP status')
        body = response.read(LIMIT + 1)
        if len(body) > LIMIT:
            raise ValueError('Source too large')
        if body.startswith(b'\x1f\x8b'):
            with gzip.GzipFile(fileobj=io.BytesIO(body)) as stream:
                body = stream.read(LIMIT + 1)
            if len(body) > LIMIT:
                raise ValueError('Expanded source too large')
        headers = getattr(response, 'headers', None)
        charset = headers.get_content_charset() if headers else None
        try:
            return body.decode(charset or 'utf-8', errors='replace')
        except LookupError:
            return body.decode('utf-8', errors='replace')


def _safe_split(base_url, ref):
    try:
        asset = urljoin(base_url, ref)
        return asset, urlsplit(asset).path
    except ValueError:
        return None, ''


def _discover_assets(base_url, refs, path_pred, cap):
    assets = []
    for ref in refs:
        asset, path = _safe_split(base_url, ref)
        if asset and asset not in assets and same_origin(asset, base_url) and path_pred(path):
            assets.append(asset)
            if len(assets) >= cap:
                break
    return assets


def observe(providers, previous, loader=fetch):
    sources = sorted({p['url'] for p in providers if p['enabled'] and p.get('url')})
    results, errors = {}, {}

    def one(url):
        try:
            source = loader(url)
            page = PageText()
            page.feed(source)
            found = _extract_page(page)
            mc_refs = [href for href in page.links if _safe_split(url, href)[1].lower().endswith('.mobileconfig')]
            status_refs = [href for href in page.links if _safe_split(url, href)[1] in STATUS_JSON_PATHS]
            for script in page.inline_scripts:
                mc_refs.extend(MOBILECONFIG_REF_RE.findall(script))
                status_refs.extend(STATUS_JSON_REF_RE.findall(script))

            seen_mc, seen_status = set(), set()

            def fetch_assets(refs, seen, path_pred, cap, extractor):
                for asset in _discover_assets(url, refs, path_pred, cap):
                    if asset in seen or len(seen) >= cap:
                        continue
                    seen.add(asset)
                    try:
                        _merge_found(found, extractor(loader(asset)))
                    except Exception:
                        pass

            def scan_bundles(path_prefix):
                for asset in _discover_assets(url, page.scripts, lambda p: p.startswith(path_prefix) and p.endswith('.js'), 2):
                    try:
                        bundle = loader(asset)
                        _merge_found(found, extract_text(bundle, allow_structured_dot=False))
                        _merge_found(found, extract_static_js_data(bundle))
                        fetch_assets(MOBILECONFIG_REF_RE.findall(bundle), seen_mc, lambda p: p.lower().endswith('.mobileconfig'), 4, extract_mobileconfig)
                        fetch_assets(STATUS_JSON_REF_RE.findall(bundle), seen_status, lambda p: p in STATUS_JSON_PATHS, 2, extract_status_json)
                    except Exception:
                        pass

            fetch_assets(mc_refs, seen_mc, lambda p: p.lower().endswith('.mobileconfig'), 4, extract_mobileconfig)
            fetch_assets(status_refs, seen_status, lambda p: p in STATUS_JSON_PATHS, 2, extract_status_json)
            scan_bundles('/assets/')
            # Only fall back to Next.js chunks when SSR/profiles yielded nothing, avoiding unlinked Pro/worker chunk data.
            if not any(found.values()):
                scan_bundles('/_next/static/')
            if not any(found.values()):
                raise ValueError('No published addresses found; possible layout change')
            return url, found, None
        except Exception as error:
            return url, None, type(error).__name__

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        for url, found, error in pool.map(one, sources):
            if error:
                errors[url] = error
            else:
                results[url] = found
    output = dict(previous.get('observations', {}))
    changes = []
    for p in providers:
        if not p['enabled'] or p.get('url') not in results:
            continue  # A timeout/404 never removes or disables an existing provider.
        entry = dict(source=p['url'], **results[p['url']])
        if output.get(p['key']) != entry:
            changes.append(dict(key=p['key'], name=p['name'], current={field: p.get(field) for field in ('ipv4', 'ipv6', 'doh', 'dot')}, observed=entry))
            output[p['key']] = entry
    return dict(schema=1, observations=output), changes, errors


def proposal(changes):
    lines = ['# Наблюдения за страницами DNS-провайдеров', '',
             'Автоматически обнаруженные адреса требуют проверки человеком. Это **не** готовый список DNS для приложения.',
             'Одна страница может описывать разные профили, bootstrap, IPv6 и примеры. Не переносите все найденные IP в один профиль.',
             'Отказы сайтов не выключают провайдеров и не меняют последний каталог.', '']
    for item in changes:
        lines += ['## ' + item['key'], '', 'Источник: ' + item['observed']['source'], '', 'Текущие настройки:', '```json',
                  json.dumps(item['current'], ensure_ascii=False, indent=2), '```', '', 'Опубликованные на странице адреса (кандидаты для проверки):', '```json',
                  json.dumps(item['observed'], ensure_ascii=False, indent=2), '```', '']
    lines += ['После проверки внесите точные изменения в `config/providers.json` или `config/provider-checkers.json`.',
              'Старые IP сохраните в `legacyIpv4`, ID/key не меняйте. Выполните `tools/Sync-ProviderAvailability.ps1`, тесты и просмотрите `config/catalog.json`.',
              'Только утверждённый и опубликованный `config/catalog.json` загружается приложениями. Этот отчёт и observations.json клиенты не читают.', '']
    return '\n'.join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    providers = json.loads((args.root / 'config/providers.json').read_text(encoding='utf-8-sig'))
    path = args.root / 'config/provider-observations.json'
    previous = json.loads(path.read_text(encoding='utf-8')) if path.exists() else {}
    output, changes, errors = observe(providers, previous)
    for url, error in errors.items():
        print('Unavailable source (previous data kept):', url, error)
    if changes:
        path.write_text(json.dumps(output, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        (args.root / 'docs/PROVIDER-CATALOG-PROPOSAL.md').write_text(proposal(changes), encoding='utf-8')
    print('Providers with changed observations:', len(changes))


if __name__ == '__main__':
    main()
