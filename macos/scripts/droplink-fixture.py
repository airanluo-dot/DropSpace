#!/usr/bin/env python3
"""Independent vector for the pinned C# derivation/transcript. Not a Windows execution."""
import base64,hashlib,hmac,json,pathlib
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives import serialization,hashes
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
key1=ec.derive_private_key(1,ec.SECP256R1());key2=ec.derive_private_key(2,ec.SECP256R1())
def hello(key,n,platform):
    return dict(protocol=dict(major=1,minor=0),deviceId=f'00000000-0000-0000-0000-{n:012d}',displayName='Fixture',platform=platform,capabilities=127,identityFingerprint=str(n)*64,publicKeyBase64=base64.b64encode(key.public_key().public_bytes(serialization.Encoding.DER,serialization.PublicFormat.SubjectPublicKeyInfo)).decode(),nonceBase64=base64.b64encode(bytes([n])*32).decode())
a,b=hello(key1,1,1),hello(key2,2,2)
salt=hashlib.sha256((a['nonceBase64']+'|'+b['nonceBase64']).encode()).digest()
ikm=hashlib.sha256(key1.exchange(ec.ECDH(),key2.public_key())).digest()
secret=HKDF(algorithm=hashes.SHA256(),length=32,salt=salt,info=b'DropLink pairing v1').derive(ikm)
transcript='\n'.join(value for p in [a,b] for value in ['1.0',p['deviceId'].replace('-',''),p['identityFingerprint'],p['publicKeyBase64'],p['nonceBase64']]).encode()
sas=int.from_bytes(hmac.digest(secret,transcript,'sha256')[:4],'little')%1000000
nonce=base64.b64encode(bytes(range(24))).decode();body=b'{"text":"DropSpace"}';digest=hashlib.sha256(body).hexdigest()
auth=hmac.digest(secret,('\n'.join(['DropLink:v1','POST','/v1/clipboard',nonce,digest])).encode(),'sha256')
result=dict(provenance='Independent Python vector transcribed from pinned Windows source; Windows runtime execution pending',local=a,remote=b,privateKey1='0'*63+'1',secret=secret.hex(),sas=sas,nonce=nonce,bodyHash=digest,authentication=base64.b64encode(auth).decode())
path=pathlib.Path(__file__).resolve().parents[1]/'Tests/DropSpaceInfrastructureTests/Fixtures/droplink-v1.json';path.parent.mkdir(parents=True,exist_ok=True);path.write_text(json.dumps(result,indent=2)+'\n')
print('Wrote',path)
