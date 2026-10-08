# Adds the benchmark profiles to a Resesh --data-dir sessions.json (built from its "Command
# Prompt" profile): "Flood <wl>" types a workload through ConPTY, "SSH <wl>" connects to
# sshflood.py on 127.0.0.1:2223 (username = workload, no authentication), plus the idle
# baselines "Idle baseline" and "SSH idle".
#   python profiles.py <resesh --data-dir> <workload dir>
import json
import os
import sys

data_dir, workloads = sys.argv[1], sys.argv[2]
path = os.path.join(data_dir, 'sessions.json')
with open(path, encoding='utf-8-sig') as f:
    store = json.load(f)
base = next(s for s in store['sessions'] if s['name'] == 'Command Prompt')


def add(index, name, **fields):
    session = json.loads(json.dumps(base))
    session.update(id='0a7e5700-0000-4000-8000-%012x' % (0xbe000 + index), name=name, builtIn=False, **fields)
    store['sessions'] = [s for s in store['sessions'] if s['id'] != session['id']] + [session]


def local(arguments):
    return {'executable': r'C:\Windows\System32\cmd.exe', 'arguments': ['/c', arguments],
            'startingDirectory': '', 'environment': None}


names = ['logs', 'color', 'unicode', 'tui', 'tiny']
for i, wl in enumerate(names):
    add(i, 'Flood ' + wl, local=local('chcp 65001>nul & type ' + os.path.join(workloads, wl + '.bin')))
add(len(names), 'Idle baseline', local=local('echo ready'))
for i, wl in enumerate(names + ['idle']):
    add(16 + i, 'SSH ' + wl, kind='ssh', local=None, host='127.0.0.1', port=2223, username=wl,
        authMethod='none', icon=None)
with open(path, 'w', encoding='utf-8') as f:
    json.dump(store, f, indent=2)
print('profiles written to', path)
