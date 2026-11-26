#!/usr/bin/env python3
"""Pre-start, field-scoped merge into Jellyfin's persisted plugin configuration.

Absent env = unmanaged (including on a retained PVC). Empty URL = explicitly
clear/disable. Nonempty URL = HTTP(S) service base URL, without auth/query/fragment.
Inputs and existing XML are validated before any write. No values/secrets logged.
"""
import argparse
import os
from pathlib import Path
import stat
import tempfile
from urllib.parse import urlsplit
from xml.dom import minidom


def service_url(value):
    if not value:
        return value
    parsed = urlsplit(value)
    if (value != value.strip() or any(ord(c) < 33 or c in '<>"\\' for c in value)
            or parsed.scheme not in ('http', 'https') or not parsed.hostname
            or parsed.username is not None or parsed.password is not None
            or '?' in value or '#' in value):
        raise ValueError('expected HTTP(S) base URL without credentials, query or fragment')
    # Access validates port syntax/range. Preserve exact URL (no silent normalization).
    parsed.port
    return value


def merge(path, env):
    fields = {}
    if 'PHANTOM_SWARM_HEALTH_SERVICE_URL' in env:
        fields['SwarmHealthServiceUrl'] = service_url(env['PHANTOM_SWARM_HEALTH_SERVICE_URL'])
    if 'PHANTOM_PROWLARR_API_KEY_FILE' in env:
        fields['ProwlarrBaseUrl'] = service_url(env['PHANTOM_PROWLARR_BASE_URL'])
        key = Path(env['PHANTOM_PROWLARR_API_KEY_FILE']).read_text().rstrip('\r\n')
        if not key:
            raise ValueError('Prowlarr secret is empty')
        fields['ProwlarrApiKey'] = key
    if not fields:
        return
    for value in fields.values():
        if any(not (c in '\t\n\r' or 0x20 <= ord(c) <= 0xD7FF
                    or 0xE000 <= ord(c) <= 0xFFFD or 0x10000 <= ord(c) <= 0x10FFFF) for c in value):
            raise ValueError('input contains invalid XML characters')

    old = path.read_bytes() if path.exists() else None
    if old is not None:
        doc = minidom.parseString(old)
        if doc.doctype is not None:
            raise ValueError('DOCTYPE is not supported')
    else:
        doc = minidom.parseString('<PluginConfiguration/>')
    root = doc.documentElement
    if root.tagName != 'PluginConfiguration' or root.namespaceURI:
        raise ValueError('expected PluginConfiguration XML root')
    for name, value in fields.items():
        nodes = [n for n in root.childNodes if n.nodeType == n.ELEMENT_NODE and n.tagName == name]
        if len(nodes) > 1:
            raise ValueError('duplicate managed field: ' + name)
        node = nodes[0] if nodes else root.appendChild(doc.createElement(name))
        for child in list(node.childNodes):
            node.removeChild(child)
        # Drop xsi:nil on a managed field, without altering unrelated attributes.
        if node.hasAttribute('xsi:nil'):
            node.removeAttribute('xsi:nil')
        node.appendChild(doc.createTextNode(value))
    data = doc.toxml(encoding='utf-8')
    if data == old:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    previous = path.stat() if old is not None else None
    fd, temporary = tempfile.mkstemp(prefix='.' + path.name, dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as output:
            output.write(data)
            if previous is not None:
                os.fchown(output.fileno(), previous.st_uid, previous.st_gid)
                os.fchmod(output.fileno(), stat.S_IMODE(previous.st_mode))
            else:
                # Init runs as root; Jellyfin must be able to read fresh config.
                os.fchmod(output.fileno(), 0o644)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    print('merge-plugin-config: reconciled ' + ', '.join(fields))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, default=Path('/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml'))
    args = parser.parse_args()
    try:
        merge(args.config, os.environ)
    except Exception as error:
        # Parser exceptions may include secret XML content: report type, not payload.
        raise SystemExit('merge-plugin-config: invalid input/config or write failure (' + type(error).__name__ + ')') from None
