"""Fetch pinned, upstream build dependencies into this checkout only."""
import concurrent.futures, hashlib, json, pathlib, urllib.request, zipfile, time
ROOT = pathlib.Path(__file__).resolve().parents[1]
DOWNLOADS = ROOT / '.downloads'
DOWNLOADS.mkdir(parents=True, exist_ok=True)

def fetch(url, name, destination=None, sha512=None):
    target = DOWNLOADS / name
    if not target.exists():
        print('Downloading ' + name, flush=True)
        request = urllib.request.Request(url, headers={'User-Agent': 'LumaMusic-Build/0.1'})
        for attempt in range(3):
            try:
                with urllib.request.urlopen(request, timeout=90) as response, target.with_suffix('.part').open('wb') as output:
                    while chunk := response.read(1024*1024): output.write(chunk)
                target.with_suffix('.part').replace(target)
                break
            except Exception:
                if attempt == 2: raise
                time.sleep(2)
    if sha512 and hashlib.sha512(target.read_bytes()).hexdigest().lower() != sha512.lower():
        raise RuntimeError('Checksum mismatch: ' + name)
    if destination:
        folder = ROOT / destination
        folder.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(target) as archive:
            for info in archive.infolist():
                resolved = (folder / info.filename).resolve()
                if not resolved.is_relative_to(folder.resolve()): raise RuntimeError('Unsafe ZIP path')
            archive.extractall(folder)
    print('Ready ' + name, flush=True)
    return target

if __name__ == '__main__':
    jobs = [
        ('https://dotnetcli.blob.core.windows.net/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.zip','dotnet-sdk.zip','.tools/dotnet','9b8b88590e4da131bfd0da7aa089d0fc04d5418d5f8607ec13d55dc5a17b4399afd54d496c12657fa05c6c6546dc5eab930f26ac6c50f2d3a7712c0fb378c366'),
        ('https://www.un4seen.com/files/bass24.zip','bass24.zip','vendor/bass'),
        ('https://www.un4seen.com/files/basswasapi24.zip','basswasapi24.zip','vendor/basswasapi'),
        ('https://www.un4seen.com/files/bassasio14.zip','bassasio14.zip','vendor/bassasio'),
        ('https://www.un4seen.com/files/bassdsd24.zip','bassdsd24.zip','vendor/bassdsd'),
        ('https://www.un4seen.com/files/bassflac24.zip','bassflac24.zip','vendor/bassflac'),
        ('https://www.un4seen.com/files/bassape24.zip','bassape24.zip','vendor/bassape'),
        ('https://www.un4seen.com/files/bassalac24.zip','bassalac24.zip','vendor/bassalac'),
        ('https://www.un4seen.com/files/bassopus24.zip','bassopus24.zip','vendor/bassopus'),
        ('https://www.un4seen.com/files/basswv24.zip','basswv24.zip','vendor/basswv'),
        ('https://www.un4seen.com/files/bassmix24.zip','bassmix24.zip','vendor/bassmix'),
        ('https://codeload.github.com/xbmc/audiodecoder.sacd/zip/refs/heads/Piers','sacd.zip','vendor/sacd'),
    ]
    with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
        futures = [pool.submit(fetch, *job) for job in jobs]
        for future in concurrent.futures.as_completed(futures):
            try: future.result()
            except Exception as error: print('FAILED: ' + str(error), flush=True)
