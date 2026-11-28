# Собирает «заводской» профиль Ulanzi (D200X) из твоего текущего профиля:
# profiles\Default Profile.ulanziDeckProfile — тот же формат, что у Ulanzi: строка «#Version: 2» и zip с папкой профиля.
# Профилю даётся новый ID, чтобы он не столкнулся с текущим. Ставит его tools\install-default-profile.ps1.
import io, json, os, sys, uuid, zipfile

here = os.path.dirname(os.path.abspath(__file__))
profiles = os.path.join(os.environ['APPDATA'], 'Ulanzi', 'UlanziDeck', 'ProfilesV2')
names = [n for n in os.listdir(profiles) if n.endswith('.ulanziProfile')]
if len(sys.argv) > 1: names = [n for n in names if n.startswith(sys.argv[1])]
if len(names) != 1: sys.exit('профилей: %d — укажи начало ID профиля: %s' % (len(names), ', '.join(names)))
src = os.path.join(profiles, names[0])

root = str(uuid.uuid4()) + '.ulanziProfile'
buf = io.BytesIO()
with zipfile.ZipFile(buf, 'w', zipfile.ZIP_DEFLATED) as z:
    z.writestr(root + '/', b'')
    for d, dirs, files in os.walk(src):
        dirs.sort()
        rel = os.path.relpath(d, src).replace(os.sep, '/')
        base = root if rel == '.' else root + '/' + rel
        if rel != '.': z.writestr(base + '/', b'')
        for f in sorted(files):
            z.write(os.path.join(d, f), base + '/' + f)

out_dir = os.path.join(os.path.dirname(here), 'profiles')
os.makedirs(out_dir, exist_ok=True)
out = os.path.join(out_dir, 'Default Profile.ulanziDeckProfile')
with open(out, 'wb') as f: f.write(b'#Version: 2\n' + buf.getvalue())

# проверка: читается так же, как заводской
b = open(out, 'rb').read(); zz = zipfile.ZipFile(io.BytesIO(b[b.index(b'PK'):]))
m = json.loads(zz.read(root + '/manifest.json'))
print('%s — %d КБ, страниц %d, профиль «%s», дека %s' % (out, len(b) // 1024, len(m['Pages']['Pages']), m['Name'], m['Device'].get('Model')))
