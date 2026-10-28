import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch
import gzip
import io
spec=importlib.util.spec_from_file_location('watcher',Path(__file__).resolve().parents[1]/'tools/watch_provider_catalog.py')
w=importlib.util.module_from_spec(spec);spec.loader.exec_module(w)
class WatcherTest(unittest.TestCase):
    def test_extracts_data_not_scripts_and_rejects_unsafe_endpoints(self):
        found=w.extract('<code>1.1.1.1 127.0.0.1 10.0.0.1 100.64.0.1</code><script>8.8.8.8</script><pre>https://dns.example/dns-query tls://dns.example</pre><div>https://127.0.0.1/dns-query https://user:pass@dns.example/dns-query</div>')
        self.assertEqual(['1.1.1.1'],found['ipv4']);self.assertEqual(['https://dns.example/dns-query'],found['doh']);self.assertEqual(['dns.example'],found['explicitDot'])
    def test_timeout_keeps_old_observation_and_does_not_disable(self):
        p=[dict(key='one',enabled=True,url='https://dns.example/',name='One',ipv4=['1.1.1.1'])];previous={'schema':1,'observations':{'one':{'source':'https://dns.example/','ipv4':['1.1.1.1']}}}
        def offline(url):raise TimeoutError()
        out,changes,errors=w.observe(p,previous,offline)
        self.assertEqual(previous,out);self.assertFalse(changes);self.assertTrue(errors);self.assertTrue(p[0]['enabled'])
    def test_pages_only_propose_and_identical_values_do_not_repeat(self):
        p=[dict(key='one',enabled=True,url='https://dns.example/',name='One',ipv4=['1.1.1.1'])]
        out,changes,errors=w.observe(p,{},lambda url:'<code>8.8.8.8</code>')
        self.assertEqual(['1.1.1.1'],p[0]['ipv4']);self.assertEqual(1,len(changes));self.assertFalse(errors)
        repeated,changes,errors=w.observe(p,out,lambda url:'Changed banner <code>8.8.8.8</code>')
        self.assertEqual(out,repeated);self.assertFalse(changes)
    def test_reads_bundles_without_executing_and_stays_on_source_origin(self):
        p=[dict(key='one',enabled=True,url='https://dns.example/',name='One')];calls=[]
        def page(url):
            calls.append(url)
            return '<script src="/assets/app.js"></script><script src="https://foreign.example/assets/app.js"></script>' if url.endswith('/') else 'const endpoint="https://dns.example/dns-query"; alert("never executed");'
        out,changes,errors=w.observe(p,{},page)
        self.assertEqual(['https://dns.example/','https://dns.example/assets/app.js'],calls)
        self.assertEqual(['https://dns.example/dns-query'],out['observations']['one']['doh'])
    def test_gzip_source_is_bounded_after_decompression(self):
        class Response(io.BytesIO):
            status=200
        class Opener:
            def __init__(self,body):self.body=body
            def open(self,*a,**kw):return Response(self.body)
        with patch.object(w.urllib.request,'build_opener',return_value=Opener(gzip.compress(b'<code>1.1.1.1</code>'))):
            self.assertIn('1.1.1.1',w.fetch('https://dns.example/'))
        with patch.object(w.urllib.request,'build_opener',return_value=Opener(gzip.compress(b'x'*(w.LIMIT+1)))):
            with self.assertRaises(ValueError):w.fetch('https://dns.example/')
    def test_sources_are_https_and_no_dynamic_code_urls(self):
        for url in ('http://example.com/','file:///etc/passwd','https://127.0.0.1/','https://a.local/','https://user:pass@dns.example/','https://dns.example:444/'):
            self.assertFalse(w.https_url(url,True),url)
        self.assertTrue(w.https_url('https://www.comss.ru/page.php?id=7315',True))
    def test_layout_failure_cannot_erase_previous(self):
        p=[dict(key='one',enabled=True,url='https://dns.example/',name='One')];previous={'schema':1,'observations':{'one':{'doh':['https://dns.example/dns-query']}}}
        out,changes,errors=w.observe(p,previous,lambda url:'Temporarily unavailable')
        self.assertEqual(previous,out);self.assertFalse(changes);self.assertTrue(errors)
if __name__=='__main__':unittest.main()
