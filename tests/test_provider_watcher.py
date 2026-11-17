import email.message
import gzip
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch
import urllib.error

spec = importlib.util.spec_from_file_location('watcher', Path(__file__).resolve().parents[1] / 'tools/watch_provider_catalog.py')
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)


def _plist_bytes(body_xml):
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n'
        f'<plist version="1.0"><dict><key>PayloadContent</key><array><dict>{body_xml}</dict></array></dict></plist>'
    ).encode('utf-8')


class WatcherTest(unittest.TestCase):
    def test_extracts_data_not_scripts_and_rejects_unsafe_endpoints(self):
        found = w.extract(
            '<code>1.1.1.1 127.0.0.1 10.0.0.1 100.64.0.1</code><script>8.8.8.8</script>'
            '<pre>https://dns.example/dns-query tls://dns.example</pre>'
            '<div>https://127.0.0.1/dns-query https://user:pass@dns.example/dns-query</div>'
        )
        self.assertEqual(['1.1.1.1'], found['ipv4'])
        self.assertEqual(['https://dns.example/dns-query'], found['doh'])
        self.assertEqual(['dns.example'], found['explicitDot'])

    def test_magicdns_sync_endpoint(self):
        found = w.extract('<code>https://api.magicdns.ru/sync https://api.magicinfo.art/rpc</code> https://foreign.example/sync https://api.magicdns.ru/sync/other')
        self.assertEqual(['https://api.magicdns.ru/sync', 'https://api.magicinfo.art/rpc'], found['doh'])

    def test_timeout_keeps_old_observation_and_does_not_disable(self):
        p = [dict(key='one', enabled=True, url='https://dns.example/', name='One', ipv4=['1.1.1.1'])]
        previous = {'schema': 1, 'observations': {'one': {'source': 'https://dns.example/', 'ipv4': ['1.1.1.1']}}}

        def offline(url):
            raise TimeoutError()

        out, changes, errors = w.observe(p, previous, offline)
        self.assertEqual(previous, out)
        self.assertFalse(changes)
        self.assertTrue(errors)
        self.assertTrue(p[0]['enabled'])

    def test_pages_only_propose_and_identical_values_do_not_repeat(self):
        p = [dict(key='one', enabled=True, url='https://dns.example/', name='One', ipv4=['1.1.1.1'])]
        out, changes, errors = w.observe(p, {}, lambda url: '<code>8.8.8.8</code>')
        self.assertEqual(['1.1.1.1'], p[0]['ipv4'])
        self.assertEqual(1, len(changes))
        self.assertFalse(errors)
        repeated, changes, errors = w.observe(p, out, lambda url: 'Changed banner <code>8.8.8.8</code>')
        self.assertEqual(out, repeated)
        self.assertFalse(changes)

    def test_reads_bundles_without_executing_and_stays_on_source_origin(self):
        p = [dict(key='one', enabled=True, url='https://dns.example/', name='One')]
        calls = []

        def page(url):
            calls.append(url)
            return (
                '<script src="/assets/app.js"></script><script src="https://foreign.example/assets/app.js"></script>'
                if url.endswith('/')
                else 'const endpoint="https://dns.example/dns-query"; alert("never executed");'
            )

        out, changes, errors = w.observe(p, {}, page)
        self.assertEqual(['https://dns.example/', 'https://dns.example/assets/app.js'], calls)
        self.assertEqual(['https://dns.example/dns-query'], out['observations']['one']['doh'])

    def test_gzip_source_is_bounded_after_decompression(self):
        class Response(io.BytesIO):
            status = 200

        class Opener:
            def __init__(self, body):
                self.body = body

            def open(self, *a, **kw):
                return Response(self.body)

        with patch.object(w.urllib.request, 'build_opener', return_value=Opener(gzip.compress(b'<code>1.1.1.1</code>'))):
            self.assertIn('1.1.1.1', w.fetch('https://dns.example/'))
        with patch.object(w.urllib.request, 'build_opener', return_value=Opener(gzip.compress(b'x' * (w.LIMIT + 1)))):
            with self.assertRaises(ValueError):
                w.fetch('https://dns.example/')

    def test_sources_are_https_and_no_dynamic_code_urls(self):
        for url in (
            'http://example.com/',
            'file:///etc/passwd',
            'https://127.0.0.1/',
            'https://a.local/',
            'https://user:pass@dns.example/',
            'https://dns.example:444/',
        ):
            self.assertFalse(w.https_url(url, True), url)
        self.assertTrue(w.https_url('https://www.comss.ru/page.php?id=7315', True))

    def test_rejects_backtick_and_js_fragments_around_doh_endpoint(self):
        found = w.extract('const e=`https://d.webshiftvpn.ru/dns-query`,Vd=`/webshift-dns.mobileconfig`,Hd=`https://t.me/webshiftvpn`;function(){}')
        self.assertEqual(['https://d.webshiftvpn.ru/dns-query'], found['doh'])

    def test_layout_failure_cannot_erase_previous(self):
        p = [dict(key='one', enabled=True, url='https://dns.example/', name='One')]
        previous = {'schema': 1, 'observations': {'one': {'doh': ['https://dns.example/dns-query']}}}
        out, changes, errors = w.observe(p, previous, lambda url: 'Temporarily unavailable')
        self.assertEqual(previous, out)
        self.assertFalse(changes)
        self.assertTrue(errors)

    def test_decodes_content_type_charset_and_falls_back_from_unknown(self):
        class Response(io.BytesIO):
            status = 200

            def __init__(self, body, content_type):
                super().__init__(body)
                self.headers = email.message.Message()
                self.headers['Content-Type'] = content_type

        class Opener:
            def __init__(self, body, content_type):
                self.body = body
                self.content_type = content_type

            def open(self, *a, **kw):
                return Response(self.body, self.content_type)

        cp1251_html = '<p>Частный DNS: dot.example</p><p>https://doh.example/dns-query</p>'.encode('windows-1251')
        with patch.object(w.urllib.request, 'build_opener', return_value=Opener(gzip.compress(cp1251_html), 'text/html; charset=windows-1251')):
            text = w.fetch('https://dns.example/')
        self.assertIn('Частный DNS', text)
        found = w.extract(text)
        self.assertEqual(['dot.example'], found['explicitDot'])
        self.assertEqual(['https://doh.example/dns-query'], found['doh'])

        with patch.object(w.urllib.request, 'build_opener', return_value=Opener(b'<p>94.140.14.14</p>', 'text/html; charset=bogus-charset')):
            self.assertIn('94.140.14.14', w.fetch('https://dns.example/'))

    def test_rejects_redirects_and_cross_origin_assets(self):
        req = urllib.request.Request('https://dns.example/')
        self.assertIsNone(w.NoRedirect().redirect_request(req, None, 302, 'Found', {}, 'https://evil.example/'))

        class FakeRedirectHandler(urllib.request.BaseHandler):
            handler_order = 100

            def https_open(self, r):
                headers = email.message.Message()
                headers['Location'] = 'https://evil.example/'
                resp = urllib.request.addinfourl(io.BytesIO(b''), headers, r.full_url, 302)
                resp.msg = 'Found'
                return resp

        opener = urllib.request.build_opener(FakeRedirectHandler, w.NoRedirect)
        with patch.object(w.urllib.request, 'build_opener', return_value=opener):
            with self.assertRaises(urllib.error.HTTPError):
                w.fetch('https://dns.example/')

        self.assertTrue(w.same_origin('https://DNS.EXAMPLE:443/profile.mobileconfig', 'https://dns.example/setup?x=1'))
        for bad in (
            'https://dl.foreign.example/profile.mobileconfig',
            'https://sub.dns.example/profile.mobileconfig',
            'http://dns.example/profile.mobileconfig',
            'https://dns.example:444/profile.mobileconfig',
            'https://user:pass@dns.example/profile.mobileconfig',
            'https://127.0.0.1/profile.mobileconfig',
        ):
            self.assertFalse(w.same_origin(bad, 'https://dns.example/'), bad)

    def test_extracts_plain_and_cms_signed_mobileconfig_and_rejects_unsafe_xml(self):
        tls_plist = _plist_bytes(
            '<key>DNSSettings</key><dict>'
            '<key>DNSProtocol</key><string>TLS</string>'
            '<key>ServerName</key><string>dns.bezmezhau.com</string>'
            '<key>ServerAddresses</key><array><string>143.20.64.55</string><string>127.0.0.1</string><string>2a00:b700:5::3:62</string><string>fe80::1</string></array>'
            '</dict>'
        )
        found_tls = w.extract_mobileconfig(tls_plist)
        self.assertEqual(['143.20.64.55'], found_tls['ipv4'])
        self.assertEqual(['2a00:b700:5::3:62'], found_tls['ipv6'])
        self.assertEqual(['dns.bezmezhau.com'], found_tls['explicitDot'])
        self.assertEqual([], found_tls['doh'])

        cms_wrapped = b'\x30\x82\x05\x00\x06\x09\x2a\x86\x48\x86\xf7\x0d\x01\x07\x02' + _plist_bytes(
            '<key>DNSSettings</key><dict>'
            '<key>DNSProtocol</key><string>HTTPS</string>'
            '<key>ServerURL</key><string>https://api.magicinfo.art/rpc</string>'
            '<key>ServerAddresses</key><array><string>109.120.187.249</string><string>217.144.190.216</string></array>'
            '</dict>'
        ) + b'\x31\x82\x01\x00CMS-SIGNER-BYTES'
        found_cms = w.extract_mobileconfig(cms_wrapped)
        self.assertEqual(['109.120.187.249', '217.144.190.216'], found_cms['ipv4'])
        self.assertEqual(['https://api.magicinfo.art/rpc'], found_cms['doh'])

        unsafe_urls = _plist_bytes(
            '<key>DNSSettings</key><dict>'
            '<key>DNSProtocol</key><string>HTTPS</string>'
            '<key>ServerURL</key><string>http://dns.example/dns-query</string>'
            '</dict>'
        )
        self.assertEqual([], w.extract_mobileconfig(unsafe_urls)['doh'])

        local_dot = _plist_bytes(
            '<key>DNSSettings</key><dict>'
            '<key>DNSProtocol</key><string>TLS</string>'
            '<key>ServerName</key><string>router.local</string>'
            '</dict>'
        )
        self.assertEqual([], w.extract_mobileconfig(local_dot)['explicitDot'])

        for bad_xml in (
            b'<!DOCTYPE plist [<!ENTITY lol "lol">]><plist version="1.0"><dict/></plist>',
            b'<plist version="1.0"><!ENTITY xxe SYSTEM "file:///etc/passwd"></plist>',
            b'x' * (w.LIMIT + 1),
        ):
            with self.assertRaises(ValueError):
                w.extract_mobileconfig(bad_xml)

    def test_observe_fetches_only_same_origin_mobileconfig_and_survives_subasset_failure(self):
        p = [dict(key='one', enabled=True, url='https://dns.example/setup', name='One')]
        calls = []
        mc = _plist_bytes(
            '<key>DNSSettings</key><dict>'
            '<key>DNSProtocol</key><string>TLS</string>'
            '<key>ServerName</key><string>dot.example</string>'
            '</dict>'
        )

        def loader(url):
            calls.append(url)
            if url == 'https://dns.example/setup':
                return (
                    '<a href=/dns.example.mobileconfig download="ignored.mobileconfig">DL</a>'
                    '<a href="https://dl.foreign.example/other.mobileconfig">Foreign</a>'
                    '<a href="https://sub.dns.example/sub.mobileconfig">Subdomain</a>'
                    '<a href="/status.json">Status</a>'
                )
            if url == 'https://dns.example/dns.example.mobileconfig':
                return mc
            if url == 'https://dns.example/status.json':
                raise TimeoutError()
            raise AssertionError(f'Unexpected fetch: {url}')

        out, changes, errors = w.observe(p, {}, loader)
        self.assertFalse(errors)
        self.assertEqual(
            [
                'https://dns.example/setup',
                'https://dns.example/dns.example.mobileconfig',
                'https://dns.example/status.json',
            ],
            calls,
        )
        self.assertEqual(['dot.example'], out['observations']['one']['explicitDot'])

    def test_reads_next_static_bundles_only_as_fallback_without_executing_js(self):
        p = [dict(key='one', enabled=True, url='https://dns.example/', name='One')]
        calls_spa = []

        def spa_loader(url):
            calls_spa.append(url)
            if url == 'https://dns.example/':
                return (
                    '<script src="/_next/static/chunks/app.js"></script>'
                    '<script src="https://evil.example/_next/static/chunks/evil.js"></script>'
                )
            return 'const u="https://dns.example/dns-query"; process.exit(1);'

        out, _, errors = w.observe(p, {}, spa_loader)
        self.assertFalse(errors)
        self.assertEqual(['https://dns.example/', 'https://dns.example/_next/static/chunks/app.js'], calls_spa)
        self.assertEqual(['https://dns.example/dns-query'], out['observations']['one']['doh'])

        calls_ssr = []

        def ssr_loader(url):
            calls_ssr.append(url)
            if url == 'https://dns.example/':
                return '<p>153.80.200.170</p><script src="/_next/static/chunks/pricing.js"></script>'
            return 'JSON.parse(\'{"pro":{"dns_ip":"85.209.148.147"}}\')'

        out_ssr, _, errors_ssr = w.observe(p, {}, ssr_loader)
        self.assertFalse(errors_ssr)
        self.assertEqual(['https://dns.example/'], calls_ssr)
        self.assertEqual(['153.80.200.170'], out_ssr['observations']['one']['ipv4'])

    def test_extracts_json_ld_and_static_js_constants_without_executing_scripts(self):
        html_doc = (
            '<script type="application/ld+json">'
            '{"@type":"HowToStep","text":"Используйте 143.20.64.55 для IPv4, dns.bezmezhau.com для DNS-over-TLS (DoT) или https://dns.bezmezhau.com/dns-query для DNS-over-HTTPS (DoH)."}'
            '</script>'
            '<script>'
            "const DNS_HOST='dns.wlrus.cfd', PROBE='probe.dns.wlrus.cfd';"
            "const DOH='https://'+DNS_HOST+'/dns-query';"
            "card('android','Android · Частный DNS','DoT',[[DNS_HOST]]);"
            "card('link','DoT: хост и порт','',[[DNS_HOST+':853']]);"
            'const fake="8.8.8.8";'
            '</script>'
        )
        found = w.extract(html_doc)
        self.assertEqual(['143.20.64.55'], found['ipv4'])
        self.assertEqual(['https://dns.bezmezhau.com/dns-query', 'https://dns.wlrus.cfd/dns-query'], found['doh'])
        self.assertEqual(['dns.bezmezhau.com', 'dns.wlrus.cfd'], found['explicitDot'])

        p = [dict(key='ws', enabled=True, url='https://dns.webshiftvpn.ru/', name='Webshift')]
        calls = []

        def ws_loader(url):
            calls.append(url)
            if url == 'https://dns.webshiftvpn.ru/':
                return '<div id="root"></div><script type="module" src="/assets/dns-DcZE8iHt.js"></script>'
            if url == 'https://dns.webshiftvpn.ru/assets/dns-DcZE8iHt.js':
                return (
                    'Ld=`31.31.207.68`,Rd=`d.webshiftvpn.ru`,zd=`https://d.webshiftvpn.ru/dns-query`,Bd=`/webshift-dns.mobileconfig`;'
                    '`Дополнительный — `,(0,G.jsx)(Jd,{value:Ld}),` или оставьте пустым (не ставьте 8.8.8.8 или DNS провайдера — устройства будут ходить мимо).`;'
                    '`Выберите «Имя хоста поставщика частного DNS-сервера».` `Введите `,(0,G.jsx)(Jd,{value:Rd}),(0,G.Fragment,{});'
                )
            if url == 'https://dns.webshiftvpn.ru/webshift-dns.mobileconfig':
                return _plist_bytes(
                    '<key>DNSSettings</key><dict><key>DNSProtocol</key><string>HTTPS</string>'
                    '<key>ServerURL</key><string>https://d.webshiftvpn.ru/dns-query</string>'
                    '<key>ServerAddresses</key><array><string>31.31.207.68</string></array></dict>'
                )
            raise AssertionError(url)

        out, _, errors = w.observe(p, {}, ws_loader)
        self.assertFalse(errors)
        self.assertEqual(['31.31.207.68'], out['observations']['ws']['ipv4'])
        self.assertEqual(['https://d.webshiftvpn.ru/dns-query'], out['observations']['ws']['doh'])
        self.assertEqual(['d.webshiftvpn.ru'], out['observations']['ws']['explicitDot'])

    def test_extracts_structured_dot_without_tls_and_ignores_negative_or_filenames(self):
        positive = w.extract(
            '<p>Имя хоста частного DNS (DoT)\nfree.pereval.top</p>'
            '<p>В меню "Частный DNS-сервер" выберите "Вручную" и введите адрес 5u35p8m9i7.cloudflare-gateway.com</p>'
            '<p>Если роутер поддерживает DoT — сервер dot.example, порт 853, SNI sni.example.</p>'
            '<p>Используйте sub.example для DNS-over-TLS (DoT).</p>'
        )
        self.assertEqual(
            ['5u35p8m9i7.cloudflare-gateway.com', 'dot.example', 'free.pereval.top', 'sni.example', 'sub.example'],
            positive['explicitDot'],
        )

        negative = w.extract(
            '<p>«Частный DNS» в настройках Android работает только по DoT, а сервер отвечает только по DoH: dns.zapret.moe</p>'
            '<p>DoT закрыт на сервере zapret.moe</p>'
            '<p>DoT profile: dns.comss.one.mobileconfig</p>'
            '<p>DNS-over-TLS: https://cf.dns-ai.ru/dns-query quic://dns.dns-ai.ru:853 host.local</p>'
        )
        self.assertEqual([], negative['explicitDot'])

    def test_filters_warning_and_fallback_ips_while_keeping_real_provider_ips(self):
        found = w.extract(
            '<button data-clipboard="FallbackDNS=1.1.1.1 8.8.8.8\nDNS=194.87.196.13">Copy</button>'
            '<button data-clipboard="45.151.102.219">Copy2</button>'
            '<p>Если рядом стоит 1.1.1.1 или 8.8.8.8, часть запросов уйдёт мимо. Основной: 143.20.64.55.</p>'
            '<p>Дополнительный — 31.31.207.68 (не ставьте 8.8.8.8 или DNS провайдера).</p>'
            '<p>Ignore version 1.2.3.4.5 and 1.2.3.4.js</p>'
        )
        self.assertEqual(['143.20.64.55', '194.87.196.13', '31.31.207.68', '45.151.102.219'], found['ipv4'])

    def test_extracts_same_origin_status_json_and_ignores_unlinked_endpoints(self):
        p = [
            dict(key='zap', enabled=True, url='https://dns.zapret.moe/', name='Zapret'),
            dict(key='wl', enabled=True, url='https://site.dns.wlrus.cfd/', name='WLrus'),
        ]
        calls = []

        def loader(url):
            calls.append(url)
            if url == 'https://dns.zapret.moe/':
                return '<a href="/status.json">status</a><a href="https://other.example/status.json">other</a>'
            if url == 'https://dns.zapret.moe/status.json':
                return json.dumps({
                    'server': {
                        'name': 'dns.zapret.moe',
                        'doh_name': 'doh.zapret.moe',
                        'doh_public': 'doh.osjcpjzipyb.click',
                    }
                })
            if url == 'https://site.dns.wlrus.cfd/':
                return '<script>fetch("/api/status");</script>'
            if url == 'https://site.dns.wlrus.cfd/api/status':
                return json.dumps({
                    'dns': {
                        'host': 'dns.wlrus.cfd',
                        'ip': '95.85.240.254',
                        'ip2': '138.124.254.134',
                        'protocols': [{'id': 'dot'}, {'id': 'doh'}],
                    }
                })
            raise AssertionError(url)

        out, _, errors = w.observe(p, {}, loader)
        self.assertFalse(errors)
        self.assertEqual(
            [
                'https://dns.zapret.moe/dns-query',
                'https://doh.osjcpjzipyb.click/dns-query',
                'https://doh.zapret.moe/dns-query',
            ],
            out['observations']['zap']['doh'],
        )
        self.assertEqual([], out['observations']['zap']['explicitDot'])
        self.assertEqual(['138.124.254.134', '95.85.240.254'], out['observations']['wl']['ipv4'])
        self.assertEqual(['https://dns.wlrus.cfd/dns-query'], out['observations']['wl']['doh'])
        self.assertEqual(['dns.wlrus.cfd'], out['observations']['wl']['explicitDot'])
        self.assertNotIn('https://other.example/status.json', calls)


if __name__ == '__main__':
    unittest.main()
