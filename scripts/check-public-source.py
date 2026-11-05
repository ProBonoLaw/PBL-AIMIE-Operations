#!/usr/bin/env python3
"""Check tracked source without printing private values."""
import re
import subprocess
from pathlib import Path


GUID = re.compile(r"\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b", re.I)
PUBLIC_IDS = {
    # Microsoft AKS server application and built-in Cosmos contributor role.
    "6dae42f8-4368-4678-94ff-3960e28e3630",
    "00000000-0000-0000-0000-000000000002",
}
HOST = re.compile(r"\b([a-z0-9-]+)\.(?:sharepoint\.com|azurecr\.io|vault\.azure\.net|openai\.azure\.com|documents\.azure\.com|servicebus\.windows\.net|(?:blob|queue)\.core\.windows\.net|azurewebsites\.net)\b", re.I)


def findings(name, contents):
    if (name.endswith(('.pem', '.pfx', '.key', '.nupkg', '.dll'))
            or Path(name).name in {'.env', 'config.toml'}):
        yield 1, 'private-or-generated-file'
    for number, line in enumerate(contents.splitlines(), 1):
        for match in GUID.finditer(line):
            value = match[0].lower()
            if value not in PUBLIC_IDS and len(set(value.replace('-', ''))) != 1:
                yield number, 'non-example-identifier'
        for match in HOST.finditer(line):
            if not re.fullmatch(r'(?:example[a-z0-9-]*|account|test|contoso)', match[1], re.I):
                yield number, 'deployment-hostname'
        if re.search(r'https?://github\.com/(?!probonolaw/)(?:daat[^/\s]*)/', line, re.I):
            yield number, 'private-repository-url'
        if re.search(r'(?:/Users/|[A-Z]:\\Users\\)[a-zA-Z0-9_.-]+(?:/|\\)', line):
            yield number, 'personal-workstation-path'


def main():
    files = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z']).decode().split('\0')
    count = 0
    for name in sorted(set(files) - {''}):
        path = Path(name)
        if not path.exists():
            continue
        if path.is_symlink() or not path.is_file():
            print(f'{name}:1: non-regular-file')
            count += 1
            continue
        try:
            contents = path.read_text()
        except UnicodeError:
            print(f'{name}:1: binary-file-needs-review')
            count += 1
            continue
        for number, rule in findings(name, contents):
            print(f'{name}:{number}: {rule}')
            count += 1
    print(f'Public source scan: {count} findings.')
    return bool(count)


if __name__ == '__main__':
    raise SystemExit(main())
