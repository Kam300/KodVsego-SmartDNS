"""Observe official DNS setup pages; propose data for human review, never edit the delivery catalog."""
import argparse
import concurrent.futures
import html
import gzip
import io
from html.parser import HTMLParser
import ipaddress
import json
from pathlib import Path
import re
import urllib.request
from urllib.parse import urlsplit, urljoin

LIMIT = 1024 * 1024

class PageText(HTMLParser):
    def __init__(self):
        super().__init__()
        self.hidden = 0
        self.parts = []
        self.scripts = []
    def handle_starttag(self, tag, attrs):
        if tag == 'script':
            self.scripts.extend(v for k,v in attrs if k == 'src' and v)
        if tag in ('script', 'style'):
            self.hidden += 1
        if not self.hidden:
            for name, value in attrs:
                if name in ('data-copy', 'data-clipboard-text', 'value') and value:
                    self.parts.append(value)
    def handle_endtag(self, tag):
        if tag in ('script', 'style'):
            self.hidden = max(0, self.hidden - 1)
    def handle_data(self, data):
        if not self.hidden:
            self.parts.append(data)

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None

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

def https_url(value, query=False):
    try:
        u = urlsplit(value)
        return len(value) <= 512 and u.scheme == 'https' and public_host(u.hostname) and u.port in (None, 443) and not u.username and not u.password and not u.fragment and (query or not u.query)
    except (ValueError, TypeError):
        return False

def extract(source):
    parser = PageText()
    parser.feed(source)
    text = html.unescape('\n'.join(parser.parts))
    return extract_text(text)

def extract_text(text):
    # These are observations, not an IP list to apply: setup pages may describe several profiles.
    ips = sorted({value for value in re.findall(r'(?<![\w.:])(?:\d{1,3}\.){3}\d{1,3}(?![\w.:])', text) if public_ip(value)})[:64]
    v6 = sorted({value for value in re.findall(r'(?<![\w:])(?:[0-9a-fA-F]{0,4}:){2,7}[0-9a-fA-F]{0,4}(?![\w:])', text) if public_ip(value)})[:32]
    endpoints = sorted({url.rstrip('.,;)') for url in re.findall(r'https://[^\s<>"\']+/dns-query(?:[^\s<>"\']*)?', text) if https_url(url.rstrip('.,;)'))})[:32]
    dot = sorted({host.rstrip('.,;)') for host in re.findall(r'tls://([A-Za-z0-9.-]+)', text) if public_host(host.rstrip('.,;)'))})[:32]
    return dict(ipv4=ips, ipv6=v6, doh=endpoints, explicitDot=dot)

def fetch(url):
    if not https_url(url, query=True):
        raise ValueError('Untrusted source URL')
    request = urllib.request.Request(url, headers={'User-Agent': 'KodVsego-SmartDNS-Catalog-Observer/1.0', 'Accept': 'text/html', 'Accept-Encoding': 'identity'})
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
        return body.decode('utf-8', errors='replace')

def observe(providers, previous, loader=fetch):
    sources = sorted({p['url'] for p in providers if p['enabled'] and p.get('url')})
    results, errors = {}, {}
    def one(url):
        try:
            source = loader(url)
            found = extract(source)
            page = PageText(); page.feed(source)
            # Read at most two same-origin static app bundles; do not execute JavaScript.
            for ref in page.scripts[:2]:
                asset = urljoin(url, ref)
                u, origin = urlsplit(asset), urlsplit(url)
                if u.netloc == origin.netloc and u.path.startswith('/assets/') and u.path.endswith('.js') and https_url(asset):
                    try:
                        extra = extract_text(loader(asset))
                        for field in found:
                            found[field] = sorted(set(found[field] + extra[field]))[:64]
                    except Exception:
                        pass
            # Reviewed static WLrus declaration; no expression evaluation or script execution.
            marker = re.search(r"const DNS_HOST='([a-zA-Z0-9.-]+)'", source)
            if marker and "const DOH='https://'+DNS_HOST+'/dns-query'" in source and public_host(marker.group(1)):
                found['doh'] = sorted(set(found['doh'] + ['https://' + marker.group(1) + '/dns-query']))
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
            changes.append(dict(key=p['key'], name=p['name'], current={field:p.get(field) for field in ('ipv4','ipv6','doh','dot')}, observed=entry))
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
