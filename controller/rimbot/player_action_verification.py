"""Observed boundaries for immediate player settings; never certify pawn labor."""


def selection_identity(payload):
    rows = payload.get('selectedObjects')
    if payload.get('success') is not True or not isinstance(rows, list) or payload.get('selectedCount') != len(rows):
        raise ValueError('Native selection is incomplete; inspect again')
    ids = [row.get('id') for row in rows]
    if any(value is None for value in ids):
        raise ValueError('Native selection lacks stable identities')
    return sorted(map(str, ids))


def verify_bill_whitelist(receipt, observed):
    write = receipt.get('write') or {}
    expected = (write.get('after') or {}).get('bill') or {}
    if receipt.get('applied') is not True or write.get('refused') is not False or observed.get('success') is not True:
        raise ValueError('Bill whitelist was not applied; inspect before retrying')
    identity = expected.get('billId')
    rows = [row for bench in observed.get('benches', []) for row in bench.get('bills', [])
            if row.get('billId') == identity]
    if not identity or len(rows) != 1 or not expected.get('filter') or rows[0].get('filter') != expected['filter']:
        raise ValueError('Bill ingredient filter differs from fresh native readback')


def verify_main_tab_closed(arguments, observed):
    if observed.get('mainTabOpen') is False:
        return
    current = observed.get('openMainTabId')
    requested = str(arguments['mainTabId']).removeprefix('main-tab:')
    if not current or str(current).removeprefix('main-tab:') == requested:
        raise ValueError('Main tab closure was not confirmed')


def verify_zone_edit(arguments, receipt, observed):
    if receipt.get('success') is not True or observed.get('success') is not True:
        raise ValueError('Zone edit/readback failed; inspect before retrying')
    operation = arguments.get('op')
    if operation not in ('add', 'remove', 'delete', 'crop', 'filter'):
        return
    identity = (receipt.get('zone') or {}).get('id')
    if identity is None:
        identity = arguments.get('zone')
    rows = [row for row in observed.get('zones', []) if str(row.get('id')) == str(identity)]
    removed = operation == 'delete' or (operation == 'remove' and receipt.get('after', {}).get('gridCellCount') == 0)
    if removed:
        if rows:
            raise ValueError('Deleted zone still exists')
        return
    if len(rows) != 1:
        raise ValueError('Edited zone is missing or ambiguous')
    zone = rows[0]
    if operation == 'crop' and (not zone.get('plantDef') or zone.get('plantDef') != receipt.get('after', {}).get('plantDef')):
        raise ValueError('Zone crop differs from native edit readback')
    if operation == 'filter' and (not isinstance(zone.get('filter'), dict) or zone.get('filter') != receipt.get('after')):
        raise ValueError('Zone filter differs from native edit readback')
    if operation in ('add', 'remove'):
        cells = zone.get('gridCells')
        if not isinstance(cells, list) or zone.get('gridCellsNotListed', 0) or zone.get('gridCellCount') != len(cells):
            raise ValueError('Zone geometry readback is incomplete')
        actual = {(cell['x'], cell['z']) for cell in cells}
        requested = receipt.get('cells')
        if not isinstance(requested, list) or not requested or any(cell.get('accepted') is not True for cell in requested):
            raise ValueError('Zone edit only partially accepted; inspect before retrying')
        expected = {(cell['x'], cell['z']) for cell in requested}
        if (operation == 'add' and not expected <= actual) or (operation == 'remove' and expected & actual):
            raise ValueError('Zone geometry does not match requested edit')
