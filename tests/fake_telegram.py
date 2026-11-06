"""Локальная подмена Bot API. Ни одного обращения к Telegram."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import threading
import socket

TOKEN = '123456:fake-token-for-tests'


class FakeTelegram:
    def __init__(self, host='127.0.0.1', port=0):
        self.updates = []
        self.messages = []
        self.responses = []
        self.calls = []
        self.webhook = ''
        self.lock = threading.Lock()
        owner = self
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_POST(self):
                method = self.path.rsplit('/',1)[-1]
                data = json.loads(self.rfile.read(int(self.headers.get('Content-Length','0'))) or b'{}')
                with owner.lock:
                    owner.calls.append(method)
                    if method == 'getMe':
                        result = {'id':123456,'is_bot':True,'username':'zachet_test_bot'}
                    elif method == 'getWebhookInfo':
                        result = {'url':owner.webhook}
                    elif method == 'getUpdates':
                        result = [u for u in owner.updates if u['update_id'] >= data.get('offset',0)]
                    elif method == 'sendMessage':
                        owner.messages.append(data)
                        if owner.responses:
                            response = owner.responses.pop(0)
                            if response == 'disconnect':
                                self.connection.shutdown(socket.SHUT_RDWR)
                                self.connection.close()
                                return
                            self.reply(response.get('error_code',200),response)
                            return
                        result = {'message_id':len(owner.messages),'chat':{'id':data['chat_id'],'type':'private'},'text':data['text']}
                    else:
                        self.reply(404,{'ok':False,'error_code':404})
                        return
                self.reply(200,{'ok':True,'result':result})

            def reply(self, code, data):
                payload = json.dumps(data).encode()
                self.send_response(code)
                self.send_header('Content-Type','application/json')
                self.send_header('Content-Length',str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
        self.http = ThreadingHTTPServer((host,port), Handler)
        self.base = f'http://127.0.0.1:{self.http.server_port}'
        self.thread = threading.Thread(target=self.http.serve_forever,daemon=True)
        self.thread.start()

    def update(self, code, chat=10000000001, kind='private', update_id=None):
        with self.lock:
            number = update_id if update_id is not None else len(self.updates)+1
            self.updates.append({'update_id':number,'message':{'from':{'id':chat,'is_bot':False},
                                 'chat':{'id':chat,'type':kind},'text':'/start '+code}})

    def close(self):
        self.http.shutdown()
        self.http.server_close()
        self.thread.join(timeout=5)


if __name__ == '__main__':
    import time
    server = FakeTelegram('0.0.0.0',8081)
    try:
        while True:
            time.sleep(1)
    finally:
        server.close()
