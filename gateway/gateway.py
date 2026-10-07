#!/usr/bin/env python3
"""Invoice-only CLN adapter boundary. No TCP listener and no spend RPCs."""
import json
import os
import re
import socket
import socketserver
import threading

UPSTREAM = os.environ.get('CLN_SOCKET', '/cln/lightning-rpc')
LISTEN = os.environ.get('GATEWAY_SOCKET', '/gateway/lightning-rpc')
EXPECTED_NODE = os.environ.get('EXPECTED_NODE', '')
ADVERTISE_HOST = os.environ.get('CLN_ADVERTISE_HOST', '')
ADVERTISE_PORT = int(os.environ.get('CLN_ADVERTISE_PORT', '9735'))
# Persisted invoice ownership boundary: retain across the XBTPay rename.
PREFIX = 'paperclip-btcpay:'
LIMIT = 262144

def read_json(sock):
    data = b''
    while len(data) < LIMIT:
        chunk = sock.recv(8192)
        if not chunk: raise ValueError('Connection ended')
        data += chunk
        try: return json.loads(data)
        except json.JSONDecodeError: pass
    raise ValueError('Request too large')

def rpc(method, params):
    with socket.socket(socket.AF_UNIX) as s:
        s.settimeout(70 if method == 'waitanyinvoice' else 15)
        s.connect(UPSTREAM)
        s.sendall(json.dumps({'jsonrpc':'2.0','id':1,'method':method,'params':params}).encode()+b'\n\n')
        response = read_json(s)
    if 'error' in response: raise ValueError(response['error'].get('message','CLN error'))
    return response['result']

def compatible(info):
    features = info.get('our_features', {})
    return (bool(re.fullmatch(r'(02|03)[0-9a-f]{64}', EXPECTED_NODE))
            and info.get('id') == EXPECTED_NODE and info.get('network') == 'bitcoin'
            and not info.get('warning_bitcoind_sync') and not info.get('warning_lightningd_sync')
            and all(int(features.get(k,'0'),16) & (1<<512) for k in ('init','node','invoice'))
            and all(int(features.get(k,'0'),16) & ((1<<514)|(1<<515)) for k in ('init','node')))

def own(item):
    return isinstance(item.get('label'),str) and item['label'].startswith(PREFIX)

def clean(item):
    item = dict(item)
    if own(item): item['label'] = item['label'][len(PREFIX):]
    return item

def label(value):
    if not isinstance(value,str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,120}',value):
        raise ValueError('Invalid BTCPay invoice label')
    return PREFIX+value

def handle(method, params):
    if not isinstance(params,list): raise ValueError('Positional parameters required')
    if method == 'getinfo':
        info = rpc(method, [])
        if not compatible(info): raise ValueError('Expected synced XBT CLN with bits 512 and 514/515')
        if not info.get('address') and ADVERTISE_HOST:
            import ipaddress
            address_type = 'ipv6' if ipaddress.ip_address(ADVERTISE_HOST).version == 6 else 'ipv4'
            info['address'] = [{'type':address_type,'address':ADVERTISE_HOST,'port':ADVERTISE_PORT}]
        return info
    if method == 'invoice':
        if not compatible(rpc('getinfo',[])): raise ValueError('XBT CLN unavailable or incompatible')
        if not 4 <= len(params) <= 9: raise ValueError('Invalid invoice arguments')
        params = list(params)
        params[1] = label(params[1])
        if params[0] != 'any' and not 0 < int(str(params[0]).removesuffix('msat')) <= 100000000000:
            raise ValueError('Invoice amount out of range')
        if not 1 <= int(params[3]) <= 86400: raise ValueError('Invoice expiry out of range')
        if len(params)>4 and params[4] is not None: raise ValueError('On-chain fallbacks not supported')
        if len(params)>5 and params[5] is not None: raise ValueError('Custom preimages not supported')
        # This LAN node receives through private channels. Include the final-hop
        # hints in merchant invoices; this does not announce channels publicly.
        while len(params) < 7: params.append(None)
        params[6] = True
        return clean(rpc(method,params))
    if method == 'listinvoices':
        params = list(params)
        if params and params[0] is not None: params[0] = label(params[0])
        return {'invoices':[clean(x) for x in rpc(method,params)['invoices'] if own(x)]}
    if method == 'delinvoice':
        if len(params)!=2 or params[1]!='unpaid': raise ValueError('Only unpaid invoice cancellation allowed')
        return clean(rpc(method,[label(params[0]),'unpaid']))
    if method == 'waitanyinvoice':
        index = int(params[0] or 0) if params else 0
        while True:
            item = rpc(method,[index,60])
            if own(item): return clean(item)
            index = item['pay_index']
    if method in ('listfunds','listpeerchannels') and not params:
        return rpc(method,[])
    raise ValueError('RPC is not permitted by the invoice-only adapter')

class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        request = {}
        try:
            self.request.settimeout(15)
            request = read_json(self.request)
            result = handle(request.get('method'),request.get('params',[]))
            response = {'jsonrpc':'2.0','id':request.get('id'),'result':result}
        except Exception as e:
            response = {'jsonrpc':'2.0','id':request.get('id'),'error':{'code':-32602,'message':str(e)}}
        try: self.request.sendall(json.dumps(response).encode()+b'\n\n')
        except OSError: pass

class Server(socketserver.ThreadingUnixStreamServer):
    daemon_threads = True
    # Bound concurrent waiters/requests instead of creating unlimited threads.
    slots = threading.BoundedSemaphore(32)
    def process_request(self, request, client_address):
        if not self.slots.acquire(blocking=False):
            request.close()
            return
        try: super().process_request(request,client_address)
        except Exception:
            self.slots.release()
            raise
    def process_request_thread(self, request, client_address):
        try: super().process_request_thread(request,client_address)
        finally: self.slots.release()

if __name__ == '__main__':
    if not re.fullmatch(r'(02|03)[0-9a-f]{64}', EXPECTED_NODE):
        raise SystemExit('Set EXPECTED_NODE to your XBT CLN public node ID')
    os.makedirs(os.path.dirname(LISTEN),exist_ok=True)
    if os.path.exists(LISTEN): os.unlink(LISTEN)
    with Server(LISTEN,Handler) as server:
        os.chmod(LISTEN,0o660)
        server.serve_forever()
