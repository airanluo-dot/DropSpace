"""Protocol tests with fake model modules; never claim model accuracy/performance."""
import contextlib, importlib.util, io, json, sys, tempfile, types, unittest
from pathlib import Path
from unittest.mock import patch

class ProtocolTests(unittest.TestCase):
    def run_fixture(self, lines, target, eos=True):
        root=Path(tempfile.mkdtemp());manifest=root/'manifest.json';request=root/'request.json'
        manifest.write_text(json.dumps({'models':{d:{'converted':d,'source':d,'files':[],'tokenizerFiles':[]} for d in ['multilingual']}}))
        request.write_text(json.dumps({'manifest':str(manifest),'evidence':str(root/'evidence'),'target':target,'lines':lines}))
        calls=[]
        class Tokenizer:
            eos_token='</s>'
            src_lang='en'
            lang_code_to_token={x:'__'+x+'__' for x in ['en','zh','ja','ko']}
            @classmethod
            def from_pretrained(cls,*a,**k):return cls()
            def convert_tokens_to_ids(self,t):return t
            def encode(self,text,**k):return [self.lang_code_to_token[self.src_lang],text,'</s>']
            def convert_ids_to_tokens(self,t):return t
            def decode(self,t,**k):
                return t[0]
        class Translator:
            def __init__(self,path,**kw):self.path=path
            def translate_batch(self,encoded,**kw):
                calls.append((self.path,encoded,kw))
                return [types.SimpleNamespace(result=lambda t=t:types.SimpleNamespace(hypotheses=[[kw['target_prefix'][0][0],'translated '+t[0]]+(['</s>'] if eos else [])])) for t in encoded]
        spec=importlib.util.spec_from_file_location('helper_under_test',Path(__file__).with_name('infer.py'));module=importlib.util.module_from_spec(spec)
        import socket
        with patch.object(socket.socket,'connect'),patch.object(socket,'create_connection'):
            spec.loader.exec_module(module)
            with patch.dict(sys.modules,{'ctranslate2':types.SimpleNamespace(Translator=Translator),'transformers':types.SimpleNamespace(M2M100Tokenizer=Tokenizer)}),patch.object(sys,'argv',['infer','--site-packages',str(root),'--request',str(request)]),patch.object(sys,'path',sys.path.copy()),contextlib.redirect_stdout(io.StringIO()) as output:
                module.main()
        return json.loads(output.getvalue()),calls,root
    def test_source_only_avoids_model(self):
        result,calls,_=self.run_fixture([{'id':7,'language':'en','text':'Original'}],'en')
        self.assertEqual(calls,[]);self.assertEqual(result['modelCalls'],0)
        self.assertEqual(result['outputs']['cold'],[{'id':7,'text':'Original'}])
    def test_direct_target_prefix_and_order(self):
        result,calls,root=self.run_fixture([{'id':3,'language':'ja','text':'Japanese'},{'id':2,'language':'ko','text':'Korean'},{'id':8,'language':'zh','text':'Unchanged'}],'zh')
        self.assertEqual([x[0] for x in calls],['multilingual']*4)
        self.assertTrue(all(call[2]['target_prefix']==[['__zh__']] for call in calls))
        self.assertEqual(calls[0][1][0][0],'__ja__')
        self.assertEqual(calls[1][1][0][0],'__ko__')
        self.assertEqual([x['id'] for x in result['outputs']['cold']],[3,2,8])
        self.assertEqual(result['outputs']['cold'][-1]['text'],'Unchanged')
        self.assertTrue((root/'evidence/cold-ja-zh.raw.json').exists())
    def test_direct_isolated_interpreter_ignores_pth(self):
        import subprocess
        root=Path(tempfile.mkdtemp()); packages=root/'site-packages';packages.mkdir()
        marker=root/'pth-executed'
        (packages/'unsafe.pth').write_text("import pathlib; pathlib.Path("+repr(str(marker))+").touch()")
        manifest=root/'manifest.json';manifest.write_text('{"models":{}}')
        request=root/'request.json';request.write_text(json.dumps({'manifest':str(manifest),'evidence':str(root/'evidence'),'target':'en','lines':[{'id':1,'language':'en','text':'雪 remains unchanged'}]}))
        run=subprocess.run([sys.executable,'-I','-S','-X','utf8',str(Path(__file__).with_name('infer.py')),'--site-packages',str(packages),'--request',str(request)],capture_output=True,encoding='utf-8',timeout=10)
        self.assertEqual(run.returncode,0,run.stderr)
        self.assertEqual(json.loads(run.stdout)['modelCalls'],0)
        self.assertIn('雪',run.stdout)
        self.assertFalse(marker.exists())
    def test_missing_eos_rejects(self):
        with self.assertRaisesRegex(ValueError,'without EOS'):self.run_fixture([{'id':0,'language':'zh','text':'Chinese'}],'en',False)

if __name__=='__main__':unittest.main()
