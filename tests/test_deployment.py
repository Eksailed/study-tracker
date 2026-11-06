"""Настоящий HTTPS через Caddy, постоянные тома и восстановление копии.

Запуск: python tests/test_deployment.py. Для обнаружения через unittest нужен RUN_DEPLOYMENT_TESTS=1.
Используются только контейнеры и тома с новым случайным префиксом zachet-test-.
"""
import json
import os
from pathlib import Path
import socket
import ssl
import subprocess
import time
import unittest
import urllib.request
import uuid
from support import Client, ROOT


@unittest.skipUnless(__name__ == '__main__' or os.environ.get('RUN_DEPLOYMENT_TESTS') == '1',
                     'Контейнерные проверки запускаются отдельно')
class DeploymentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.prefix = 'zachet-test-' + uuid.uuid4().hex[:10]
        cls.project = cls.prefix
        cls.projects = [cls.prefix]
        cls.backup_volume = cls.prefix + '-backup'
        def free_port():
            with socket.socket() as sock:
                sock.bind(('127.0.0.1',0))
                return str(sock.getsockname()[1])
        cls.env = dict(os.environ, DOMAIN='localhost', BIND_ADDRESS='127.0.0.1',
                       HTTP_PORT=free_port(), HTTPS_PORT=free_port(),
                       TRACKER_IMAGE=os.environ.get('TRACKER_TEST_IMAGE','study-tracker:local'),
                       BACKEND_SUBNET='172.29.246.0/24', BACKEND_POOL='172.29.246.128/25', PROXY_IP='172.29.246.2')
        cls.base = 'https://localhost:' + cls.env['HTTPS_PORT']
        cls.context = ssl.create_default_context()
        try:
            cls.compose('up','-d')
            cls.load_ca_and_wait()
            for name in ('a','b'):
                cls.compose('run','--rm','--no-deps','-T','app','--create-account',f'{name}@example.test',
                            input='container-test-password-2026\n')
            cls.a, cls.b = Client(cls.base,cls.context), Client(cls.base,cls.context)
            cls.expected = {}
            for client,name in ((cls.a,'a'),(cls.b,'b')):
                if client.login(f'{name}@example.test','container-test-password-2026')[0] != 200:
                    raise RuntimeError('Вход по HTTPS не выполнен')
                code,subject = client.request('subjects','POST',dict(name=f'Предмет {name}',color='teal'))
                if code != 201:
                    raise RuntimeError(f'Предмет не создан: {code} {subject}')
                code,task = client.request('assignments','POST',dict(title=f'Задание {name}',subjectId=subject['id'],
                                           dueDate='2026-12-20',description='Сохранить в копии',status='InProgress'))
                if code != 201:
                    raise RuntimeError(f'Задание не создано: {code} {task}')
                cls.expected[name] = task['id']
        except Exception:
            cls.cleanup()
            raise

    @classmethod
    def docker(cls,*args,input=None,check=True):
        result = subprocess.run(['docker',*args],cwd=ROOT,env=cls.env,input=input,
                                 capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=180)
        if check and result.returncode:
            raise RuntimeError(f'docker {args[0]}: {result.stdout}\n{result.stderr}')
        return result

    @classmethod
    def compose(cls,*args,**kwargs):
        return cls.docker('compose','-f',str(ROOT/'compose.yaml'),'-p',cls.project,*args,**kwargs)

    @classmethod
    def load_ca_and_wait(cls):
        for _ in range(100):
            result = cls.compose('exec','-T','caddy','cat','/data/caddy/pki/authorities/local/root.crt',check=False)
            if result.returncode == 0 and 'BEGIN CERTIFICATE' in result.stdout:
                cls.context.load_verify_locations(cadata=result.stdout)
                break
            time.sleep(.2)
        else:
            raise RuntimeError(cls.compose('logs','--tail','50',check=False).stdout)
        cls.wait_health()

    @classmethod
    def wait_health(cls):
        for _ in range(100):
            try:
                with urllib.request.urlopen(cls.base+'/health',context=cls.context,timeout=2) as response:
                    if response.status == 200:
                        return
            except OSError:
                time.sleep(.2)
        raise RuntimeError(cls.compose('logs','--tail','50',check=False).stdout)

    @classmethod
    def cleanup(cls):
        for project in cls.projects:
            if not project.startswith(cls.prefix):
                raise RuntimeError('Отказ от очистки постороннего проекта')
            cls.project = project
            cls.compose('down','--volumes','--remove-orphans',check=False)
        cls.docker('volume','rm',cls.backup_volume,check=False)

    @classmethod
    def tearDownClass(cls):
        cls.cleanup()

    def assert_records(self):
        for client,name in ((self.a,'a'),(self.b,'b')):
            code,tasks = client.request('assignments')
            self.assertEqual(code,200)
            self.assertEqual([t['id'] for t in tasks],[self.expected[name]])
            self.assertEqual(tasks[0]['status'],'InProgress')
            self.assertEqual(tasks[0]['dueDate'],'2026-12-20')

    def test_health_returns_200(self):
        with urllib.request.urlopen(self.base+'/health',context=self.context) as response:
            self.assertEqual(response.status,200)
            self.assertEqual(json.load(response),{'status':'ok'})
        self.assertNotEqual(self.compose('exec','-T','app','id','-u').stdout.strip(),'0')
        self.assertFalse(self.a.request('auth/options')[1]['allowRegistration'])
        self.assertTrue(next(c for c in self.a.jar if c.name == 'Zachet.Account').secure)

    def test_container_restart_preserves_data(self):
        self.compose('up','-d','--no-deps','--force-recreate','app')
        self.wait_health()
        self.assert_records()

    def test_cookie_survives_container_restart(self):
        self.compose('up','-d','--no-deps','--force-recreate','app')
        self.wait_health()
        self.assertEqual(self.a.request('auth/me')[1]['email'],'a@example.test')
        self.assertEqual(self.b.request('auth/me')[1]['email'],'b@example.test')

    def test_backup_restore_preserves_records(self):
        original = self.project
        self.compose('stop','app','caddy')
        self.docker('volume','create',self.backup_volume)
        for volume in ('data','keys'):
            self.docker('run','--rm','--network','none','-v',f'{original}_{volume}:/source:ro',
                        '-v',f'{self.backup_volume}:/backup','alpine:3','tar','-C','/source','-cf',f'/backup/{volume}.tar','.')
        self.compose('down')
        type(self).project = original+'-restored'
        self.projects.append(self.project)
        for volume in ('data','keys'):
            self.docker('volume','create',f'{self.project}_{volume}')
            self.docker('run','--rm','--network','none','-v',f'{self.project}_{volume}:/target',
                        '-v',f'{self.backup_volume}:/backup:ro','alpine:3','tar','-C','/target','-xf',f'/backup/{volume}.tar')
        self.compose('up','-d')
        self.load_ca_and_wait()
        self.assert_records()
        self.assertEqual(self.a.request('auth/me')[0],200)


if __name__ == '__main__':
    unittest.main(verbosity=2)
