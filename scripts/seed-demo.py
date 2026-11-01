"""Populate only an empty, loopback RetailOps workspace with synthetic training records."""
import argparse
from datetime import datetime, timedelta, timezone
import http.cookiejar
import json
import os
from pathlib import Path
import secrets
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parent.parent
BASE = 'http://127.0.0.1:8150'
STATE = ROOT / '.local' / 'demo-state.json'


class Client:
    def __init__(self):
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def call(self, path, body=None, method='GET', key=None):
        headers = {'Accept': 'application/json'}
        if method != 'GET':
            headers['X-CSRF-TOKEN'] = self.call('/api/auth/csrf')['token']
            headers['Content-Type'] = 'application/json'
        if key:
            headers['Idempotency-Key'] = key
        request = urllib.request.Request(BASE + path, data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
        try:
            with self.opener.open(request, timeout=30) as response:
                raw = response.read()
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as error:
            raise RuntimeError(f'HTTP {error.code} at {path}. Local checkpoint retained; inspect the application before retrying.') from None

    def login(self, account):
        return self.call('/api/auth/login', {'email': account['email'], 'password': account['password']}, 'POST')


def save(state):
    if STATE.is_symlink() or STATE.parent.is_symlink():
        raise RuntimeError('Refusing a symbolic checkpoint path.')
    STATE.parent.mkdir(exist_ok=True)
    temporary = STATE.with_suffix('.tmp')
    if temporary.is_symlink():
        raise RuntimeError('Refusing a symbolic temporary path.')
    temporary.write_text(json.dumps(state, indent=2) + '\n', encoding='utf-8')
    temporary.chmod(0o600)
    os.replace(temporary, STATE)


def seed():
    admin = Client()
    contract = admin.call('/openapi/v1.json')
    if '/api/retail/sales' not in contract.get('paths', {}):
        raise RuntimeError('Port8150 is not the expected development RetailOps API.')
    status = admin.call('/api/auth/status')
    if STATE.is_symlink():
        raise RuntimeError('Refusing a symbolic checkpoint.')
    if STATE.exists():
        state = json.loads(STATE.read_text(encoding='utf-8'))
        if state.get('scope') != 'retailops-synthetic-demo-v1':
            raise RuntimeError('Unrecognized local checkpoint.')
        if state.get('inflight'):
            raise RuntimeError('A previous demo step has an unresolved result: ' + state['inflight'] + '. Inspect server records before resuming; no business writes attempted.')
        if status['setupRequired']:
            raise RuntimeError('Checkpoint exists but workspace is empty. No credentials or database were replaced.')
        me = admin.login(state['accounts']['admin'])
        if me['organizationId'] != state.get('organizationId'):
            raise RuntimeError('Checkpoint belongs to another organization; no data changed.')
        if state.get('complete'):
            print('Synthetic demo already complete. No data changed. Credentials: .local/LOCAL-CREDENTIALS.txt')
            return
    else:
        if not status['setupRequired']:
            raise RuntimeError('Workspace already exists and was not created by this demo tool. No data changed.')
        state = {'scope': 'retailops-synthetic-demo-v1', 'accounts': {}, 'results': {}, 'keys': {}, 'complete': False}
        for role, email in [('admin', 'admin@retailops.test'), ('manager', 'manager@retailops.test'), ('bandung', 'cashier.bandung@retailops.test'), ('bogor', 'cashier.bogor@retailops.test')]:
            state['accounts'][role] = {'email': email, 'password': 'Aa7!' + secrets.token_urlsafe(28)}
        save(state)
        owner = state['accounts']['admin']
        me = admin.call('/api/auth/setup', {'organizationName': 'RetailOps Training', 'fullName': 'Training Administrator', **owner}, 'POST')
        state['organizationId'] = me['organizationId']
        save(state)

    def step(name, action):
        if name in state['results']:
            return state['results'][name]
        if name not in state['keys']:
            state['keys'][name] = str(uuid.uuid4())
            save(state)
        state['inflight'] = name
        save(state)
        result = action(state['keys'][name])
        state['results'][name] = result
        state['inflight'] = None
        save(state)
        return result

    def natural(name, path, body, field, value):
        def action(_):
            found = admin.call(path + ('?pageSize=100' if path in ('/api/products', '/api/suppliers', '/api/users') else ''))
            rows = found['items'] if isinstance(found, dict) else found
            for row in rows:
                if row.get(field) == value:
                    return row
            return admin.call(path, body, 'POST')
        return step(name, action)

    for key, name, role in [('manager', 'Training Manager', 'Manager'), ('bandung', 'Training Cashier Bandung', 'Cashier'), ('bogor', 'Training Cashier Bogor', 'Cashier')]:
        account = state['accounts'][key]
        natural('user-' + key, '/api/users', {'fullName': name, 'role': role, **account}, 'email', account['email'])
    branches = {}
    for code, name in [('BDG', 'Bandung'), ('BGR', 'Bogor'), ('CRB', 'Cirebon')]:
        warehouse = natural('warehouse-' + code, '/api/warehouses', {'code': 'TRN-' + code, 'name': 'Training ' + name}, 'code', 'TRN-' + code)
        branches[code] = natural('branch-' + code, '/api/retail/branches', {'code': 'TRN-' + code, 'name': 'Training ' + name, 'warehouseId': warehouse['id']}, 'code', 'TRN-' + code)
    for account, code in [('bandung', 'BDG'), ('bogor', 'BGR')]:
        step('assignment-' + code, lambda _, account=account, code=code: admin.call('/api/retail/assignments', {'userId': state['results']['user-' + account]['id'], 'branchId': branches[code]['id']}, 'POST'))

    products = []
    for sku, name, cost, retail, wholesale in [
        ('PEN', 'Pulpen gel 0.5 mm', 2500, 4000, 3500), ('BOOK', 'Buku catatan A5', 18000, 25000, 22000),
        ('BINDER', 'Binder ring A4', 24000, 35000, 31000), ('PAPER', 'Kertas A4 80 gsm', 48000, 65000, 58000),
        ('TONER', 'Toner laser hitam', 135000, 180000, 165000), ('TAPE', 'Lakban bening 48 mm', 7500, 11000, 9500),
    ]:
        product = natural('product-' + sku, '/api/products', {'sku': 'TRN-' + sku, 'name': name, 'unit': 'unit', 'reorderPoint': 5, 'standardCost': cost, 'isActive': True}, 'sku', 'TRN-' + sku)
        step('price-' + sku, lambda _, product=product, retail=retail, wholesale=wholesale: admin.call('/api/retail/prices/' + product['id'], {'retailPrice': retail, 'wholesalePrice': wholesale, 'wholesaleMinimum': 3, 'expectedVersion': 0}, 'PUT'))
        for code in branches:
            step('stock-' + sku + '-' + code, lambda _, product=product, code=code: admin.call('/api/inventory/adjustments', {'productId': product['id'], 'warehouseId': branches[code]['warehouseId'], 'quantity': 20, 'reason': 'Training opening inventory'}, 'POST'))
        products.append(product)

    manager = Client(); manager.login(state['accounts']['manager'])
    supplier = natural('supplier', '/api/suppliers', {'name': 'Training Office Supply', 'email': 'supply@retailops.test', 'phone': '0000000000', 'leadTimeDays': 3}, 'email', 'supply@retailops.test')
    order = step('order', lambda _: admin.call('/api/orders', {'supplierId': supplier['id'], 'warehouseId': branches['BDG']['warehouseId'], 'expectedDate': (datetime.now().date() + timedelta(days=3)).isoformat(), 'notes': 'Training purchasing and price variance', 'lines': [{'productId': products[3]['id'], 'quantity': 10, 'unitCost': 45000}]}, 'POST'))
    step('submit', lambda _: admin.call('/api/orders/' + order['id'] + '/submit', None, 'POST'))
    step('approve', lambda _: manager.call('/api/orders/' + order['id'] + '/approve', {'reason': 'Training approval by a different person'}, 'POST'))
    step('receive', lambda key: admin.call('/api/orders/' + order['id'] + '/receipts', {'notes': 'Training stock received', 'lines': [{'orderLineId': order['lines'][0]['id'], 'quantity': 10}]}, 'POST', key))

    def sale(client, label, branch, shift, product, quantity, tier, cash):
        catalog = client.call('/api/retail/catalog?branchId=' + branch['id'])
        item = next(p for p in catalog['products'] if p['productId'] == product['id'])
        return step(label, lambda key: client.call('/api/retail/sales', {'branchId': branch['id'], 'shiftId': shift['id'], 'priceTier': tier, 'clientCreatedAt': datetime.now(timezone.utc).isoformat(), 'lines': [{'productId': item['productId'], 'quantity': quantity, 'unitPrice': item['retailPrice' if tier == 'Retail' else 'wholesalePrice'], 'priceVersion': item['version']}], 'cashTendered': cash}, 'POST', key))

    bandung = Client(); bandung.login(state['accounts']['bandung'])
    shift_a = step('shift-bandung', lambda key: bandung.call('/api/retail/shifts', {'branchId': branches['BDG']['id'], 'openingCash': 100000}, 'POST', key))
    first = sale(bandung, 'sale-pen', branches['BDG'], shift_a, products[0], 2, 'Retail', 50000)
    sale(bandung, 'sale-books', branches['BDG'], shift_a, products[1], 3, 'Wholesale', 100000)
    shift_m = step('shift-manager', lambda key: manager.call('/api/retail/shifts', {'branchId': branches['BDG']['id'], 'openingCash': 100000}, 'POST', key))
    step('return-pen', lambda key: manager.call('/api/retail/sales/' + first['id'] + '/returns', {'shiftId': shift_m['id'], 'reason': 'Training partial cash return', 'lines': [{'saleLineId': first['lines'][0]['id'], 'quantity': 1}]}, 'POST', key))
    bogor = Client(); bogor.login(state['accounts']['bogor'])
    shift_b = step('shift-bogor', lambda key: bogor.call('/api/retail/shifts', {'branchId': branches['BGR']['id'], 'openingCash': 50000}, 'POST', key))
    sale(bogor, 'sale-binder', branches['BGR'], shift_b, products[2], 2, 'Retail', 100000)
    step('close-bogor', lambda key: bogor.call('/api/retail/shifts/' + shift_b['id'] + '/close', {'countedCash': 120000}, 'POST', key))
    today = (datetime.now(timezone.utc) + timedelta(hours=7)).date()
    report = admin.call(f'/api/retail/reports?from={today.replace(day=1)}&to={today}')
    if not report['journalBalanced'] or not report['balanceSheet']['balanced']:
        raise RuntimeError('Training report did not reconcile; checkpoint preserved.')
    credentials = ROOT / '.local' / 'LOCAL-CREDENTIALS.txt'
    if credentials.is_symlink():
        raise RuntimeError('Refusing a symbolic credentials path.')
    credentials.write_text('RetailOps synthetic training workspace\nhttp://127.0.0.1:8150\n\n' + '\n\n'.join(f'{role}\nEmail: {account["email"]}\nPassword: {account["password"]}' for role, account in state['accounts'].items()) + '\n', encoding='utf-8')
    credentials.chmod(0o600)
    state['complete'] = True; save(state)
    print('Training workspace ready: 3 branches, 6 products, native purchases/sales/return. Reports reconcile.')
    print('Credentials: .local/LOCAL-CREDENTIALS.txt (ignored by Git). No passwords printed.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--confirm-synthetic', action='store_true')
    args = parser.parse_args()
    if not args.confirm_synthetic:
        parser.error('Add --confirm-synthetic to populate an empty training workspace. Existing unrelated workspaces are refused.')
    try:
        seed()
    except (OSError, ValueError, RuntimeError, urllib.error.URLError) as error:
        raise SystemExit(str(error)) from None
